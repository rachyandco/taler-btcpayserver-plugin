// Prompt details stored on invoice for one Taler payment attempt.
// Inputs: merchant order creation response and asset metadata.
// Output: data used by checkout UI and payment listener polling.
namespace BTCPayServer.Plugins.Taler.Services.Payments;

public class TalerPaymentMethodDetails
{
    public required string OrderId { get; init; }
    public required string TalerPayUri { get; init; }
    public required string AssetCode { get; init; }
    public required decimal Amount { get; init; }
    public string? MerchantBaseUrl { get; init; }
    public string? MerchantInstanceId { get; init; }
}
