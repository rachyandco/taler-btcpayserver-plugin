using System;
using System.Globalization;
using System.Net.Http;
using System.Threading.Tasks;
using System.Threading;
using BTCPayServer.Data;
using BTCPayServer.Payments;
using BTCPayServer.Plugins.Taler.Configuration;
using BTCPayServer.Plugins.Taler.Services;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace BTCPayServer.Plugins.Taler.Services.Payments;

public class TalerPaymentMethodHandler(
    TalerAssetConfigurationItem configurationItem,
    TalerMerchantClient talerMerchantClient) : IPaymentMethodHandler
{
    public JsonSerializer Serializer { get; } = BlobSerializer.CreateSerializer().Serializer;

    public PaymentMethodId PaymentMethodId { get; } = configurationItem.GetPaymentMethodId();

    public Task BeforeFetchingRates(PaymentMethodContext context)
    {
        context.Prompt.Currency = configurationItem.AssetCode;
        context.Prompt.Divisibility = configurationItem.Divisibility;
        context.Prompt.RateDivisibility = null;
        return Task.CompletedTask;
    }

    public async Task ConfigurePrompt(PaymentMethodContext context)
    {
        if (string.IsNullOrWhiteSpace(configurationItem.MerchantBaseUrl))
            throw new PaymentMethodUnavailableException("Taler merchant backend is not configured");

        var instanceId = string.IsNullOrWhiteSpace(configurationItem.MerchantInstanceId) ? "default" : configurationItem.MerchantInstanceId;
        var due = context.Prompt.Calculate().Due;
        var amountValue = Math.Round(due, configurationItem.Divisibility, MidpointRounding.AwayFromZero);
        var amount = $"{configurationItem.AssetCode}:{amountValue.ToString(CultureInfo.InvariantCulture)}";
        var orderId = $"{Guid.NewGuid():N}-{configurationItem.AssetCode}";
        var summary = $"BTCPay payment ({configurationItem.AssetCode})";

        string createdOrderId;
        try
        {
            createdOrderId = await talerMerchantClient.CreateOrderAsync(
                configurationItem.MerchantBaseUrl,
                instanceId,
                configurationItem.ApiToken ?? string.Empty,
                orderId,
                summary,
                amount,
                CancellationToken.None);
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("code\": 2000", StringComparison.OrdinalIgnoreCase) ||
                                              ex.Message.Contains("merchant instance", StringComparison.OrdinalIgnoreCase))
        {
            throw new PaymentMethodUnavailableException(
                $"Taler instance '{instanceId}' was not found. Open Server settings -> Taler, initialize the instance, generate an API token, save, and restart BTCPay.");
        }
        catch (HttpRequestException ex) when (ex.Message.Contains("code\": 2500", StringComparison.OrdinalIgnoreCase) ||
                                              ex.Message.Contains("bank accounts configured", StringComparison.OrdinalIgnoreCase))
        {
            throw new PaymentMethodUnavailableException(
                $"Taler instance '{instanceId}' has no active bank account. Open Server settings -> Taler, add a bank account, then retry.");
        }

        var status = await talerMerchantClient.GetOrderStatusAsync(
            configurationItem.MerchantBaseUrl,
            instanceId,
            configurationItem.ApiToken ?? string.Empty,
            createdOrderId,
            CancellationToken.None);

        if (string.IsNullOrWhiteSpace(status.TalerPayUri))
            throw new PaymentMethodUnavailableException("Taler pay URI not returned by merchant backend");

        var talerPayUri = RewritePublicPayUri(status.TalerPayUri!, configurationItem.MerchantPublicBaseUrl);

        context.Prompt.PaymentMethodFee = 0;
        context.Prompt.Details = JObject.FromObject(new TalerPaymentMethodDetails
        {
            OrderId = createdOrderId,
            TalerPayUri = talerPayUri,
            AssetCode = configurationItem.AssetCode,
            Amount = amountValue,
            MerchantBaseUrl = configurationItem.MerchantBaseUrl,
            MerchantInstanceId = instanceId
        }, Serializer);
    }

    object IPaymentMethodHandler.ParsePaymentMethodConfig(JToken config)
    {
        return ParsePaymentMethodConfig(config);
    }

    object IPaymentMethodHandler.ParsePaymentPromptDetails(JToken details)
    {
        return ParsePaymentPromptDetails(details)!;
    }

    object IPaymentMethodHandler.ParsePaymentDetails(JToken details)
    {
        return ParsePaymentDetails(details);
    }

    private static TalerPaymentMethodConfig ParsePaymentMethodConfig(JToken config)
    {
        return config.ToObject<TalerPaymentMethodConfig>(BlobSerializer.CreateSerializer().Serializer) ??
               throw new FormatException($"Invalid {nameof(TalerPaymentMethodHandler)} config");
    }

    public TalerPaymentMethodDetails? ParsePaymentPromptDetails(JToken details)
    {
        return details.ToObject<TalerPaymentMethodDetails>(Serializer);
    }

    public TalerPaymentData ParsePaymentDetails(JToken details)
    {
        return details.ToObject<TalerPaymentData>(Serializer) ??
               throw new FormatException($"Invalid {nameof(TalerPaymentMethodHandler)} payment details");
    }

    private static string RewritePublicPayUri(string talerPayUri, string? publicBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(publicBaseUrl))
            return NormalizeToWalletPayUri(talerPayUri);

        const string talerPrefix = "taler+";
        var hasTalerPrefix = talerPayUri.StartsWith(talerPrefix, StringComparison.OrdinalIgnoreCase);
        var rawUri = hasTalerPrefix ? talerPayUri[talerPrefix.Length..] : talerPayUri;
        if (rawUri.StartsWith("taler://", StringComparison.OrdinalIgnoreCase))
            return talerPayUri;

        if (!Uri.TryCreate(publicBaseUrl, UriKind.Absolute, out var publicUri))
            return NormalizeToWalletPayUri(talerPayUri);

        string pathAndQuery;
        var markerIndex = rawUri.IndexOf("/instances/", StringComparison.OrdinalIgnoreCase);
        if (markerIndex >= 0)
        {
            pathAndQuery = rawUri[markerIndex..];
        }
        else if (Uri.TryCreate(rawUri, UriKind.Absolute, out var sourceUri))
        {
            pathAndQuery = sourceUri.PathAndQuery;
        }
        else
        {
            return talerPayUri;
        }

        var rebuiltUri = new Uri(publicUri, pathAndQuery.TrimStart('/'));
        var rebuilt = rebuiltUri.ToString();
        return NormalizeToWalletPayUri(hasTalerPrefix ? $"{talerPrefix}{rebuilt}" : rebuilt);
    }

    private static string NormalizeToWalletPayUri(string uriValue)
    {
        const string talerPrefix = "taler+";
        if (uriValue.StartsWith("taler://", StringComparison.OrdinalIgnoreCase))
            return uriValue;

        var raw = uriValue.StartsWith(talerPrefix, StringComparison.OrdinalIgnoreCase)
            ? uriValue[talerPrefix.Length..]
            : uriValue;
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
            return uriValue;
        if (!parsed.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !parsed.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return uriValue;

        var hostPort = parsed.IsDefaultPort ? parsed.Host : $"{parsed.Host}:{parsed.Port}";
        return $"taler://pay/{hostPort}{parsed.PathAndQuery}";
    }
}
