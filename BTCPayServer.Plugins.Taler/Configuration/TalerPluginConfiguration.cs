// Container object holding all enabled Taler assets for plugin registration.
// Inputs: computed at startup from server settings.
// Output: dictionary keyed by payment method ID for fast lookups.
using System.Collections.Generic;
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Taler.Configuration;

public class TalerPluginConfiguration
{
    public Dictionary<PaymentMethodId, TalerAssetConfigurationItem> AssetConfigurationItems { get; init; } = [];
}
