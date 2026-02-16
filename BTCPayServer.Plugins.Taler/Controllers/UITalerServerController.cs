using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using BTCPayServer.Abstractions.Constants;
using BTCPayServer.Abstractions.Extensions;
using BTCPayServer.Abstractions.Models;
using BTCPayServer.Abstractions.Contracts;
using BTCPayServer.Filters;
using BTCPayServer.Client;
using BTCPayServer.Plugins.Taler.Configuration;
using BTCPayServer.Plugins.Taler.Controllers.ViewModels;
using BTCPayServer.Plugins.Taler.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace BTCPayServer.Plugins.Taler.Controllers;

[Route("server/taler")]
[Authorize(AuthenticationSchemes = AuthenticationSchemes.Cookie)]
[Authorize(Policy = Policies.CanModifyServerSettings, AuthenticationSchemes = AuthenticationSchemes.Cookie)]
public class UITalerServerController(
    ISettingsRepository settingsRepository,
    TalerMerchantClient talerMerchantClient) : Controller
{
    private const string SecretMask = "***";

    [HttpGet]
    public async Task<IActionResult> GetServerConfig()
    {
        var settings = await GetSettings();
        if (string.IsNullOrWhiteSpace(settings.MerchantBaseUrl))
            settings.MerchantBaseUrl = GetDefaultMerchantBaseUrl();
        if (string.IsNullOrWhiteSpace(settings.MerchantInstanceId))
            settings.MerchantInstanceId = "default";
        var vm = Map(settings);
        await PopulateBankAccounts(vm, settings.MerchantBaseUrl, settings.MerchantInstanceId, settings.ApiToken);
        return View(vm);
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GetServerConfig(TalerServerConfigViewModel viewModel)
    {
        if (!ModelState.IsValid)
            return View(viewModel);

        var settings = await GetSettings();
        settings.MerchantBaseUrl = string.IsNullOrWhiteSpace(viewModel.MerchantBaseUrl) ? null : viewModel.MerchantBaseUrl.Trim();
        settings.MerchantPublicBaseUrl = string.IsNullOrWhiteSpace(viewModel.MerchantPublicBaseUrl) ? null : viewModel.MerchantPublicBaseUrl.Trim();
        settings.MerchantInstanceId = string.IsNullOrWhiteSpace(viewModel.MerchantInstanceId) ? null : viewModel.MerchantInstanceId.Trim();
        settings.InstancePassword = UpdateSecret(settings.InstancePassword, viewModel.InstancePassword);
        settings.ApiToken = UpdateSecret(settings.ApiToken, viewModel.ApiToken);
        settings.Assets = viewModel.Assets.Select(Map).ToList();

        await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Message = "Taler settings saved. Restart BTCPayServer to apply asset list changes.",
            Severity = StatusMessageModel.StatusSeverity.Success
        });

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("refresh-assets")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshAssets(TalerServerConfigViewModel? form)
    {
        var settings = await GetSettings();
        var baseUrl = string.IsNullOrWhiteSpace(form?.MerchantBaseUrl)
            ? settings.MerchantBaseUrl
            : form!.MerchantBaseUrl.Trim();
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set a merchant base URL before fetching assets.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            var discovered = await talerMerchantClient.GetCurrenciesAsync(baseUrl, HttpContext.RequestAborted);
            foreach (var asset in discovered)
            {
                var existing = settings.Assets.FirstOrDefault(a => string.Equals(a.AssetCode, asset.AssetCode, StringComparison.OrdinalIgnoreCase));
                if (existing is not null)
                {
                    existing.DisplayName = asset.DisplayName;
                    existing.Divisibility = asset.Divisibility;
                    existing.Symbol = asset.Symbol;
                    continue;
                }

                settings.Assets.Add(new TalerAssetSettingsItem
                {
                    AssetCode = asset.AssetCode,
                    DisplayName = asset.DisplayName,
                    Divisibility = asset.Divisibility,
                    Symbol = asset.Symbol,
                    Enabled = false,
                    IsManual = false
                });
            }

            await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Assets refreshed from Taler merchant backend.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Could not reach merchant backend at {baseUrl}. {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }
        catch (Exception ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to fetch assets: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("init-instance")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> InitInstance(TalerServerConfigViewModel? form)
    {
        var settings = await GetSettings();
        var baseUrl = string.IsNullOrWhiteSpace(form?.MerchantBaseUrl) ? settings.MerchantBaseUrl : form!.MerchantBaseUrl.Trim();
        var instanceId = string.IsNullOrWhiteSpace(form?.MerchantInstanceId)
            ? (string.IsNullOrWhiteSpace(settings.MerchantInstanceId) ? "default" : settings.MerchantInstanceId)
            : form!.MerchantInstanceId.Trim();
        var password = NormalizeSecretInput(form?.InstancePassword, settings.InstancePassword);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(password))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL and instance password before initializing.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            var config = await talerMerchantClient.GetConfigAsync(baseUrl, HttpContext.RequestAborted);
            if (!config.SelfProvisioning)
            {
                TempData.SetStatusMessageModel(new StatusMessageModel
                {
                    Message = "Merchant backend self-provisioning is disabled. Enable it in the merchant config.",
                    Severity = StatusMessageModel.StatusSeverity.Error
                });
                return RedirectToAction(nameof(GetServerConfig));
            }

            await talerMerchantClient.CreateInstanceAsync(baseUrl, instanceId, password!, HttpContext.RequestAborted);

            settings.MerchantBaseUrl = baseUrl;
            settings.MerchantInstanceId = instanceId;
            settings.InstancePassword = password;
            await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Instance {instanceId} created and saved.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to initialize instance: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }
        catch (Exception ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to initialize instance: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }
        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("generate-token")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateToken(TalerServerConfigViewModel? form)
    {
        var settings = await GetSettings();
        var baseUrl = string.IsNullOrWhiteSpace(form?.MerchantBaseUrl) ? settings.MerchantBaseUrl : form!.MerchantBaseUrl.Trim();
        var instanceId = string.IsNullOrWhiteSpace(form?.MerchantInstanceId)
            ? (string.IsNullOrWhiteSpace(settings.MerchantInstanceId) ? "default" : settings.MerchantInstanceId)
            : form!.MerchantInstanceId.Trim();
        var password = NormalizeSecretInput(form?.InstancePassword, settings.InstancePassword);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(password))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL and instance password before generating a token.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            // We need account management permissions for UI provisioning in addition
            // to order creation permissions used at checkout.
            var token = await talerMerchantClient.CreateTokenAsync(baseUrl, instanceId, password!, "all", HttpContext.RequestAborted);
            settings.MerchantBaseUrl = baseUrl;
            settings.MerchantInstanceId = instanceId;
            settings.InstancePassword = password;
            settings.ApiToken = token.AccessToken;
            await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "API token generated and saved (scope: all).",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to generate token: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }
        catch (Exception ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to generate token: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("refresh-bank-accounts")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RefreshBankAccounts(TalerServerConfigViewModel? form)
    {
        var settings = await GetSettings();
        var (baseUrl, instanceId, apiToken) = ResolveBackendAccess(form, settings);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL, instance ID and API token before refreshing bank accounts.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            var accounts = await talerMerchantClient.GetBankAccountsAsync(baseUrl, instanceId, apiToken, HttpContext.RequestAborted);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Fetched {accounts.Count} bank account(s).",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to refresh bank accounts: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("bank-accounts/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddBankAccount(TalerAddBankAccountViewModel model, TalerServerConfigViewModel? form)
    {
        if (!ModelState.IsValid)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Invalid bank account entry.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        var settings = await GetSettings();
        var (baseUrl, instanceId, apiToken) = ResolveBackendAccess(form, settings);
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Set merchant base URL, instance ID and API token before adding a bank account.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        try
        {
            await talerMerchantClient.AddBankAccountAsync(baseUrl, instanceId, apiToken, model.PaytoUri.Trim(), model.CreditFacadeUrl, HttpContext.RequestAborted);
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Bank account added to merchant instance.",
                Severity = StatusMessageModel.StatusSeverity.Success
            });
        }
        catch (HttpRequestException ex)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = $"Failed to add bank account: {ex.Message}",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
        }

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("assets/add")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddManualAsset(TalerAddManualAssetViewModel model)
    {
        if (!ModelState.IsValid)
        {
            TempData.SetStatusMessageModel(new StatusMessageModel
            {
                Message = "Invalid manual asset entry.",
                Severity = StatusMessageModel.StatusSeverity.Error
            });
            return RedirectToAction(nameof(GetServerConfig));
        }

        var settings = await GetSettings();
        var code = model.AssetCode.Trim();
        var existing = settings.Assets.FirstOrDefault(a => string.Equals(a.AssetCode, code, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            existing.DisplayName = model.DisplayName.Trim();
            existing.Divisibility = model.Divisibility;
            existing.Symbol = string.IsNullOrWhiteSpace(model.Symbol) ? null : model.Symbol.Trim();
            existing.Enabled = model.Enabled;
            existing.IsManual = true;
        }
        else
        {
            settings.Assets.Add(new TalerAssetSettingsItem
            {
                AssetCode = code,
                DisplayName = model.DisplayName.Trim(),
                Divisibility = model.Divisibility,
                Symbol = string.IsNullOrWhiteSpace(model.Symbol) ? null : model.Symbol.Trim(),
                Enabled = model.Enabled,
                IsManual = true
            });
        }

        await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Message = $"Manual asset {code} added.",
            Severity = StatusMessageModel.StatusSeverity.Success
        });

        return RedirectToAction(nameof(GetServerConfig));
    }

    [HttpPost("assets/{assetCode}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteAsset(string assetCode)
    {
        var settings = await GetSettings();
        var existing = settings.Assets.FirstOrDefault(a => string.Equals(a.AssetCode, assetCode, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            settings.Assets.Remove(existing);
            await settingsRepository.UpdateSetting(settings, TalerPlugin.ServerSettingsKey);
        }

        TempData.SetStatusMessageModel(new StatusMessageModel
        {
            Message = $"Asset {assetCode} removed.",
            Severity = StatusMessageModel.StatusSeverity.Success
        });

        return RedirectToAction(nameof(GetServerConfig));
    }

    private async Task<TalerServerSettings> GetSettings()
    {
        return await settingsRepository.GetSettingAsync<TalerServerSettings>(TalerPlugin.ServerSettingsKey) ?? new TalerServerSettings();
    }

    private static string GetDefaultMerchantBaseUrl()
    {
        var configured = Environment.GetEnvironmentVariable("BTCPAY_TALER_MERCHANT_URL");
        if (!string.IsNullOrWhiteSpace(configured))
            return configured.Trim();

        return string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
            ? "http://taler-merchant:9966/"
            : "http://localhost:9966/";
    }

    private static TalerServerConfigViewModel Map(TalerServerSettings settings)
    {
        return new TalerServerConfigViewModel
        {
            MerchantBaseUrl = settings.MerchantBaseUrl,
            MerchantPublicBaseUrl = settings.MerchantPublicBaseUrl,
            MerchantInstanceId = settings.MerchantInstanceId,
            InstancePassword = settings.InstancePassword,
            InstancePasswordActual = settings.InstancePassword,
            ApiToken = settings.ApiToken,
            ApiTokenActual = settings.ApiToken,
            Assets = settings.Assets.Select(Map).OrderBy(a => a.AssetCode, StringComparer.OrdinalIgnoreCase).ToList()
        };
    }

    private static (string? BaseUrl, string? InstanceId, string? ApiToken) ResolveBackendAccess(TalerServerConfigViewModel? form, TalerServerSettings settings)
    {
        var baseUrl = string.IsNullOrWhiteSpace(form?.MerchantBaseUrl) ? settings.MerchantBaseUrl : form!.MerchantBaseUrl.Trim();
        var instanceId = string.IsNullOrWhiteSpace(form?.MerchantInstanceId)
            ? (string.IsNullOrWhiteSpace(settings.MerchantInstanceId) ? "default" : settings.MerchantInstanceId)
            : form!.MerchantInstanceId.Trim();
        var apiToken = NormalizeSecretInput(form?.ApiToken, settings.ApiToken);
        return (baseUrl, instanceId, apiToken);
    }

    private async Task PopulateBankAccounts(TalerServerConfigViewModel vm, string? baseUrl, string? instanceId, string? apiToken)
    {
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(apiToken))
            return;

        try
        {
            var accounts = await talerMerchantClient.GetBankAccountsAsync(baseUrl, instanceId, apiToken, HttpContext.RequestAborted);
            vm.BankAccounts = accounts.Select(a => new TalerBankAccountViewModel
            {
                PaytoUri = a.PaytoUri,
                HWire = a.HWire,
                Active = a.Active
            }).ToList();
        }
        catch (Exception ex)
        {
            vm.BankAccountsError = ex.Message;
        }
    }

    private static string? NormalizeSecretInput(string? input, string? currentValue)
    {
        if (string.IsNullOrWhiteSpace(input))
            return currentValue;

        var trimmed = input.Trim();
        if (trimmed == SecretMask)
            return currentValue;

        return trimmed;
    }

    private static string? UpdateSecret(string? currentValue, string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var trimmed = input.Trim();
        if (trimmed == SecretMask)
            return currentValue;

        return trimmed;
    }

    private static TalerAssetViewModel Map(TalerAssetSettingsItem asset)
    {
        return new TalerAssetViewModel
        {
            AssetCode = asset.AssetCode,
            DisplayName = asset.DisplayName,
            Divisibility = asset.Divisibility,
            Symbol = asset.Symbol,
            Enabled = asset.Enabled,
            IsManual = asset.IsManual
        };
    }

    private static TalerAssetSettingsItem Map(TalerAssetViewModel asset)
    {
        return new TalerAssetSettingsItem
        {
            AssetCode = asset.AssetCode.Trim(),
            DisplayName = asset.DisplayName.Trim(),
            Divisibility = asset.Divisibility,
            Symbol = string.IsNullOrWhiteSpace(asset.Symbol) ? null : asset.Symbol.Trim(),
            Enabled = asset.Enabled,
            IsManual = asset.IsManual
        };
    }
}
