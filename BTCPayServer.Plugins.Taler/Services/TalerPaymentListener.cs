// Background worker polling Taler merchant orders to settle BTCPay invoices.
// Inputs: monitored invoices, plugin asset config, and merchant status API.
// Output: persisted BTCPay payments + invoice update events when paid.
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

    /// <summary>
    /// Starts background polling loop when at least one Taler asset is enabled.
    /// Inputs: host cancellation token.
    /// Output: running loop task or no-op.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (talerPluginConfiguration.AssetConfigurationItems.Count == 0)
            return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _loop = Task.Run(() => Loop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Requests graceful shutdown of polling loop.
    /// Inputs: host cancellation token.
    /// Output: loop task completion.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();
        return _loop ?? Task.CompletedTask;
    }

    /// <summary>
    /// Main loop iterating all configured assets on a fixed interval.
    /// Inputs: cancellation token.
    /// Output: periodic merchant checks until cancellation.
    /// </summary>
    private async Task Loop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                foreach (var asset in talerPluginConfiguration.AssetConfigurationItems.Values)
                {
                    try
                    {
                        await CheckPaymentsForAsset(asset, token);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Error while checking Taler payments for asset {AssetCode}", asset.AssetCode);
                    }
                }
                await Task.Delay(TimeSpan.FromSeconds(15.0), token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                // Normal shutdown path.
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while checking Taler payments");
                await Task.Delay(TimeSpan.FromSeconds(15.0), token);
            }
        }
    }

    /// <summary>
    /// Checks unpaid monitored invoices for one asset and records settled payments.
    /// Inputs: asset config and cancellation token.
    /// Output: new BTCPay payments and invoice update events.
    /// </summary>
    private async Task CheckPaymentsForAsset(TalerAssetConfigurationItem asset, CancellationToken token)
    {
        var pmi = asset.GetPaymentMethodId();
        var handler = (TalerPaymentMethodHandler)handlers[pmi];
        InvoiceEntity[] monitoredInvoices;
        try
        {
            monitoredInvoices = await invoiceRepository.GetMonitoredInvoices(pmi, cancellationToken: token);
        }
        catch (Exception ex) when (IsTransientTimeout(ex, token))
        {
            logger.LogWarning(ex, "Timed out loading monitored invoices for {PaymentMethodId}; skipping this polling cycle", pmi);
            return;
        }

        var invoices = monitoredInvoices
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
            catch (TalerOrderNotFoundException)
            {
                var promptToDeactivate = invoice.GetPaymentPrompt(pmi);
                if (promptToDeactivate is not null && promptToDeactivate.Activated)
                {
                    promptToDeactivate.Inactive = true;
                    await invoiceRepository.UpdatePrompt(invoice.Id, promptToDeactivate);
                    eventAggregator.Publish(new InvoiceNeedUpdateEvent(invoice.Id));
                    logger.LogInformation(
                        "Deactivated stale Taler prompt for invoice {InvoiceId} and order {OrderId} because merchant reports unknown proposal",
                        invoice.Id,
                        details.OrderId);
                }
                else
                {
                    logger.LogDebug("Skipping unknown Taler order {OrderId}", details.OrderId);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to check Taler order {OrderId}", details.OrderId);
            }
        }
    }

    private static bool IsTransientTimeout(Exception exception, CancellationToken token)
    {
        if (token.IsCancellationRequested || exception is OperationCanceledException)
            return false;

        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TimeoutException)
                return true;
        }

        return false;
    }
}
