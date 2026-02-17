// Checkout extension that maps Taler payment details into BTCPay checkout model fields.
// Inputs: payment prompt data produced by Taler handler.
// Output: QR/link values (`InvoiceBitcoinUrl*`) shown on checkout page.
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Taler.Configuration;

namespace BTCPayServer.Plugins.Taler.Services.Payments;

public class TalerCheckoutModelExtension(
    TalerAssetConfigurationItem configurationItem,
    IEnumerable<IPaymentLinkExtension> paymentLinkExtensions) : ICheckoutModelExtension
{
    private readonly IPaymentLinkExtension _paymentLinkExtension = paymentLinkExtensions.Single(p => p.PaymentMethodId == configurationItem.GetPaymentMethodId());

    public PaymentMethodId PaymentMethodId { get; } = configurationItem.GetPaymentMethodId();
    public string Image => configurationItem.CryptoImagePath ?? string.Empty;
    public string Badge => "";

    /// <summary>
    /// Populates checkout model URL/QR fields for Taler payment prompts.
    /// Inputs: checkout context with Taler prompt details.
    /// Output: updated model consumed by standard BTCPay checkout component.
    /// </summary>
    public void ModifyCheckoutModel(CheckoutModelContext context)
    {
        if (context is not { Handler: TalerPaymentMethodHandler })
            return;

        context.Model.CheckoutBodyComponentName = BitcoinCheckoutModelExtension.CheckoutBodyComponentName;
        context.Model.InvoiceBitcoinUrl = _paymentLinkExtension.GetPaymentLink(context.Prompt, context.UrlHelper);
        context.Model.InvoiceBitcoinUrlQR = context.Model.InvoiceBitcoinUrl;
        context.Model.ShowPayInWalletButton = true;
        context.Model.PaymentMethodCurrency = configurationItem.DisplayName;
    }
}
