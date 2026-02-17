// Persisted server-wide settings edited from "Server settings -> Taler".
// Inputs: admin UI values and discovered/manual assets.
// Output: serialized configuration loaded on plugin startup.
using System.Collections.Generic;

namespace BTCPayServer.Plugins.Taler.Configuration;

public class TalerServerSettings
{
    public string? MerchantBaseUrl { get; set; }
    public string? MerchantPublicBaseUrl { get; set; }
    public string? MerchantInstanceId { get; set; }
    public string? ApiToken { get; set; }
    public string? InstancePassword { get; set; }
    public List<TalerAssetSettingsItem> Assets { get; set; } = [];
}

public class TalerAssetSettingsItem
{
    public string AssetCode { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Divisibility { get; set; }
    public string? Symbol { get; set; }
    public bool Enabled { get; set; }
    public bool IsManual { get; set; }
}
