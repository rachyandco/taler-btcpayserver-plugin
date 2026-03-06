// Server admin controller for global Taler configuration and provisioning actions.
// Inputs: posted form values, merchant API responses, persisted settings.
// Output: Razor views and status messages for server-level Taler operations.
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using System.Text.RegularExpressions;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Filters;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Taler.Configuration;
using BTCPayServer.Plugins.Taler.Controllers.ViewModels;
using BTCPayServer.Plugins.Taler.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Taler.Controllers;

[Route("server/taler")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UITalerServerController(
    ISettingsRepository settingsRepository,
    TalerMerchantClient talerMerchantClient,
    ILogger<UITalerServerController> logger) : Controller
{
    private const string SecretMask = "***";
    private const string PendingRefundOrderIdKey = "Taler_PendingRefundOrderId";
    private const string OrderActionOrderIdKey = "Taler_OrderActionOrderId";
    private const string OrderActionMessageKey = "Taler_OrderActionMessage";
    private const string OrderActionSeverityKey = "Taler_OrderActionSeverity";

    [HttpGet]
    /// <summary>
    /// Loads current Taler server settings and bank account status for the admin page.
    /// Inputs: persisted settings and optional merchant connectivity.
    /// Output: settings view model rendered in the Taler server UI.
    /// </summary>
    public async Task<IActionResult> GetServerConfig(int ordersPage = 1)
    {
        var settings = await GetSettings();
        if (string.IsNullOrWhiteSpace(settings.MerchantBaseUrl))
            settings.MerchantBaseUrl = GetDefaultMerchantBaseUrl();
        if (string.IsNullOrWhiteSpace(settings.MerchantInstanceId))
            settings.MerchantInstanceId = "default";
        var vm = Map(settings);
        vm.OrdersPage = Math.Max(1, ordersPage);
        PopulateOrderUiState(vm);
        await PopulateBankAccounts(vm, settings.MerchantBaseUrl, settings.MerchantInstanceId, settings.ApiToken);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Saves server-level Taler settings entered in the admin form.
    /// Inputs: posted configuration values and existing secrets for mask handling.
    /// Output: redirect to settings page with success/error status message.
    /// </summary>
    public async Task<IActionResult> GetServerConfig(TalerServerConfigViewModel viewModel)
    {
        if (!ModelState.IsValid)
            return View(viewModel);

        var settings = await GetSettings();
        settings.MerchantBaseUrl = string.IsNullOrWhiteSpace(viewModel.MerchantBaseUrl) ? null : viewModel.MerchantBaseUrl.Trim();
        settings.MerchantPublicBaseUrl = string.IsNullOrWhiteSpace(viewModel.MerchantPublicBaseUrl) ? null : viewModel.MerchantPublicBaseUrl.Trim();
        settings.MerchantInstanceId = string.IsNullOrWhiteSpace(viewModel.MerchantInstanceId) ? null : viewModel.MerchantInstanceId.Trim();
        settings.InstancePassword = UpdateSecret(settings.InstancePassword, viewModel.InstancePassword);
        settings.ApiToken = UpdateSecret(settings.ApiToken, viewModel.ApiToken);
        settings.Assets = viewModel.Assets.Select(Map).ToList();

        await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Message = "Taler settings saved. Restart BTCPayServer to apply asset list changes.",
            Severity = StatusMessageModel.StatusSeverity.Success
        });

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("refresh-assets")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Queries merchant /config and merges discovered currencies into stored assets.
    /// Inputs: merchant base URL from form/settings.
    /// Output: updated server settings and redirect with operation result.
    /// </summary>
    public async Task<IActionResult> RefreshAssets(TalerServerConfigViewModel? form)
    {
        var settings = await GetSettings();
        var baseUrl = string.IsNullOrWhiteSpace(form?.MerchantBaseUrl)
            ? settings.MerchantBaseUrl
            : form!.MerchantBaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set a merchant base URL before fetching assets.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            var discovered = await talerMerchantClient.GetCurrenciesAsync(baseUrl, HttpContext.RequestAborted);
            foreach (var asset in discovered)
            {
                var existing = settings.Assets.FirstOrDefault(a => string.Equals(a.AssetCode, asset.AssetCode, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.DisplayName = asset.DisplayName;
                    existing.Divisibility = asset.Divisibility;
                    existing.Symbol = asset.Symbol;
                    continue;
                }

                settings.Assets.Add(new TalerAssetSettingsItem
                {
                    AssetCode = asset.AssetCode,
                    DisplayName = asset.DisplayName,
                    Divisibility = asset.Divisibility,
                    Symbol = asset.Symbol,
                    Enabled = false,
                    IsManual = false
                });
            }

            await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Assets refreshed from Taler merchant backend.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Could not reach merchant backend at {baseUrl}. {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }
        catch (Exception ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to fetch assets: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("init-instance")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Creates or validates a merchant instance using self-provisioning endpoints.
    /// Inputs: base URL, instance ID, and instance password.
    /// Output: persisted instance settings and status message.
    /// </summary>
    public async Task<IActionResult> InitInstance(TalerServerConfigViewModel? form)
    {
        var settings = await GetSettings();
        var baseUrl = string.IsNullOrWhiteSpace(form?.MerchantBaseUrl) ? settings.MerchantBaseUrl : form!.MerchantBaseUrl.Trim();
        var instanceId = string.IsNullOrWhiteSpace(form?.MerchantInstanceId)
            ? (string.IsNullOrWhiteSpace(settings.MerchantInstanceId) ? "default" : settings.MerchantInstanceId)
            : form!.MerchantInstanceId.Trim();
        var password = NormalizeSecretInput(form?.InstancePassword, settings.InstancePassword);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(password))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL and instance password before initializing.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            var config = await talerMerchantClient.GetConfigAsync(baseUrl, HttpContext.RequestAborted);
            if (!config.SelfProvisioning)
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "Merchant backend self-provisioning is disabled. Enable it in the merchant config.",
                    Severity = StatusMessageModel.StatusSeverity.Error
                });
                return RedirectToAction(nameof(GetServerConfig));
            }

            await talerMerchantClient.CreateInstanceAsync(baseUrl, instanceId, password!, HttpContext.RequestAborted);

            settings.MerchantBaseUrl = baseUrl;
            settings.MerchantInstanceId = instanceId;
            settings.InstancePassword = password;
            await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Instance {instanceId} created and saved.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to initialize instance: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }
        catch (Exception ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to initialize instance: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }
        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("generate-token")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Generates and stores a merchant API token for private API access.
    /// Inputs: base URL, instance ID, and instance password.
    /// Output: updated token in settings and redirect status.
    /// </summary>
    public async Task<IActionResult> GenerateToken(TalerServerConfigViewModel? form)
    {
        var settings = await GetSettings();
        var baseUrl = string.IsNullOrWhiteSpace(form?.MerchantBaseUrl) ? settings.MerchantBaseUrl : form!.MerchantBaseUrl.Trim();
        var instanceId = string.IsNullOrWhiteSpace(form?.MerchantInstanceId)
            ? (string.IsNullOrWhiteSpace(settings.MerchantInstanceId) ? "default" : settings.MerchantInstanceId)
            : form!.MerchantInstanceId.Trim();
        var password = NormalizeSecretInput(form?.InstancePassword, settings.InstancePassword);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(password))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL and instance password before generating a token.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            // We need account management permissions for UI provisioning in addition
            // to order creation permissions used at checkout.
            var token = await talerMerchantClient.CreateTokenAsync(baseUrl, instanceId, password!, "all", HttpContext.RequestAborted);
            settings.MerchantBaseUrl = baseUrl;
            settings.MerchantInstanceId = instanceId;
            settings.InstancePassword = password;
            settings.ApiToken = token.AccessToken;
            await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "API token generated and saved (scope: all).",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to generate token: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }
        catch (Exception ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to generate token: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("refresh-bank-accounts")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Fetches current merchant bank accounts to validate payout wiring setup.
    /// Inputs: resolved backend URL, instance, and API token.
    /// Output: redirect with success/error status.
    /// </summary>
    public async Task<IActionResult> RefreshBankAccounts(TalerServerConfigViewModel? form)
    {
        var settings = await GetSettings();
        var (baseUrl, instanceId, apiToken) = ResolveBackendAccess(form, settings);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL, instance ID and API token before refreshing bank accounts.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            var accounts = await talerMerchantClient.GetBankAccountsAsync(baseUrl, instanceId, apiToken, HttpContext.RequestAborted);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Fetched {accounts.Count} bank account(s).",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to refresh bank accounts: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("bank-accounts/add")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Adds a payto account to the merchant instance for settlement wiring.
    /// Inputs: payto URI, optional facade URL, and backend credentials.
    /// Output: redirect with operation status.
    /// </summary>
    public async Task<IActionResult> AddBankAccount(TalerAddBankAccountViewModel model, TalerServerConfigViewModel? form)
    {
        if (!ModelState.IsValid)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Invalid bank account entry.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        var settings = await GetSettings();
        var (baseUrl, instanceId, apiToken) = ResolveBackendAccess(form, settings);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL, instance ID and API token before adding a bank account.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            await talerMerchantClient.AddBankAccountAsync(baseUrl, instanceId, apiToken, model.PaytoUri.Trim(), model.CreditFacadeUrl, HttpContext.RequestAborted);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Bank account added to merchant instance.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to add bank account: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("bank-accounts/{hWire}/delete")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Deletes a bank account from the merchant instance by h_wire.
    /// Inputs: account h_wire and backend credentials from form/settings.
    /// Output: redirect with operation status.
    /// </summary>
    public async Task<IActionResult> DeleteBankAccount(string hWire, TalerServerConfigViewModel? form)
    {
        if (string.IsNullOrWhiteSpace(hWire))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Invalid bank account identifier.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        var settings = await GetSettings();
        var (baseUrl, instanceId, apiToken) = ResolveBackendAccess(form, settings);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL, instance ID and API token before deleting a bank account.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            await talerMerchantClient.DeleteBankAccountAsync(baseUrl, instanceId, apiToken, hWire.Trim(), HttpContext.RequestAborted);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Bank account deleted from merchant instance.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to delete bank account: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("orders/{orderId}/refund")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Triggers order refund through merchant private API.
    /// Inputs: order ID and backend credentials from form/settings.
    /// Output: redirect with operation status.
    /// </summary>
    public async Task<IActionResult> RefundOrder(
        string orderId,
        string? refundAmount,
        bool confirmRefund = false,
        bool cancelRefund = false,
        TalerServerConfigViewModel? form = null,
        int ordersPage = 1)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            SetOrderActionResult(orderId, "Invalid order identifier.", StatusMessageModel.StatusSeverity.Error);
            return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
        }

        if (cancelRefund)
        {
            ClearPendingRefund(orderId);
            return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
        }

        if (!confirmRefund)
        {
            SetPendingRefund(orderId);
            ClearOrderActionResult();
            return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
        }

        var settings = await GetSettings();
        var (baseUrl, instanceId, apiToken) = ResolveBackendAccess(form, settings);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
        {
            SetOrderActionResult(
                orderId,
                "Set merchant base URL, instance ID and API token before refunding an order.",
                StatusMessageModel.StatusSeverity.Error);
            return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
        }

        try
        {
            ClearPendingRefund(orderId);
            var orders = await talerMerchantClient.GetOrdersAsync(baseUrl, instanceId, apiToken, HttpContext.RequestAborted);
            var currentOrder = orders.FirstOrDefault(o => string.Equals(o.OrderId, orderId, StringComparison.OrdinalIgnoreCase));
            if (currentOrder is null)
            {
                SetOrderActionResult(orderId, $"Order {orderId} was not found.", StatusMessageModel.StatusSeverity.Error);
                return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
            }

            if (!currentOrder.Refundable || currentOrder.Wired || currentOrder.RefundPending)
            {
                SetOrderActionResult(
                    orderId,
                    $"Order {orderId} is no longer refundable (already wired, refund pending, or refund window expired).",
                    StatusMessageModel.StatusSeverity.Error);
                return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
            }

            var effectiveRefundAmount = string.IsNullOrWhiteSpace(refundAmount) ? null : refundAmount.Trim();
            if (string.IsNullOrWhiteSpace(effectiveRefundAmount))
            {
                // Fallback: resolve amount from current backend order snapshot.
                effectiveRefundAmount = currentOrder.Amount;
            }

            if (string.IsNullOrWhiteSpace(effectiveRefundAmount))
            {
                SetOrderActionResult(
                    orderId,
                    $"Could not determine refund amount for order {orderId}.",
                    StatusMessageModel.StatusSeverity.Error);
                return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
            }

            await talerMerchantClient.RefundOrderAsync(
                baseUrl,
                instanceId,
                apiToken,
                orderId.Trim(),
                effectiveRefundAmount,
                HttpContext.RequestAborted);
            SetOrderActionResult(orderId, $"Refund requested for order {orderId}.", StatusMessageModel.StatusSeverity.Success);
        }
        catch (HttpRequestException ex)
        {
            if (ex.Message.Contains("\"code\": 2169", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("past the wire transfer deadline", StringComparison.OrdinalIgnoreCase))
            {
                SetOrderActionResult(
                    orderId,
                    $"Order {orderId} is past wire deadline and cannot be refunded via Taler API.",
                    StatusMessageModel.StatusSeverity.Error);
                return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
            }

            var friendly = BuildFriendlyRefundError(orderId, ex.Message);
            SetOrderActionResult(orderId, friendly, StatusMessageModel.StatusSeverity.Error);
        }

        return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
    }

    [HttpPost("orders/{orderId}/abort")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Aborts failed order through merchant private API.
    /// Inputs: order ID and backend credentials from form/settings.
    /// Output: redirect with operation status.
    /// </summary>
    public async Task<IActionResult> AbortOrder(string orderId, TalerServerConfigViewModel? form, int ordersPage = 1)
    {
        if (string.IsNullOrWhiteSpace(orderId))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Invalid order identifier.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
        }

        var settings = await GetSettings();
        var (baseUrl, instanceId, apiToken) = ResolveBackendAccess(form, settings);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL, instance ID and API token before aborting an order.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
        }

        try
        {
            await talerMerchantClient.AbortOrderAsync(baseUrl, instanceId, apiToken, orderId.Trim(), HttpContext.RequestAborted);
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

        return RedirectToAction(nameof(GetServerConfig), new { ordersPage });
    }

    [HttpPost("kyc/accept-tos")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Accepts exchange terms-of-service for a KYC requirement.
    /// Inputs: exchange URL, requirement ID, ToS URL/text and backend access form values.
    /// Output: redirect with status message.
    /// </summary>
    public async Task<IActionResult> AcceptKycTerms(TalerAcceptKycTermsViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Invalid KYC terms submission.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            var acceptedTos = string.IsNullOrWhiteSpace(model.TosUrl) ? "accepted-via-btcpay" : model.TosUrl.Trim();
            await talerMerchantClient.AcceptExchangeTosAsync(
                model.ExchangeUrl.Trim(),
                model.RequirementId.Trim(),
                model.FormId.Trim(),
                acceptedTos,
                HttpContext.RequestAborted);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "KYC terms accepted. Refresh status to confirm readiness.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to accept KYC terms: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("assets/{assetCode}/delete")]
    [ValidateAntiForgeryToken]
    /// <summary>
    /// Removes a manual/discovered asset from server settings.
    /// Inputs: asset code from route.
    /// Output: updated settings and redirect status.
    /// </summary>
    public async Task<IActionResult> DeleteAsset(string assetCode)
    {
        var settings = await GetSettings();
        var existing = settings.Assets.FirstOrDefault(a => string.Equals(a.AssetCode, assetCode, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            settings.Assets.Remove(existing);
            await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);
        }

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Message = $"Asset {assetCode} removed.",
            Severity = StatusMessageModel.StatusSeverity.Success
        });

        return RedirectToAction(nameof(GetServerConfig));
    }

    /// <summary>
    /// Reads plugin server settings from BTCPay settings storage.
    /// Inputs: settings repository and plugin key.
    /// Output: current settings object or defaults.
    /// </summary>
    private async Task<TalerServerSettings> GetSettings()
    {
        return await settingsRepository.GetSettingAsync<TalerServerSettings>(TalerPlugin.ServerSettingsKey) ?? new TalerServerSettings();
    }

    /// <summary>
    /// Computes a default internal merchant URL based on runtime environment.
    /// Inputs: optional BTCPAY_TALER_MERCHANT_URL env var and container flag.
    /// Output: default merchant base URL string.
    /// </summary>
    private static string GetDefaultMerchantBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("BTCPAY_TALER_MERCHANT_URL");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            ? "http://taler-merchant:9966/"
            : "http://localhost:9966/";
    }

    /// <summary>
    /// Maps persisted settings to the server configuration view model.
    /// Inputs: <see cref="TalerServerSettings"/> values.
    /// Output: populated view model for Razor rendering.
    /// </summary>
    private static TalerServerConfigViewModel Map(TalerServerSettings settings)
    {
        return new TalerServerConfigViewModel
        {
            MerchantBaseUrl = settings.MerchantBaseUrl,
            MerchantPublicBaseUrl = settings.MerchantPublicBaseUrl,
            MerchantInstanceId = settings.MerchantInstanceId,
            InstancePassword = string.IsNullOrWhiteSpace(settings.InstancePassword) ? null : SecretMask,
            ApiToken = string.IsNullOrWhiteSpace(settings.ApiToken) ? null : SecretMask,
            Assets = settings.Assets.Select(Map).OrderBy(a => a.AssetCode, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    /// <summary>
    /// Resolves backend access values from posted form with fallback to stored settings.
    /// Inputs: optional form values and persisted server settings.
    /// Output: normalized tuple of base URL, instance ID, and API token.
    /// </summary>
    private static (string? BaseUrl, string? InstanceId, string? ApiToken) ResolveBackendAccess(TalerServerConfigViewModel? form, TalerServerSettings settings)
    {
        var baseUrl = string.IsNullOrWhiteSpace(form?.MerchantBaseUrl) ? settings.MerchantBaseUrl : form!.MerchantBaseUrl.Trim();
        var instanceId = string.IsNullOrWhiteSpace(form?.MerchantInstanceId)
            ? (string.IsNullOrWhiteSpace(settings.MerchantInstanceId) ? "default" : settings.MerchantInstanceId)
            : form!.MerchantInstanceId.Trim();
        var apiToken = NormalizeSecretInput(form?.ApiToken, settings.ApiToken);
        return (baseUrl, instanceId, apiToken);
    }

    /// <summary>
    /// Loads bank accounts from merchant backend and writes them into the view model.
    /// Inputs: resolved backend URL, instance, and API token.
    /// Output: <see cref="TalerServerConfigViewModel.BankAccounts"/> or error message.
    /// </summary>
    private async Task PopulateBankAccounts(TalerServerConfigViewModel vm, string? baseUrl, string? instanceId, string? apiToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
            return;

        try
        {
            var accounts = await talerMerchantClient.GetBankAccountsAsync(baseUrl, instanceId, apiToken, HttpContext.RequestAborted);
            vm.BankAccounts = accounts.Select(a => new TalerBankAccountViewModel
            {
                PaytoUri = a.PaytoUri,
                HWire = a.HWire,
                Active = a.Active
            }).ToList();
        }
        catch (Exception ex)
        {
            vm.BankAccountsError = ex.Message;
        }

        try
        {
            var kycEntries = await talerMerchantClient.GetKycAsync(baseUrl, instanceId, apiToken, HttpContext.RequestAborted);
            vm.KycEntries = kycEntries.Select(k => new TalerKycEntryViewModel
            {
                PaytoUri = k.PaytoUri,
                HWire = k.HWire,
                Status = k.Status,
                ExchangeUrl = k.ExchangeUrl,
                ExchangeCurrency = k.ExchangeCurrency,
                NoKeys = k.NoKeys,
                AuthConflict = k.AuthConflict,
                ExchangeHttpStatus = k.ExchangeHttpStatus,
                ExchangeCode = k.ExchangeCode,
                AccessToken = k.AccessToken,
                PaytoKycAuths = k.PaytoKycAuths.ToList(),
                Limits = k.Limits.Select(l => new TalerKycLimitViewModel
                {
                    OperationType = l.OperationType,
                    TimeframeMicros = l.TimeframeMicros,
                    Threshold = l.Threshold,
                    SoftLimit = l.SoftLimit
                }).ToList()
            }).ToList();

            foreach (var entry in vm.KycEntries.Where(e =>
                         string.Equals(e.Status, "kyc-required", StringComparison.OrdinalIgnoreCase) &&
                         !string.IsNullOrWhiteSpace(e.AccessToken)))
            {
                try
                {
                    var requirements = await talerMerchantClient.GetExchangeKycInfoAsync(
                        entry.ExchangeUrl,
                        entry.AccessToken!,
                        HttpContext.RequestAborted);

                    entry.Requirements = requirements.Select(r => new TalerExchangeKycRequirementViewModel
                    {
                        Form = r.Form,
                        Id = r.Id,
                        Description = r.Description,
                        TosUrl = r.TosUrl
                    }).ToList();
                }
                catch
                {
                    // Keep KYC row visible even if enrichment request fails.
                }
            }
        }
        catch (Exception ex)
        {
            vm.KycError = ex.Message;
        }

        try
        {
            logger.LogInformation(
                "Loading Taler orders for settings page. BaseUrl={BaseUrl} InstanceId={InstanceId} Page={Page}",
                baseUrl,
                instanceId,
                vm.OrdersPage);
            var orders = await talerMerchantClient.GetOrdersAsync(baseUrl, instanceId, apiToken, HttpContext.RequestAborted);
            vm.Orders = orders
                .Select(o => new TalerOrderViewModel
                {
                    OrderId = o.OrderId,
                    Paid = o.Paid,
                    Refundable = o.Refundable,
                    Wired = o.Wired,
                    RefundPending = o.RefundPending,
                    PaymentFailed = o.PaymentFailed,
                    OrderStatus = o.OrderStatus,
                    Amount = o.Amount,
                    RefundAmount = o.RefundAmount,
                    PendingRefundAmount = o.PendingRefundAmount,
                    CreatedAt = o.CreatedAt
                })
                .ToList();

            var ordered = vm.Orders
                .OrderByDescending(o => o.CreatedAt ?? DateTimeOffset.MinValue)
                .ThenByDescending(o => o.OrderId, StringComparer.OrdinalIgnoreCase)
                .ToList();

            vm.OrdersTotalCount = ordered.Count;
            vm.OrdersTotalPages = Math.Max(1, (vm.OrdersTotalCount + vm.OrdersPageSize - 1) / vm.OrdersPageSize);
            vm.OrdersPage = Math.Min(Math.Max(1, vm.OrdersPage), vm.OrdersTotalPages);
            vm.Orders = ordered
                .Skip((vm.OrdersPage - 1) * vm.OrdersPageSize)
                .Take(vm.OrdersPageSize)
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex,
                "Failed loading Taler orders for settings page. BaseUrl={BaseUrl} InstanceId={InstanceId}",
                baseUrl,
                instanceId);
            vm.OrdersError = ex.Message;
            vm.OrdersTotalCount = 0;
            vm.OrdersTotalPages = 1;
            vm.OrdersPage = 1;
        }
    }

    /// <summary>
    /// Reuses current secret when masked/empty input is posted from UI.
    /// Inputs: posted value and existing secret.
    /// Output: normalized secret used for transient operations.
    /// </summary>
    private static string? NormalizeSecretInput(string? input, string? currentValue)
    {
        if (string.IsNullOrWhiteSpace(input))
            return currentValue;

        var trimmed = input.Trim();
        if (trimmed == SecretMask)
            return currentValue;

        return trimmed;
    }

    /// <summary>
    /// Updates persisted secret with posted value while honoring mask semantics.
    /// Inputs: current secret and new form input.
    /// Output: new stored secret (or null if explicitly cleared).
    /// </summary>
    private static string? UpdateSecret(string? currentValue, string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        if (trimmed == SecretMask)
            return currentValue;

        return trimmed;
    }

    private void PopulateOrderUiState(TalerServerConfigViewModel vm)
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

    /// <summary>
    /// Converts persisted asset settings to a UI asset row.
    /// Inputs: <see cref="TalerAssetSettingsItem"/>.
    /// Output: <see cref="TalerAssetViewModel"/> for editing.
    /// </summary>
    private static TalerAssetViewModel Map(TalerAssetSettingsItem asset)
    {
        return new TalerAssetViewModel
        {
            AssetCode = asset.AssetCode,
            DisplayName = asset.DisplayName,
            Divisibility = asset.Divisibility,
            Symbol = asset.Symbol,
            Enabled = asset.Enabled,
            IsManual = asset.IsManual
        };
    }

    /// <summary>
    /// Converts UI asset row back to persisted asset settings.
    /// Inputs: <see cref="TalerAssetViewModel"/> values from form post.
    /// Output: sanitized <see cref="TalerAssetSettingsItem"/> for storage.
    /// </summary>
    private static TalerAssetSettingsItem Map(TalerAssetViewModel asset)
    {
        return new TalerAssetSettingsItem
        {
            AssetCode = asset.AssetCode.Trim(),
            DisplayName = asset.DisplayName.Trim(),
            Divisibility = asset.Divisibility,
            Symbol = string.IsNullOrWhiteSpace(asset.Symbol) ? null : asset.Symbol.Trim(),
            Enabled = asset.Enabled,
            IsManual = asset.IsManual
        };
    }
}
