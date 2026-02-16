using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

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
    public string? InstancePasswordActual { get; set; }
    public string? ApiTokenActual { get; set; }

    public List<TalerAssetViewModel> Assets { get; set; } = [];
    public List<TalerBankAccountViewModel> BankAccounts { get; set; } = [];
    public string? BankAccountsError { get; set; }
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

public class TalerAddManualAssetViewModel
{
    [Required]
    [Display(Name = "Asset code")]
    public string AssetCode { get; set; } = "";

    [Required]
    [Display(Name = "Display name")]
    public string DisplayName { get; set; } = "";

    [Range(0, 18)]
    [Display(Name = "Divisibility")]
    public int Divisibility { get; set; }

    [Display(Name = "Symbol")]
    public string? Symbol { get; set; }

    [Display(Name = "Enable asset")]
    public bool Enabled { get; set; } = true;
}

public class TalerBankAccountViewModel
{
    public string PaytoUri { get; set; } = "";
    public string HWire { get; set; } = "";
    public bool Active { get; set; }
}

public class TalerAddBankAccountViewModel
{
    [Required]
    [Display(Name = "Payto URI")]
    public string PaytoUri { get; set; } = "";

    [Display(Name = "Credit facade URL")]
    public string? CreditFacadeUrl { get; set; }
}
