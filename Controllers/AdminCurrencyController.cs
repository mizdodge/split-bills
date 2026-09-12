using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize(Roles = DatabaseSeeder.AdminRole)]
public sealed class AdminCurrencyController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    ICurrencyCatalog catalog, ICurrencyRateProvider provider, ICurrencyRateService rates,
    ICurrencyConfigurationService configurationService, IStringLocalizer<SharedResource> localizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var config = await GetConfigurationAsync(cancellationToken);
        return View(await ToViewModelAsync(config, cancellationToken));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(AdminCurrencyViewModel input, CancellationToken cancellationToken)
    {
        var config = await GetConfigurationAsync(cancellationToken);
        ValidateEndpoint(input);
        if (config.DefaultCurrencyCode != input.DefaultCurrencyCode.Trim().ToUpperInvariant() && await configurationService.IsDefaultCurrencyLockedAsync(cancellationToken))
            ModelState.AddModelError(nameof(input.DefaultCurrencyCode), localizer["CurrencyDefaultLocked"].Value);
        if (!catalog.TryGet(input.DefaultCurrencyCode, out _)) ModelState.AddModelError(nameof(input.DefaultCurrencyCode), localizer["UnsupportedCurrency"].Value);
        if (!ModelState.IsValid) { input.HasStoredApiKey = !string.IsNullOrWhiteSpace(config.ProtectedApiKey); input.Currencies = catalog.GetAll(); return View(input); }
        config.DefaultCurrencyCode = input.DefaultCurrencyCode.Trim().ToUpperInvariant();
        config.ProviderKind = input.ProviderKind; config.BaseUrl = input.BaseUrl.Trim().TrimEnd('/'); config.AuthenticationMode = input.AuthenticationMode;
        config.AllowPrivateNetworkEndpoint = input.AllowPrivateNetworkEndpoint; config.AutoRefreshEnabled = input.AutoRefreshEnabled;
        if (!string.IsNullOrWhiteSpace(input.ApiKey)) config.ProtectedApiKey = configurationService.ProtectApiKey(input.ApiKey.Trim());
        else if (input.AuthenticationMode == CurrencyAuthenticationMode.None) config.ProtectedApiKey = null;
        config.UpdatedAt = DateTimeOffset.UtcNow; config.UpdatedByUserId = userManager.GetUserId(User);
        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = localizer["CurrencyConfigurationSaved"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(AdminCurrencyViewModel input, CancellationToken cancellationToken)
    {
        var config = await GetConfigurationAsync(cancellationToken);
        ValidateEndpoint(input);
        if (!ModelState.IsValid) { input.Currencies = catalog.GetAll(); return View("Index", input); }
        try
        {
            var secret = !string.IsNullOrWhiteSpace(input.ApiKey) ? input.ApiKey : configurationService.UnprotectApiKey(config.ProtectedApiKey);
            var remoteCurrencies = await provider.GetCurrenciesAsync(input.BaseUrl.Trim().TrimEnd('/'), input.AuthenticationMode, secret, cancellationToken);
            var sampleBase = remoteCurrencies.Any(x => x.Code == "USD") ? "USD" : remoteCurrencies.FirstOrDefault(x => x.Code != input.DefaultCurrencyCode)?.Code;
            if (!string.IsNullOrWhiteSpace(sampleBase)) await provider.GetRateAsync(input.BaseUrl.Trim().TrimEnd('/'), input.AuthenticationMode, secret, sampleBase, input.DefaultCurrencyCode, DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);
            config.LastTestAt = DateTimeOffset.UtcNow; config.LastTestSucceeded = true; config.LastError = null; await db.SaveChangesAsync(cancellationToken);
            TempData["Success"] = localizer["CurrencyConnectionSuccess"].Value;
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        {
            config.LastTestAt = DateTimeOffset.UtcNow; config.LastTestSucceeded = false; config.LastError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message; await db.SaveChangesAsync(cancellationToken);
            TempData["Error"] = localizer["CurrencyConnectionFailed"].Value;
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Refresh(CancellationToken cancellationToken)
    {
        var config = await GetConfigurationAsync(cancellationToken);
        try { await rates.RefreshAsync(cancellationToken); TempData["Success"] = localizer["CurrencyRefreshSuccess"].Value; }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException)
        { config.LastRefreshAt = DateTimeOffset.UtcNow; config.LastRefreshSucceeded = false; config.LastRefreshError = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message; await db.SaveChangesAsync(cancellationToken); TempData["Error"] = localizer["CurrencyRefreshFailed"].Value; }
        return RedirectToAction(nameof(Index));
    }

    private async Task<CurrencyConfiguration> GetConfigurationAsync(CancellationToken cancellationToken) =>
        await db.CurrencyConfigurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken) ?? new CurrencyConfiguration { Id = 1, UpdatedAt = DateTimeOffset.UtcNow };

    private async Task<AdminCurrencyViewModel> ToViewModelAsync(CurrencyConfiguration config, CancellationToken cancellationToken) => new()
    {
        DefaultCurrencyCode = config.DefaultCurrencyCode, ProviderKind = config.ProviderKind, BaseUrl = config.BaseUrl,
        AuthenticationMode = config.AuthenticationMode, AllowPrivateNetworkEndpoint = config.AllowPrivateNetworkEndpoint,
        AutoRefreshEnabled = config.AutoRefreshEnabled, HasStoredApiKey = !string.IsNullOrWhiteSpace(config.ProtectedApiKey),
        LastTestAt = config.LastTestAt, LastTestSucceeded = config.LastTestSucceeded, LastError = config.LastError,
        LastRefreshAt = config.LastRefreshAt, LastRefreshSucceeded = config.LastRefreshSucceeded, LastRefreshError = config.LastRefreshError,
        DefaultCurrencyLocked = await configurationService.IsDefaultCurrencyLockedAsync(cancellationToken), Currencies = catalog.GetAll()
    };

    private void ValidateEndpoint(AdminCurrencyViewModel input)
    {
        if (!Uri.TryCreate(input.BaseUrl?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment) || !string.IsNullOrEmpty(uri.UserInfo))
            ModelState.AddModelError(nameof(input.BaseUrl), localizer["CurrencyEndpointInvalid"].Value);
        var loopbackHost = uri is not null && (string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase) || string.Equals(uri.Host, "::1", StringComparison.OrdinalIgnoreCase));
        if (uri is not null && uri.Scheme == "http" && !input.AllowPrivateNetworkEndpoint && !loopbackHost)
            ModelState.AddModelError(nameof(input.AllowPrivateNetworkEndpoint), localizer["CurrencyPrivateEndpointRequired"].Value);
    }

}
