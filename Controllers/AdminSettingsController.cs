using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize(Roles = DatabaseSeeder.AdminRole)]
public sealed class AdminSettingsController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    IDataProtectionProvider dataProtectionProvider, IAiReceiptService aiService,
    IAiModelCatalogService modelCatalogService, IStringLocalizer<SharedResource> localizer) : Controller
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(AiApiKeyProtection.Purpose);

    public async Task<IActionResult> Index()
    {
        var settings = await db.AiConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive);
        return View(settings is null ? new AiSettingsViewModel() : ToViewModel(settings));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(AiSettingsViewModel input, string actionType = "save")
    {
        var settings = await db.AiConfigurations.SingleOrDefaultAsync(x => x.IsActive) ?? new AiConfiguration();
        var hasStoredKeyForProvider = settings.Id != 0 && settings.Provider == input.Provider &&
                                      !string.IsNullOrWhiteSpace(settings.ProtectedApiKey);
        if (string.IsNullOrWhiteSpace(input.ApiKey) && !hasStoredKeyForProvider)
            ModelState.AddModelError(nameof(input.ApiKey), localizer["ProviderKeyRequired", ProviderName(input.Provider)]);
        if (input.Provider == AiProvider.AzureOpenAi)
        {
            if (string.IsNullOrWhiteSpace(input.Endpoint)) ModelState.AddModelError(nameof(input.Endpoint), localizer["AzureEndpointRequired"]);
            if (string.IsNullOrWhiteSpace(input.DeploymentName)) ModelState.AddModelError(nameof(input.DeploymentName), localizer["AzureDeploymentRequired"]);
            if (string.IsNullOrWhiteSpace(input.ApiVersion)) ModelState.AddModelError(nameof(input.ApiVersion), localizer["AzureApiVersionRequired"]);
        }
        if (!ModelState.IsValid)
        {
            input.HasStoredApiKey = hasStoredKeyForProvider;
            return View(input);
        }

        if (settings.Provider != input.Provider && string.IsNullOrWhiteSpace(input.ApiKey)) settings.ProtectedApiKey = null;
        settings.Provider = input.Provider;
        settings.Endpoint = input.Endpoint?.Trim();
        settings.Model = input.Model.Trim();
        settings.DeploymentName = input.DeploymentName?.Trim();
        settings.ApiVersion = input.ApiVersion?.Trim();
        settings.ApiMode = input.ApiMode;
        settings.UpdatedAt = DateTimeOffset.UtcNow;
        settings.UpdatedByUserId = userManager.GetUserId(User);
        if (!string.IsNullOrWhiteSpace(input.ApiKey)) settings.ProtectedApiKey = _protector.Protect(input.ApiKey.Trim());
        if (settings.Id == 0) db.AiConfigurations.Add(settings);
        await db.SaveChangesAsync();

        if (actionType == "test")
        {
            settings.LastTestAt = DateTimeOffset.UtcNow;
            try
            {
                await aiService.TestConnectionAsync(settings);
                settings.LastTestSucceeded = true;
                settings.LastError = null;
                TempData["Success"] = localizer["ConfigurationTestSuccess"].Value;
            }
            catch (Exception exception) when (exception is AiServiceException or System.Text.Json.JsonException)
            {
                settings.LastTestSucceeded = false;
                settings.LastError = exception.Message.Length > 1000 ? exception.Message[..1000] : exception.Message;
                TempData["Error"] = localizer["ConfigurationTestFailed"].Value;
            }
            await db.SaveChangesAsync();
        }
        else TempData["Success"] = localizer["ConfigurationSaved"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Models(AiModelLookupViewModel input, CancellationToken cancellationToken)
    {
        var activeSettings = await db.AiConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.IsActive, cancellationToken);
        var protectedApiKey = activeSettings?.Provider == input.Provider ? activeSettings.ProtectedApiKey : null;
        if (!string.IsNullOrWhiteSpace(input.ApiKey))
            protectedApiKey = _protector.Protect(input.ApiKey.Trim());

        try
        {
            var models = await modelCatalogService.ListModelsAsync(
                new AiModelLookup(input.Provider, input.Endpoint, input.ApiVersion, protectedApiKey),
                cancellationToken);
            return Json(new { models });
        }
        catch (Exception exception) when (exception is AiServiceException or System.Text.Json.JsonException)
        {
            return BadRequest(new { message = exception.Message });
        }
    }

    private static AiSettingsViewModel ToViewModel(AiConfiguration settings) => new()
    {
        Id = settings.Id, Provider = settings.Provider, Endpoint = settings.Endpoint, Model = settings.Model,
        DeploymentName = settings.DeploymentName, ApiVersion = settings.ApiVersion, ApiMode = settings.ApiMode,
        LastTestAt = settings.LastTestAt, LastTestSucceeded = settings.LastTestSucceeded, LastError = settings.LastError,
        HasStoredApiKey = !string.IsNullOrWhiteSpace(settings.ProtectedApiKey)
    };

    private static string ProviderName(AiProvider provider) =>
        provider == AiProvider.OpenAi ? "OpenAI" : "Azure OpenAI";
}
