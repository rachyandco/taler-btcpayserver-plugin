// View models used by the server-level Taler settings page and related forms.
// Inputs: values from settings storage and form posts.
// Output: strongly typed models rendered in Razor views and validated on submit.
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System;

namespace BTCPayServer.Plugins.Taler.Controllers.ViewModels;

public class TalerServerConfigViewModel
{
    [Display(Name = "Merchant base URL")]
    public string? MerchantBaseUrl { get; set; }

    [Display(Name = "Merchant public base URL")]
    public string? MerchantPublicBaseUrl { get; set; }

    [Display(Name = "Merchant instance ID")]
    public string? MerchantInstanceId { get; set; }

    [Display(Name = "Instance password")]
    public string? InstancePassword { get; set; }

    [Display(Name = "Merchant API token")]
    public string? ApiToken { get; set; }
    public List<TalerAssetViewModel> Assets { get; set; } = [];
    public List<TalerBankAccountViewModel> BankAccounts { get; set; } = [];
    public string? BankAccountsError { get; set; }
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
    public List<TalerKycEntryViewModel> KycEntries { get; set; } = [];
    public string? KycError { get; set; }
}

public class TalerAssetViewModel
{
    public string AssetCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Divisibility { get; set; }
    public string? Symbol { get; set; }
    public bool Enabled { get; set; }
    public bool IsManual { get; set; }
}

public class TalerBankAccountViewModel
{
    public string PaytoUri { get; set; } = "";
    public string HWire { get; set; } = "";
    public bool Active { get; set; }
}

public class TalerOrderViewModel
{
    public string OrderId { get; set; } = "";
    public bool Paid { get; set; }
    public bool Refundable { get; set; }
    public bool Wired { get; set; }
    public bool RefundPending { get; set; }
    public bool PaymentFailed { get; set; }
    public string? OrderStatus { get; set; }
    public string? Amount { get; set; }
    public string? RefundAmount { get; set; }
    public string? PendingRefundAmount { get; set; }
    public DateTimeOffset? CreatedAt { get; set; }
}

public class TalerKycEntryViewModel
{
    public string PaytoUri { get; set; } = "";
    public string HWire { get; set; } = "";
    public string Status { get; set; } = "";
    public string ExchangeUrl { get; set; } = "";
    public string ExchangeCurrency { get; set; } = "";
    public bool NoKeys { get; set; }
    public bool AuthConflict { get; set; }
    public int? ExchangeHttpStatus { get; set; }
    public int? ExchangeCode { get; set; }
    public string? AccessToken { get; set; }
    public List<string> PaytoKycAuths { get; set; } = [];
    public List<TalerKycLimitViewModel> Limits { get; set; } = [];
    public List<TalerExchangeKycRequirementViewModel> Requirements { get; set; } = [];
}

public class TalerKycLimitViewModel
{
    public string OperationType { get; set; } = "";
    public long TimeframeMicros { get; set; }
    public string Threshold { get; set; } = "";
    public bool SoftLimit { get; set; }
}

public class TalerExchangeKycRequirementViewModel
{
    public string Form { get; set; } = "";
    public string? Id { get; set; }
    public string Description { get; set; } = "";
    public string? TosUrl { get; set; }
}

public class TalerAddBankAccountViewModel
{
    [Required]
    [Display(Name = "Payto URI")]
    public string PaytoUri { get; set; } = "";

    [Display(Name = "Credit facade URL")]
    public string? CreditFacadeUrl { get; set; }
}

public class TalerAcceptKycTermsViewModel
{
    [Required]
    public string ExchangeUrl { get; set; } = "";

    [Required]
    public string RequirementId { get; set; } = "";

    [Required]
    public string FormId { get; set; } = "";

    public string? AccessToken { get; set; }

    public string? TosUrl { get; set; }
}
