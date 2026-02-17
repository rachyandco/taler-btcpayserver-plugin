// Store-level controller for enabling/disabling Taler assets per BTCPay store.
// Inputs: store data, configured plugin assets, and posted toggle values.
// Output: store wallet settings pages and persisted payment method config.
using System.Linq;
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
using BTCPayServer.Plugins.Taler.Services.Payments;
using BTCPayServer.Services.Invoices;
using BTCPayServer.Services.Stores;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Taler.Controllers;

[Route("stores/{storeId}/taler")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyStoreSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UITalerStoreController(
    StoreRepository storeRepository,
    PaymentMethodHandlerDictionary handlers,
    TalerPluginConfiguration pluginConfiguration) : Controller
{
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
    /// Displays one store Taler payment method configuration form.
    /// Inputs: payment method ID route value and store config.
    /// Output: edit view model or 404 when asset is unknown.
    /// </summary>
    public IActionResult GetStoreTalerPaymentMethod(PaymentMethodId paymentMethodId)
    {
        if (!pluginConfiguration.AssetConfigurationItems.TryGetValue(paymentMethodId, out var asset))
            return NotFound();

        var excludeFilters = StoreData.GetStoreBlob().GetExcludedPaymentMethods();
        var matchedPaymentMethodConfig = StoreData.GetPaymentMethodConfig<TalerPaymentMethodConfig>(paymentMethodId, handlers);

        return View(new EditTalerPaymentMethodViewModel
        {
            Enabled = matchedPaymentMethodConfig != null && !excludeFilters.Match(paymentMethodId),
            DisplayName = asset.DisplayName
        });
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
}
