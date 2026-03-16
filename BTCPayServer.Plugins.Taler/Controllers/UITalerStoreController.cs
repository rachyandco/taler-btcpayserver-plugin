// Store-level controller for enabling/disabling Taler assets per BTCPay store.
// Inputs: store data, configured plugin assets, and posted toggle values.
// Output: store wallet settings pages and persisted payment method config.
using System;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Client;
using BTCPayServer.Data;
using BTCPayServer.Filters;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Taler.Configuration;
using BTCPayServer.Plugins.Taler.Controllers.ViewModels;
using BTCPayServer.Plugins.Taler.Services;
using BTCPayServer.Plugins.Taler.Services.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Taler.Controllers;

[Route("stores/{storeId}/taler")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UITalerStoreController(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers,
    TalerPluginConfiguration pluginConfiguration,
    TalerMerchantClient talerMerchantClient,
    ILogger<UITalerStoreController> logger) : Controller
{
    private const string PendingRefundOrderIdKey = "TalerStore_PendingRefundOrderId";
    private const string OrderActionOrderIdKey = "TalerStore_OrderActionOrderId";
    private const string OrderActionMessageKey = "TalerStore_OrderActionMessage";
    private const string OrderActionSeverityKey = "TalerStore_OrderActionSeverity";

    /// <summary>
    /// Returns current store entity from request context.
    /// Inputs: active HTTP context store binding.
    /// Output: <see cref="StoreData"/> for authorization and config reads.
    /// </summary>
    private StoreData StoreData => HttpContext.GetStoreData();

    [HttpGet]
    /// <summary>
    /// Displays all available Taler assets for the selected store.
    /// Inputs: store context and plugin asset registry.
    /// Output: list view model rendered in store wallets UI.
    /// </summary>
    public IActionResult GetStoreTalerPaymentMethods()
    {
        var vm = GetVM(StoreData);
        return View(vm);
    }

    [NonAction]
    /// <summary>
    /// Builds store-facing Taler options from configured plugin assets.
    /// Inputs: store object and payment method handlers.
    /// Output: asset list with enabled/disabled flags for each configured Taler method.
    /// </summary>
    public ViewTalerStoreOptionsViewModel GetVM(StoreData storeData)
    {
        var excludeFilters = storeData.GetStoreBlob().GetExcludedPaymentMethods();
        var vm = new ViewTalerStoreOptionsViewModel();

        foreach (var item in pluginConfiguration.AssetConfigurationItems.Values)
        {
            var pmi = item.GetPaymentMethodId();
            var matchedPaymentMethod = storeData.GetPaymentMethodConfig<TalerPaymentMethodConfig>(pmi, handlers);
            vm.Items.Add(new TalerStoreOptionItemViewModel
            {
                PaymentMethodId = pmi,
                DisplayName = item.DisplayName,
                Enabled = matchedPaymentMethod != null && !excludeFilters.Match(pmi)
            });
        }

        return vm;
    }

    [HttpGet("{paymentMethodId}")]
    /// <summary>
    /// Displays one store Taler payment method configuration form along with its orders.
    /// Inputs: payment method ID route value, store config, and optional page number.
    /// Output: edit view model with orders or 404 when asset is unknown.
    /// </summary>
    public async Task<IActionResult> GetStoreTalerPaymentMethod(PaymentMethodId paymentMethodId, int ordersPage = 1)
    {
        if (!pluginConfiguration.AssetConfigurationItems.TryGetValue(paymentMethodId, out var asset))
            return NotFound();

        var excludeFilters = StoreData.GetStoreBlob().GetExcludedPaymentMethods();
        var matchedPaymentMethodConfig = StoreData.GetPaymentMethodConfig<TalerPaymentMethodConfig>(paymentMethodId, handlers);

        var vm = new EditTalerPaymentMethodViewModel
        {
            Enabled = matchedPaymentMethodConfig != null && !excludeFilters.Match(paymentMethodId),
            DisplayName = asset.DisplayName,
            PaymentMethodId = paymentMethodId.ToString(),
            OrdersPage = ordersPage
        };

        await PopulateOrders(vm, asset, ordersPage);
        PopulateOrderUiState(vm);
        return View(vm);
    }

    [HttpPost("{paymentMethodId}")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Saves enabled flag for one Taler method in the current store.
    /// Inputs: posted form model and payment method ID.
    /// Output: updated store config and redirect to edit page.
    /// </summary>
    public async Task<IActionResult> GetStoreTalerPaymentMethod(EditTalerPaymentMethodViewModel viewModel, PaymentMethodId paymentMethodId)
    {
        if (!pluginConfiguration.AssetConfigurationItems.TryGetValue(paymentMethodId, out var asset))
            return NotFound();

        var store = StoreData;
        var blob = store.GetStoreBlob();
        var currentPaymentMethodConfig = store.GetPaymentMethodConfig<TalerPaymentMethodConfig>(paymentMethodId, handlers);
        currentPaymentMethodConfig ??= new TalerPaymentMethodConfig();

        if (viewModel.Enabled == blob.IsExcluded(paymentMethodId))
        {
            blob.SetExcluded(paymentMethodId, !viewModel.Enabled);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"{asset.DisplayName} is now {(viewModel.Enabled ? "enabled" : "disabled")}",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }

        store.SetPaymentMethodConfig(handlers[paymentMethodId], currentPaymentMethodConfig);
        store.SetStoreBlob(blob);
        await storeRepository.UpdateStore(store);

        return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId = store.Id, paymentMethodId });
    }

    [HttpPost("{paymentMethodId}/orders/{orderId}/refund")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Triggers order refund through merchant private API.
    /// Inputs: payment method ID, order ID, optional refund amount and confirmation flag.
    /// Output: redirect with operation status.
    /// </summary>
    public async Task<IActionResult> RefundOrder(
        PaymentMethodId paymentMethodId,
        string orderId,
        string? refundAmount,
        bool confirmRefund = false,
        bool cancelRefund = false,
        int ordersPage = 1)
    {
        if (!pluginConfiguration.AssetConfigurationItems.TryGetValue(paymentMethodId, out var asset))
            return NotFound();

        var storeId = StoreData.Id;

        if (string.IsNullOrWhiteSpace(orderId))
        {
            SetOrderActionResult(orderId ?? "", "Invalid order identifier.", StatusMessageModel.StatusSeverity.Error);
            return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
        }

        if (cancelRefund)
        {
            ClearPendingRefund(orderId);
            return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
        }

        if (!confirmRefund)
        {
            SetPendingRefund(orderId);
            ClearOrderActionResult();
            return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
        }

        if (string.IsNullOrWhiteSpace(asset.MerchantBaseUrl) ||
            string.IsNullOrWhiteSpace(asset.MerchantInstanceId) ||
            string.IsNullOrWhiteSpace(asset.ApiToken))
        {
            SetOrderActionResult(orderId, "Merchant configuration incomplete — check server Taler settings.", StatusMessageModel.StatusSeverity.Error);
            return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
        }

        try
        {
            ClearPendingRefund(orderId);
            var orders = await talerMerchantClient.GetOrdersAsync(
                asset.MerchantBaseUrl, asset.MerchantInstanceId, asset.ApiToken, HttpContext.RequestAborted);
            var currentOrder = orders.FirstOrDefault(o => string.Equals(o.OrderId, orderId, StringComparison.OrdinalIgnoreCase));
            if (currentOrder is null)
            {
                SetOrderActionResult(orderId, $"Order {orderId} was not found.", StatusMessageModel.StatusSeverity.Error);
                return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
            }

            if (!currentOrder.Refundable || currentOrder.Wired || currentOrder.RefundPending)
            {
                SetOrderActionResult(orderId,
                    $"Order {orderId} is no longer refundable (already wired, refund pending, or refund window expired).",
                    StatusMessageModel.StatusSeverity.Error);
                return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
            }

            var effectiveRefundAmount = string.IsNullOrWhiteSpace(refundAmount) ? currentOrder.Amount : refundAmount.Trim();
            if (string.IsNullOrWhiteSpace(effectiveRefundAmount))
            {
                SetOrderActionResult(orderId, $"Could not determine refund amount for order {orderId}.", StatusMessageModel.StatusSeverity.Error);
                return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
            }

            await talerMerchantClient.RefundOrderAsync(
                asset.MerchantBaseUrl, asset.MerchantInstanceId, asset.ApiToken,
                orderId.Trim(), effectiveRefundAmount, HttpContext.RequestAborted);
            SetOrderActionResult(orderId, $"Refund requested for order {orderId}.", StatusMessageModel.StatusSeverity.Success);
        }
        catch (HttpRequestException ex)
        {
            if (ex.Message.Contains("\"code\": 2169", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("past the wire transfer deadline", StringComparison.OrdinalIgnoreCase))
            {
                SetOrderActionResult(orderId,
                    $"Order {orderId} is past wire deadline and cannot be refunded via Taler API.",
                    StatusMessageModel.StatusSeverity.Error);
                return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
            }

            var friendly = BuildFriendlyRefundError(orderId, ex.Message);
            SetOrderActionResult(orderId, friendly, StatusMessageModel.StatusSeverity.Error);
        }

        return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
    }

    [HttpPost("{paymentMethodId}/orders/{orderId}/abort")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Aborts a failed order through merchant private API.
    /// Inputs: payment method ID, order ID.
    /// Output: redirect with operation status.
    /// </summary>
    public async Task<IActionResult> AbortOrder(PaymentMethodId paymentMethodId, string orderId, int ordersPage = 1)
    {
        if (!pluginConfiguration.AssetConfigurationItems.TryGetValue(paymentMethodId, out var asset))
            return NotFound();

        var storeId = StoreData.Id;

        if (string.IsNullOrWhiteSpace(orderId))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Invalid order identifier.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
        }

        if (string.IsNullOrWhiteSpace(asset.MerchantBaseUrl) ||
            string.IsNullOrWhiteSpace(asset.MerchantInstanceId) ||
            string.IsNullOrWhiteSpace(asset.ApiToken))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Merchant configuration incomplete — check server Taler settings.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
        }

        try
        {
            await talerMerchantClient.AbortOrderAsync(
                asset.MerchantBaseUrl, asset.MerchantInstanceId, asset.ApiToken,
                orderId.Trim(), HttpContext.RequestAborted);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Abort requested for order {orderId}.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to abort order {orderId}: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetStoreTalerPaymentMethod), new { storeId, paymentMethodId, ordersPage });
    }

    private async Task PopulateOrders(EditTalerPaymentMethodViewModel vm, TalerAssetConfigurationItem asset, int ordersPage)
    {
        if (string.IsNullOrWhiteSpace(asset.MerchantBaseUrl) ||
            string.IsNullOrWhiteSpace(asset.MerchantInstanceId) ||
            string.IsNullOrWhiteSpace(asset.ApiToken))
        {
            vm.OrdersError = "Merchant not configured — set base URL, instance ID and API token in server Taler settings.";
            return;
        }

        try
        {
            logger.LogInformation(
                "Loading Taler orders for store wallet page. Asset={Asset} BaseUrl={BaseUrl}",
                asset.AssetCode, asset.MerchantBaseUrl);

            var orders = await talerMerchantClient.GetOrdersAsync(
                asset.MerchantBaseUrl, asset.MerchantInstanceId, asset.ApiToken, HttpContext.RequestAborted);

            var mapped = orders
                .Select(o => new TalerOrderViewModel
                {
                    OrderId = o.OrderId,
                    Paid = o.Paid,
                    Refundable = o.Refundable,
                    Wired = o.Wired,
                    RefundPending = o.RefundPending,
                    PaymentFailed = o.PaymentFailed,
                    Refunded = o.Refunded,
                    OrderStatus = o.OrderStatus,
                    Amount = o.Amount,
                    RefundAmount = o.RefundAmount,
                    PendingRefundAmount = o.PendingRefundAmount,
                    CreatedAt = o.CreatedAt,
                    OrderStatusUrl = o.OrderStatusUrl
                })
                .ToList();

            var ordered = mapped
                .OrderByDescending(o => o.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(o => o.OrderId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            vm.OrdersTotalCount = ordered.Count;
            vm.OrdersTotalPages = Math.Max(1, (vm.OrdersTotalCount + vm.OrdersPageSize - 1) / vm.OrdersPageSize);
            vm.OrdersPage = Math.Min(Math.Max(1, ordersPage), vm.OrdersTotalPages);
            vm.Orders = ordered
                .Skip((vm.OrdersPage - 1) * vm.OrdersPageSize)
                .Take(vm.OrdersPageSize)
                .ToList();

            var enrichTasks = vm.Orders
                .Where(o => string.IsNullOrWhiteSpace(o.OrderStatusUrl))
                .Select(async o =>
                {
                    try
                    {
                        o.OrderStatusUrl = await talerMerchantClient.GetOrderStatusUrlAsync(
                            asset.MerchantBaseUrl, asset.MerchantInstanceId, asset.ApiToken,
                            o.OrderId, HttpContext.RequestAborted);
                    }
                    catch
                    {
                        // best-effort; leave null
                    }
                });
            await Task.WhenAll(enrichTasks);
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                "Failed loading Taler orders for store wallet page. Asset={Asset}: {Error}",
                asset.AssetCode, ex.Message);
            vm.OrdersError = ex.Message;
        }
    }

    private void PopulateOrderUiState(EditTalerPaymentMethodViewModel vm)
    {
        vm.PendingRefundOrderId = TempData.TryGetValue(PendingRefundOrderIdKey, out var pending) ? pending?.ToString() : null;
        vm.OrderActionOrderId = TempData.TryGetValue(OrderActionOrderIdKey, out var orderId) ? orderId?.ToString() : null;
        vm.OrderActionMessage = TempData.TryGetValue(OrderActionMessageKey, out var message) ? message?.ToString() : null;
        vm.OrderActionSeverity = TempData.TryGetValue(OrderActionSeverityKey, out var severity) ? severity?.ToString() : null;
    }

    private void SetPendingRefund(string orderId)
    {
        TempData[PendingRefundOrderIdKey] = orderId;
    }

    private void ClearPendingRefund(string orderId)
    {
        if (TempData.TryGetValue(PendingRefundOrderIdKey, out var pending) &&
            string.Equals(pending?.ToString(), orderId, StringComparison.OrdinalIgnoreCase))
        {
            TempData.Remove(PendingRefundOrderIdKey);
        }
    }

    private void ClearOrderActionResult()
    {
        TempData.Remove(OrderActionOrderIdKey);
        TempData.Remove(OrderActionMessageKey);
        TempData.Remove(OrderActionSeverityKey);
    }

    private void SetOrderActionResult(string orderId, string message, StatusMessageModel.StatusSeverity severity)
    {
        TempData[OrderActionOrderIdKey] = orderId;
        TempData[OrderActionMessageKey] = message;
        TempData[OrderActionSeverityKey] = severity == StatusMessageModel.StatusSeverity.Success ? "success" : "error";
    }

    private static string BuildFriendlyRefundError(string orderId, string rawMessage)
    {
        if (rawMessage.Contains("\"code\": 22", StringComparison.OrdinalIgnoreCase) ||
            rawMessage.Contains("\"field\": \"reason\"", StringComparison.OrdinalIgnoreCase) ||
            rawMessage.Contains("\"field\": \"refund\"", StringComparison.OrdinalIgnoreCase))
        {
            return $"Refund request for order {orderId} was rejected by merchant (invalid refund payload).";
        }

        var hintMatch = Regex.Match(rawMessage, "\"hint\"\\s*:\\s*\"(?<hint>.*?)\"", RegexOptions.IgnoreCase);
        if (hintMatch.Success)
        {
            var hint = hintMatch.Groups["hint"].Value;
            if (!string.IsNullOrWhiteSpace(hint))
                return $"Refund failed for order {orderId}: {hint}";
        }

        return $"Refund failed for order {orderId}. Merchant backend returned an error.";
    }
}
