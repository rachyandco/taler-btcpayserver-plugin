namespace BTCPayServer.Plugins.Taler.Services.Payments;

public class TalerPaymentData
{
    public required string OrderId { get; init; }
    public required string AssetCode { get; init; }
    public required decimal Amount { get; init; }
    public string? TalerPayUri { get; init; }
    public string? MerchantBaseUrl { get; init; }
    public string? MerchantInstanceId { get; init; }
}
