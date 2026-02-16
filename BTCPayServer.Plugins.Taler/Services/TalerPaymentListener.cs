using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BTCPayServer.Client.Models;
using BTCPayServer.Data;
using BTCPayServer.Events;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Taler.Configuration;
using BTCPayServer.Plugins.Taler.Services.Payments;
using BTCPayServer.Services.Invoices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Taler.Services;

public class TalerPaymentListener(
    InvoiceRepository invoiceRepository,
    EventAggregator eventAggregator,
    TalerMerchantClient talerMerchantClient,
    TalerPluginConfiguration talerPluginConfiguration,
    ILogger<TalerPaymentListener> logger,
    PaymentMethodHandlerDictionary handlers,
    PaymentService paymentService) : IHostedService
{
    public static readonly List<InvoiceStatus> StatusToTrack =
    [
        InvoiceStatus.New,
        InvoiceStatus.Processing
    ];

    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (talerPluginConfiguration.AssetConfigurationItems.Count == 0)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => Loop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return _loop ?? Task.CompletedTask;
    }

    private async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var asset in talerPluginConfiguration.AssetConfigurationItems.Values)
                {
                    await CheckPaymentsForAsset(asset, token);
                }
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Normal shutdown path.
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while checking Taler payments");
                await Task.Delay(TimeSpan.FromSeconds(15), token);
            }
        }
    }

    private async Task CheckPaymentsForAsset(TalerAssetConfigurationItem asset, CancellationToken token)
    {
        var pmi = asset.GetPaymentMethodId();
        var handler = (TalerPaymentMethodHandler)handlers[pmi];
        var invoices = (await invoiceRepository.GetMonitoredInvoices(pmi, true, cancellationToken: token))
            .Where(i => StatusToTrack.Contains(i.Status))
            .Where(i => i.GetPaymentPrompt(pmi)?.Activated is true)
            .ToArray();

        if (invoices.Length == 0)
            return;

        foreach (var invoice in invoices)
        {
            var prompt = invoice.GetPaymentPrompt(pmi);
            if (prompt?.Details is null)
                continue;

            var details = handler.ParsePaymentPromptDetails(prompt.Details);
            if (details is null)
                continue;

            if (invoice.GetPayments(false).Any(p => p.PaymentMethodId == pmi && p.Id == details.OrderId))
                continue;

            try
            {
                var status = await talerMerchantClient.GetOrderStatusAsync(
                    details.MerchantBaseUrl ?? asset.MerchantBaseUrl ?? string.Empty,
                    details.MerchantInstanceId ?? asset.MerchantInstanceId ?? "default",
                    asset.ApiToken ?? string.Empty,
                    details.OrderId,
                    token);

                if (!status.Paid)
                    continue;

                var paymentData = new PaymentData
                {
                    Status = PaymentStatus.Settled,
                    Amount = details.Amount,
                    Created = DateTimeOffset.UtcNow,
                    Id = details.OrderId,
                    Currency = details.AssetCode,
                    InvoiceDataId = invoice.Id
                }.Set(invoice, handler, new TalerPaymentData
                {
                    OrderId = details.OrderId,
                    AssetCode = details.AssetCode,
                    Amount = details.Amount,
                    TalerPayUri = details.TalerPayUri,
                    MerchantBaseUrl = details.MerchantBaseUrl,
                    MerchantInstanceId = details.MerchantInstanceId
                });

                var payment = await paymentService.AddPayment(paymentData, [details.OrderId]);
                if (payment != null)
                    eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to check Taler order {OrderId}", details.OrderId);
            }
        }
    }
}
