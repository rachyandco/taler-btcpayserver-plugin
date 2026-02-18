// Plugin entry point that registers services, payment handlers, and UI extensions.
// Inputs: dependency injection service collection and persisted server settings.
// Output: fully wired Taler payment methods available to BTCPay.
using System;
using System.Collections.Generic;
using System.Linq;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Services;
using BTCPayServer.Hosting;
using BTCPayServer.Payments;
using BTCPayServer.Payments.Bitcoin;
using BTCPayServer.Plugins.Taler.Configuration;
using BTCPayServer.Plugins.Taler.Services;
using BTCPayServer.Plugins.Taler.Services.Payments;
using BTCPayServer.Services.Rates;
using Microsoft.Extensions.DependencyInjection;

namespace BTCPayServer.Plugins.Taler;

public class TalerPlugin : BaseBTCPayServerPlugin
{
    private const string TalerLogoDataUri =
        "data:image/svg+xml;base64,PHN2ZyB3aWR0aD0iNjcwIiBoZWlnaHQ9IjY3MCIgdmVyc2lvbj0iMS4xIiB2aWV3Qm94PSIwIDAgMjAxIDIwMSIgeG1sbnM9Imh0dHA6Ly93d3cudzMub3JnLzIwMDAvc3ZnIj4KIDxjaXJjbGUgY3g9IjEwMC41IiBjeT0iMTAwLjUiIHI9IjEwMC41IiBmaWxsPSIjZmZmIiBzdHlsZT0icGFpbnQtb3JkZXI6c3Ryb2tlIG1hcmtlcnMgZmlsbCIvPgogPGcgdHJhbnNmb3JtPSJ0cmFuc2xhdGUoMS40LDU1Ljc1KSI+CiAgPGcgZmlsbD0iIzAwNDJiMyIgZmlsbC1ydWxlPSJldmVub2RkIiBzdHJva2Utd2lkdGg9Ii4zIj4KICAgPHBhdGggZD0ibTg2LjcgMS4xYzE1LjYgMCAyOSA5LjQgMzYgMjMuMmgtNS45YTM1LjEgMzUuMSAwIDAgMC0zMC4xLTE3LjhjLTE5LjcgMC0zNS43IDE3LjEtMzUuNyAzOC4yIDAgMTAuNCAzLjggMTkuNyAxMCAyNi42YTMxLjQgMzEuNCAwIDAgMS00LjIgMyA0NS4yIDQ1LjIgMCAwIDEtMTAuOC0yOS42YzAtMjQgMTguMi00My42IDQwLjctNDMuNnptMzUuOCA2NC4zYTQwLjQgNDAuNCAwIDAgMS0zOSAyMi44YzMtMS41IDYtMy41IDguNi01LjdhMzUuNiAzNS42IDAgMCAwIDI0LjYtMTcuMXoiLz4KICAgPHBhdGggZD0ibTY0LjIgMS4xIDMuMSAwLjFjLTMgMS42LTUuOSAzLjUtOC41IDUuOGEzNy41IDM3LjUgMCAwIDAtMzAuMiAzNy43YzAgMTQuMyA3LjMgMjYuNyAxOCAzMy4zYTI5LjYgMjkuNiAwIDAgMS04LjUgMC4yYy05LTgtMTQuNi0yMC0xNC42LTMzLjUgMC0yNCAxOC4yLTQzLjYgNDAuNy00My42em01LjQgODEuNGEzNS42IDM1LjYgMCAwIDAgMjQuNi0xNy4xaDUuOWE0MC40IDQwLjQgMCAwIDEtMzkgMjIuOGMzLTEuNSA1LjktMy41IDguNS01Ljd6bTI0LjgtNTguMmEzNyAzNyAwIDAgMC0xMi42LTEyLjggMjkuNiAyOS42IDAgMCAxIDguNS0wLjJjNCAzLjYgNy40IDggOS45IDEzeiIvPgogICA8cGF0aCBkPSJtNDEuOCAxLjFjMSAwIDIgMCAzLjEgMC4yLTMgMS41LTUuOSAzLjQtOC41IDUuNmEzNy41IDM3LjUgMCAwIDAtMzAuMyAzNy44YzAgMjEuMSAxNiAzOC4zIDM1LjcgMzguMyAxMi42IDAgMjMuNi03IDMwLTE3LjZoNS44YTQwLjQgNDAuNCAwIDAgMS0zNS44IDIzYy0yMi41IDAtNDAuOC0xOS42LTQwLjgtNDMuNyAwLTI0IDE4LjItNDMuNiA0MC43LTQzLjZ6bTMwLjEgMjMuMmEzOC4xIDM4LjEgMCAwIDAtNC41LTYuMWMxLjMtMS4yIDIuNy0yLjIgNC4zLTMgMi4zIDIuNyA0LjQgNS44IDYgOS4xeiIvPgogIDwvZz4KICA8cGF0aCBkPSJtNzYuMSAzNC40aDkuMnYtNWgtMjMuNHY1aDkuMXYyNmg1LjF6bTE2LjUgMTguNWgxMy43bDMgNy40aDUuM2wtMTIuNy0zMS4yaC00LjdsLTEyLjcgMzEuMmg1LjJ6bTExLjgtNC45aC05LjlsNS0xMi40em0xOS40LTE4LjZoLTQuNnYzMWgyMC42di01aC0xNnptNDIuNyAwaC0yMS41djMxaDIxLjZ2LTVoLTE2LjZ2LTguM2gxNC41di00LjloLTE0LjV2LThoMTYuNHptMjQuNyAxMC4xYzAgMS42LTAuNSAyLjgtMS42IDMuOHMtMi42IDEuNC00LjQgMS40aC03LjR2LTEwLjRoNy40YzEuOSAwIDMuNCAwLjQgNC40IDEuM3MxLjYgMi4yIDEuNiAzLjl6bTYgMjAuOC03LjctMTEuN2MxLTAuMyAxLjktMC43IDIuNy0xLjNhOC44IDguOCAwIDAgMCAzLjYtNC42YzAuNC0xIDAuNS0yLjIgMC41LTMuNSAwLTEuNS0wLjItMi45LTAuNy00LjFhOC40IDguNCAwIDAgMC0yLjEtMy4xYy0xLTAuOC0yLTEuNS0zLjQtMi0xLjMtMC40LTIuOC0wLjYtNC41LTAuNmgtMTIuOXYzMWg1di0xMWg2LjVsNyAxMC44eiIvPgogPC9nPgo8L3N2Zz4K";

    public override IBTCPayServerPlugin.PluginDependency[] Dependencies { get; } =
    [
        new IBTCPayServerPlugin.PluginDependency { Identifier = nameof(BTCPayServer), Condition = ">=2.0.0" }
    ];

    /// <summary>
    /// Registers plugin services into BTCPay dependency injection container.
    /// Inputs: host service collection.
    /// Output: mutated service registrations.
    /// </summary>
    public override void Execute(IServiceCollection serviceCollection)
    {
        RegisterServices(serviceCollection);
        base.Execute(serviceCollection);
    }

    /// <summary>
    /// Builds runtime asset config and registers all Taler payment integrations.
    /// Inputs: service collection + persisted server settings.
    /// Output: registered handlers, extensions, currencies, and hosted listener.
    /// </summary>
    private static void RegisterServices(IServiceCollection services)
    {
        var settingsRepository = services.BuildServiceProvider().GetService<ISettingsRepository>() ??
                                 throw new InvalidOperationException("serviceProvider.GetService<ISettingsRepository>()");

        var serverSettings = settingsRepository.GetSettingAsync<TalerServerSettings>(ServerSettingsKey).Result ?? new TalerServerSettings();
        var assets = serverSettings.Assets.Where(a => a.Enabled).ToArray();

        var configuration = new TalerPluginConfiguration
        {
            AssetConfigurationItems = assets.Select(asset =>
            {
                var config = new TalerAssetConfigurationItem
                {
                    AssetCode = asset.AssetCode,
                    DisplayName = string.IsNullOrWhiteSpace(asset.DisplayName) ? asset.AssetCode : asset.DisplayName,
                    Divisibility = asset.Divisibility,
                    Symbol = asset.Symbol,
                    CryptoImagePath = TalerLogoDataUri,
                    MerchantBaseUrl = serverSettings.MerchantBaseUrl,
                    MerchantPublicBaseUrl = serverSettings.MerchantPublicBaseUrl,
                    MerchantInstanceId = serverSettings.MerchantInstanceId,
                    ApiToken = serverSettings.ApiToken
                };
                return new KeyValuePair<PaymentMethodId, TalerAssetConfigurationItem>(config.GetPaymentMethodId(), config);
            }).ToDictionary(pair => pair.Key, pair => pair.Value)
        };

        services.AddSingleton(configuration);
        services.AddHttpClient<TalerMerchantClient>();
        services.AddSingleton<TalerMerchantClient>();
        services.AddHostedService<TalerPaymentListener>();

        foreach (var asset in configuration.AssetConfigurationItems.Values)
        {
            services.AddCurrencyData(new CurrencyData
            {
                Code = asset.AssetCode,
                Name = asset.DisplayName,
                Divisibility = asset.Divisibility,
                Symbol = asset.Symbol ?? asset.AssetCode,
                Crypto = true
            });

            var paymentMethodId = asset.GetPaymentMethodId();
            services.AddSingleton(provider => (IPaymentMethodHandler)ActivatorUtilities.CreateInstance(provider, typeof(TalerPaymentMethodHandler), asset));
            services.AddSingleton<IPaymentLinkExtension>(provider =>
                (IPaymentLinkExtension)ActivatorUtilities.CreateInstance(provider, typeof(TalerPaymentLinkExtension), paymentMethodId));
            services.AddSingleton(provider =>
                (ICheckoutModelExtension)ActivatorUtilities.CreateInstance(provider, typeof(TalerCheckoutModelExtension), asset));
            services.AddDefaultPrettyName(paymentMethodId, asset.DisplayName);
        }

        services.AddUIExtension("server-nav", "Taler/ServerNavTalerExtension");
        services.AddUIExtension("store-wallets-nav", "Taler/StoreWalletsNavTalerExtension");
        services.AddUIExtension("store-invoices-payments", "Taler/ViewTalerPaymentData");
        services.AddUIExtension("checkout-payment-method", "Taler/CheckoutPaymentMethod");
    }

    public static string ServerSettingsKey => "Taler_Server_Settings";
}
