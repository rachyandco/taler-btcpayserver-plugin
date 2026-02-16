using BTCPayServer.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Taler.Services.Payments;

public class TalerPaymentLinkExtension(PaymentMethodId paymentMethodId) : IPaymentLinkExtension
{
    public PaymentMethodId PaymentMethodId { get; } = paymentMethodId;

    public string? GetPaymentLink(PaymentPrompt prompt, IUrlHelper? urlHelper)
    {
        if (prompt.Details is null)
            return null;

        var details = prompt.Details.ToObject<TalerPaymentMethodDetails>();
        return details?.TalerPayUri;
    }
}
