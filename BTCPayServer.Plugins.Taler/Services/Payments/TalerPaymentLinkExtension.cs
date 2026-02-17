// Payment link extension exposing Taler URI from prompt details to checkout.
// Inputs: payment prompt details emitted by Taler handler.
// Output: wallet link string used by BTCPay checkout buttons/QR.
using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Taler.Services.Payments;

public class TalerPaymentLinkExtension(PaymentMethodId paymentMethodId) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;

    /// <summary>
    /// Extracts Taler pay URI from prompt details for checkout rendering.
    /// Inputs: payment prompt and optional URL helper.
    /// Output: Taler URI string or null when details are missing.
    /// </summary>
    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        if (prompt.Details is null)
            return null;

        var details = prompt.Details.ToObject<TalerPaymentMethodDetails>();
        return details?.TalerPayUri;
    }
}
