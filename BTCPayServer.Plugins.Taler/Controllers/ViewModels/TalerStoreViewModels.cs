// View models used by store wallet pages for Taler payment method toggles.
// Inputs: store payment configs and enabled plugin assets.
// Output: UI-friendly structures for list and edit screens.
using System.Collections.Generic;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Taler.Controllers.ViewModels;

public class ViewTalerStoreOptionsViewModel
{
    public List<TalerStoreOptionItemViewModel> Items { get; set; } = [];
}

public class TalerStoreOptionItemViewModel
{
    public required PaymentMethodId PaymentMethodId { get; init; }
    public required string DisplayName { get; init; }
    public bool Enabled { get; init; }
}

public class EditTalerPaymentMethodViewModel
{
    public bool Enabled { get; set; }
    public string DisplayName { get; set; } = "";
}
