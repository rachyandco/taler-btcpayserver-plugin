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

    public PaymentMethodId GetPaymentMethodId() => new($"{AssetCode}-{Constants.TalerPaymentType}");
}
