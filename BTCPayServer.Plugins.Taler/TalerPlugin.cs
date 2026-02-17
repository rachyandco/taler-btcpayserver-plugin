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
        "data:image/svg+xml;base64,PHN2ZyB4bWxucz0iaHR0cDovL3d3dy53My5vcmcvMjAwMC9zdmciIHZpZXdCb3g9IjAgMCAxMjggMTI4IiByb2xlPSJpbWciIGFyaWEtbGFiZWw9IkdOVSBUYWxlciI+PHJlY3Qgd2lkdGg9IjEyOCIgaGVpZ2h0PSIxMjgiIHJ4PSIyMCIgZmlsbD0iIzFmNmZlYiIvPjxjaXJjbGUgY3g9IjY0IiBjeT0iNjQiIHI9IjQ2IiBmaWxsPSIjZmZmIi8+PHBhdGggZD0iTTQwIDQzaDQ4djEwSDY5djM4SDU5VjUzSDQweiIgZmlsbD0iIzFmNmZlYiIvPjwvc3ZnPg==";

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
