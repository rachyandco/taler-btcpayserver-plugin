using System.Collections.Generic;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Taler.Configuration;

public class TalerPluginConfiguration
{
    public Dictionary<PaymentMethodId, TalerAssetConfigurationItem> AssetConfigurationItems { get; init; } = [];
}
