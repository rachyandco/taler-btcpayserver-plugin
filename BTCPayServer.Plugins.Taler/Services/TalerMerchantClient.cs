// HTTP client wrapper around GNU Taler merchant APIs used by this plugin.
// Inputs: merchant base URL, instance credentials, and order/account identifiers.
// Output: typed responses for assets, bank accounts, orders, and provisioning actions.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace BTCPayServer.Plugins.Taler.Services;

public record TalerDiscoveredAsset(string AssetCode, string DisplayName, int Divisibility, string? Symbol);
public record TalerBankAccount(string PaytoUri, string HWire, bool Active);

public record TalerOrderStatus(string OrderId, string? TalerPayUri, bool Paid, decimal? Amount, string? Currency);
public record TalerConfig(bool SelfProvisioning);
public record TalerTokenResponse(string AccessToken);

public class TalerMerchantClient(HttpClient httpClient, ILogger<TalerMerchantClient> logger)
{
    /// <summary>
    /// Reads merchant wire accounts for one instance.
    /// Inputs: base URL, instance ID, API token, cancellation token.
    /// Output: list of payto accounts with active status and h_wire.
    /// </summary>
    public async Task<IReadOnlyList<TalerBankAccount>> GetBankAccountsAsync(string baseUrl, string instanceId, string apiToken, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, BuildInstancePrivateUri(baseUrl, instanceId, "accounts"));
        AddAuthorization(request, apiToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessStatusCode(response, "get bank accounts");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!json.RootElement.TryGetProperty("accounts", out var accountsProp) || accountsProp.ValueKind != JsonValueKind.Array)
            return Array.Empty<TalerBankAccount>();

        var result = new List<TalerBankAccount>();
        foreach (var account in accountsProp.EnumerateArray())
        {
            var payto = account.TryGetProperty("payto_uri", out var paytoProp) ? paytoProp.GetString() : null;
            var hWire = account.TryGetProperty("h_wire", out var hWireProp) ? hWireProp.GetString() : null;
            var active = account.TryGetProperty("active", out var activeProp) && activeProp.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(payto) || string.IsNullOrWhiteSpace(hWire))
                continue;
            result.Add(new TalerBankAccount(payto!, hWire!, active));
        }

        return result;
    }

    /// <summary>
    /// Adds a new bank account to merchant instance configuration.
    /// Inputs: backend coordinates, API token, payto URI, optional facade URL.
    /// Output: none; throws on HTTP/API errors.
    /// </summary>
    public async Task AddBankAccountAsync(string baseUrl, string instanceId, string apiToken, string paytoUri, string? creditFacadeUrl, CancellationToken cancellationToken)
    {
        var payload = new
        {
            payto_uri = paytoUri,
            credit_facade_url = string.IsNullOrWhiteSpace(creditFacadeUrl) ? null : creditFacadeUrl.Trim()
        };

        var request = new HttpRequestMessage(HttpMethod.Post, BuildInstancePrivateUri(baseUrl, instanceId, "accounts"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        AddAuthorization(request, apiToken);

        using var response = await httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessStatusCode(response, "add bank account");
    }

    /// <summary>
    /// Reads merchant global config to detect self-provisioning support.
    /// Inputs: base URL and cancellation token.
    /// Output: <see cref="TalerConfig"/> with self-provisioning flag.
    /// </summary>
    public async Task<TalerConfig> GetConfigAsync(string baseUrl, CancellationToken cancellationToken)
    {
        var uri = BuildUri(baseUrl, "config");
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
            return new TalerConfig(false);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var selfProvisioning = json.RootElement.TryGetProperty("have_self_provisioning", out var sp) && sp.ValueKind == JsonValueKind.True;
        return new TalerConfig(selfProvisioning);
    }

    /// <summary>
    /// Discovers merchant currencies from `/config`.
    /// Inputs: base URL and cancellation token.
    /// Output: normalized list of asset code/name/divisibility/symbol.
    /// </summary>
    public async Task<IReadOnlyList<TalerDiscoveredAsset>> GetCurrenciesAsync(string baseUrl, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return Array.Empty<TalerDiscoveredAsset>();

        var uri = BuildUri(baseUrl, "config");
        using var response = await httpClient.GetAsync(uri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Taler merchant /config returned {StatusCode}", response.StatusCode);
            return Array.Empty<TalerDiscoveredAsset>();
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        if (!json.RootElement.TryGetProperty("currencies", out var currencies) || currencies.ValueKind != JsonValueKind.Object)
            return Array.Empty<TalerDiscoveredAsset>();

        var result = new List<TalerDiscoveredAsset>();
        foreach (var currency in currencies.EnumerateObject())
        {
            var code = currency.Name;
            var spec = currency.Value;
            var name = spec.TryGetProperty("name", out var nameProp) ? nameProp.GetString() : code;
            var fraction = spec.TryGetProperty("fraction", out var fractionProp) ? fractionProp.GetInt32() : 2;
            var symbol = spec.TryGetProperty("symbol", out var symbolProp) ? symbolProp.GetString() : null;
            result.Add(new TalerDiscoveredAsset(code, string.IsNullOrWhiteSpace(name) ? code : name, fraction, symbol));
        }

        return result;
    }

    /// <summary>
    /// Creates merchant instance through management/self-provisioning API.
    /// Inputs: base URL, instance ID, password, cancellation token.
    /// Output: none; returns silently when instance already exists.
    /// </summary>
    public async Task CreateInstanceAsync(string baseUrl, string instanceId, string password, CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(new
        {
            id = instanceId,
            name = instanceId,
            auth = new
            {
                method = "token",
                password = password
            },
            // The API expects valid Location objects for setup.
            address = new { country = "ZZ" },
            jurisdiction = new { country = "ZZ" },
            // Required by newer merchant API.
            default_pay_delay = new { d_us = 900000000L }, // 15 minutes
            default_refund_delay = new { d_us = 604800000000L }, // 7 days
            default_wire_transfer_delay = new { d_us = 3600000000L }, // 1 hour
            use_stefan = false
        });

        var managementUri = BuildUri(baseUrl, "management/instances");
        using var primaryRequest = BuildJsonRequest(HttpMethod.Post, managementUri, payloadJson, string.Empty);
        using var primaryResponse = await httpClient.SendAsync(primaryRequest, cancellationToken);

        HttpResponseMessage responseToUse = primaryResponse;
        if ((int)primaryResponse.StatusCode == 404)
        {
            var legacyUri = BuildUri(baseUrl, "instances");
            using var legacyRequest = BuildJsonRequest(HttpMethod.Post, legacyUri, payloadJson, string.Empty);
            var legacyResponse = await httpClient.SendAsync(legacyRequest, cancellationToken);
            if (legacyResponse.IsSuccessStatusCode || (int)legacyResponse.StatusCode == 409)
            {
                responseToUse = legacyResponse;
            }
            else
            {
                legacyResponse.Dispose();
            }
        }

        if ((int)responseToUse.StatusCode == 409)
            return;

        await EnsureSuccessStatusCode(responseToUse, "create instance");
    }

    /// <summary>
    /// Creates API token for one merchant instance.
    /// Inputs: base URL, instance ID/password, token scope, cancellation token.
    /// Output: access token value returned by merchant backend.
    /// </summary>
    public async Task<TalerTokenResponse> CreateTokenAsync(string baseUrl, string instanceId, string password, string scope, CancellationToken cancellationToken)
    {
        var payload = new
        {
            scope,
            refreshable = false,
            // Request a non-expiring token; backend may still enforce policy limits.
            duration = new
            {
                d_us = "forever"
            }
        };

        var request = new HttpRequestMessage(HttpMethod.Post, BuildInstancePrivateUri(baseUrl, instanceId, "token"))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{instanceId}:{password}"));
        request.Headers.Authorization = AuthenticationHeaderValue.Parse($"Basic {basic}");

        using var response = await httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var token = json.RootElement.TryGetProperty("access_token", out var tokenProp) ? tokenProp.GetString() : null;
        if (string.IsNullOrWhiteSpace(token))
            throw new InvalidOperationException("Merchant backend did not return access_token");
        return new TalerTokenResponse(token!);
    }

    /// <summary>
    /// Creates merchant order for checkout payment.
    /// Inputs: backend URL, instance, API token, order metadata and amount.
    /// Output: created order ID string.
    /// </summary>
    public async Task<string> CreateOrderAsync(string baseUrl, string instanceId, string apiToken, string orderId, string summary, string amount, CancellationToken cancellationToken)
    {
        var payloadJson = JsonSerializer.Serialize(new
        {
            order_id = orderId,
            order = new
            {
                summary,
                amount
            }
        });

        var primaryUri = BuildInstancePrivateUri(baseUrl, instanceId, "orders");
        using var primaryRequest = BuildJsonRequest(HttpMethod.Post, primaryUri, payloadJson, apiToken);
        using var primaryResponse = await httpClient.SendAsync(primaryRequest, cancellationToken);

        HttpResponseMessage responseToUse = primaryResponse;
        if ((int)primaryResponse.StatusCode == 404)
        {
            var alternativeUri = BuildAlternativeInstancePrivateUri(baseUrl, instanceId, "orders");
            if (alternativeUri is not null && alternativeUri != primaryUri)
            {
                using var alternativeRequest = BuildJsonRequest(HttpMethod.Post, alternativeUri, payloadJson, apiToken);
                var alternativeResponse = await httpClient.SendAsync(alternativeRequest, cancellationToken);
                if (alternativeResponse.IsSuccessStatusCode)
                {
                    responseToUse = alternativeResponse;
                }
                else
                {
                    alternativeResponse.Dispose();
                }
            }
        }

        await EnsureSuccessStatusCode(responseToUse, "create order");

        await using var stream = await responseToUse.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (json.RootElement.TryGetProperty("order_id", out var orderIdProp))
            return orderIdProp.GetString() ?? orderId;

        return orderId;
    }

    /// <summary>
    /// Reads order status including payment state and pay URI.
    /// Inputs: backend URL, instance, API token, order ID.
    /// Output: <see cref="TalerOrderStatus"/> with paid flag and amount details.
    /// </summary>
    public async Task<TalerOrderStatus> GetOrderStatusAsync(string baseUrl, string instanceId, string apiToken, string orderId, CancellationToken cancellationToken)
    {
        var primaryUri = BuildInstancePrivateUri(baseUrl, instanceId, $"orders/{orderId}");
        using var primaryRequest = new HttpRequestMessage(HttpMethod.Get, primaryUri);
        AddAuthorization(primaryRequest, apiToken);
        using var primaryResponse = await httpClient.SendAsync(primaryRequest, cancellationToken);

        HttpResponseMessage responseToUse = primaryResponse;
        if ((int)primaryResponse.StatusCode == 404)
        {
            var alternativeUri = BuildAlternativeInstancePrivateUri(baseUrl, instanceId, $"orders/{orderId}");
            if (alternativeUri is not null && alternativeUri != primaryUri)
            {
                using var alternativeRequest = new HttpRequestMessage(HttpMethod.Get, alternativeUri);
                AddAuthorization(alternativeRequest, apiToken);
                var alternativeResponse = await httpClient.SendAsync(alternativeRequest, cancellationToken);
                if (alternativeResponse.IsSuccessStatusCode)
                {
                    responseToUse = alternativeResponse;
                }
                else
                {
                    alternativeResponse.Dispose();
                }
            }
        }

        await EnsureSuccessStatusCode(responseToUse, "get order status");

        await using var stream = await responseToUse.Content.ReadAsStreamAsync(cancellationToken);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        var root = json.RootElement;
        var paid = root.TryGetProperty("paid", out var paidProp) && paidProp.ValueKind == JsonValueKind.True;
        if (!paid &&
            root.TryGetProperty("order_status", out var orderStatusProp) &&
            orderStatusProp.ValueKind == JsonValueKind.String)
        {
            var orderStatus = orderStatusProp.GetString();
            paid = string.Equals(orderStatus, "paid", StringComparison.OrdinalIgnoreCase);
        }
        var payUri = root.TryGetProperty("taler_pay_uri", out var payUriProp) ? payUriProp.GetString() : null;
        decimal? amount = null;
        string? currency = null;

        if (root.TryGetProperty("amount", out var amountProp) && amountProp.ValueKind == JsonValueKind.String)
        {
            var amountString = amountProp.GetString();
            if (!string.IsNullOrWhiteSpace(amountString) && amountString.Contains(':'))
            {
                var parts = amountString.Split(':', 2);
                currency = parts[0];
                if (decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed))
                    amount = parsed;
            }
        }

        return new TalerOrderStatus(orderId, payUri, paid, amount, currency);
    }

    /// <summary>
    /// Builds absolute merchant URI from base + relative path.
    /// Inputs: base URL and relative endpoint string.
    /// Output: normalized absolute <see cref="Uri"/>.
    /// </summary>
    private static Uri BuildUri(string baseUrl, string relative)
    {
        var trimmed = baseUrl.TrimEnd('/') + "/";
        return new Uri(new Uri(trimmed), relative);
    }

    /// <summary>
    /// Builds preferred instance private API path.
    /// Inputs: base URL, instance ID, private endpoint suffix.
    /// Output: instance-private endpoint URI.
    /// </summary>
    private static Uri BuildInstancePrivateUri(string baseUrl, string instanceId, string relativePrivatePath)
    {
        if (IsInstanceBaseUrl(baseUrl))
            return BuildUri(baseUrl, $"private/{relativePrivatePath}");

        return BuildUri(baseUrl, $"instances/{instanceId}/private/{relativePrivatePath}");
    }

    /// <summary>
    /// Builds fallback private API path for alternate merchant URL layouts.
    /// Inputs: base URL, instance ID, private endpoint suffix.
    /// Output: alternate URI or null when not applicable.
    /// </summary>
    private static Uri? BuildAlternativeInstancePrivateUri(string baseUrl, string instanceId, string relativePrivatePath)
    {
        if (IsInstanceBaseUrl(baseUrl))
            return BuildUri(GetServerRootBaseUrl(baseUrl), $"instances/{instanceId}/private/{relativePrivatePath}");

        return BuildUri(baseUrl, $"private/{relativePrivatePath}");
    }

    /// <summary>
    /// Detects whether base URL already points to a specific instance path.
    /// Inputs: base URL string.
    /// Output: true when URL path ends with `/instances/{id}`.
    /// </summary>
    private static bool IsInstanceBaseUrl(string baseUrl)
    {
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri))
            return false;

        var segments = uri.AbsolutePath
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return segments.Length >= 2 &&
               segments[^2].Equals("instances", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Strips instance path from base URL and returns server root.
    /// Inputs: absolute base URL.
    /// Output: root URL with scheme/host/port and trailing slash.
    /// </summary>
    private static string GetServerRootBaseUrl(string baseUrl)
    {
        var uri = new Uri(baseUrl);
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return $"{uri.Scheme}://{uri.Host}{port}/";
    }

    /// <summary>
    /// Builds authenticated JSON HTTP request.
    /// Inputs: method, URI, serialized payload, and API token.
    /// Output: ready-to-send <see cref="HttpRequestMessage"/>.
    /// </summary>
    private static HttpRequestMessage BuildJsonRequest(HttpMethod method, Uri uri, string payloadJson, string apiToken)
    {
        var request = new HttpRequestMessage(method, uri)
        {
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json")
        };
        AddAuthorization(request, apiToken);
        return request;
    }

    /// <summary>
    /// Throws enriched exception when HTTP response is not successful.
    /// Inputs: response object and operation label.
    /// Output: none; method returns only on success status codes.
    /// </summary>
    private static async Task EnsureSuccessStatusCode(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = await response.Content.ReadAsStringAsync();
        var bodySnippet = body.Length <= 300 ? body : body[..300];
        throw new HttpRequestException(
            $"Taler merchant {operation} failed with {(int)response.StatusCode} ({response.StatusCode}) at {response.RequestMessage?.RequestUri}. Body: {bodySnippet}");
    }

    /// <summary>
    /// Applies bearer authorization header for merchant private APIs.
    /// Inputs: request object and token string.
    /// Output: mutated request headers.
    /// </summary>
    private static void AddAuthorization(HttpRequestMessage request, string apiToken)
    {
        if (string.IsNullOrWhiteSpace(apiToken))
            return;

        var token = apiToken.Trim();
        if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(token);
            return;
        }

        // Support both legacy secret-token credentials and modern bearer tokens.
        request.Headers.Authorization = AuthenticationHeaderValue.Parse($"Bearer {token}");
    }
}
