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
    private StoreData StoreData => HttpContext.GetStoreData();

    [HttpGet]
    public IActionResult GetStoreTalerPaymentMethods()
    {
        var vm = GetVM(StoreData);
        return View(vm);
    }

    [NonAction]
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
