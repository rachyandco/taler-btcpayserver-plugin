// Runtime asset configuration injected into payment handlers at startup.
// Inputs: persisted server settings + enabled assets.
// Output: immutable per-asset config used during checkout and payment polling.
using BTCPayServer.Payments;

namespace BTCPayServer.Plugins.Taler.Configuration;

public record TalerAssetConfigurationItem
{
    public required string AssetCode { get; init; }
    public required string DisplayName { get; init; }
    public required int Divisibility { get; init; }
    public string? Symbol { get; init; }
    public string? CryptoImagePath { get; init; }
    public string? MerchantBaseUrl { get; init; }
    public string? MerchantPublicBaseUrl { get; init; }
    public string? MerchantInstanceId { get; init; }
    public string? ApiToken { get; init; }

    /// <summary>
    /// Builds the BTCPay payment method identifier from the Taler asset code.
    /// Input: current <see cref="AssetCode"/> value.
    /// Output: payment method ID like "CHF-Taler".
    /// </summary>
    public PaymentMethodId GetPaymentMethodId() => new($"{AssetCode}-{Constants.TalerPaymentType}");
}
