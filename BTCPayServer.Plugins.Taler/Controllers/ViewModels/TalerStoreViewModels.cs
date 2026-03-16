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
    public string PaymentMethodId { get; set; } = "";
    public List<TalerOrderViewModel> Orders { get; set; } = [];
    public string? OrdersError { get; set; }
    public int OrdersPage { get; set; } = 1;
    public int OrdersPageSize { get; set; } = 20;
    public int OrdersTotalCount { get; set; }
    public int OrdersTotalPages { get; set; } = 1;
    public string? PendingRefundOrderId { get; set; }
    public string? OrderActionOrderId { get; set; }
    public string? OrderActionMessage { get; set; }
    public string? OrderActionSeverity { get; set; }
}
