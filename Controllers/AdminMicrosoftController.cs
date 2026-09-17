using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

using Microsoft.Extensions.Localization;

namespace Splitbill.Controllers;

[Authorize(Roles = "Admin")]
[Route("admin/microsoft")]
[Route("AdminMicrosoft")]
public sealed class AdminMicrosoftController(
    IMicrosoftIntegrationService microsoft,
    ApplicationDbContext db,
    IStringLocalizer<SharedResource> localizer,
    ILogger<AdminMicrosoftController> logger) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? tab = null, CancellationToken cancellationToken = default)
    {
        var model = await BuildUnifiedViewModelAsync(tab, cancellationToken);
        return View(model);
    }

    [HttpPost("save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(MicrosoftIntegrationSaveViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            var unified = await BuildUnifiedViewModelAsync("sso", cancellationToken);
            unified.Microsoft.TenantId = model.TenantId;
            unified.Microsoft.ClientId = model.ClientId;
            unified.Microsoft.MicrosoftIntegrationEnabled = model.MicrosoftIntegrationEnabled;
            unified.Microsoft.MicrosoftLoginEnabled = model.MicrosoftLoginEnabled;
            return View("Index", unified);
        }

        try
        {
            await microsoft.SaveAsync(model, User.FindFirstValue(ClaimTypes.NameIdentifier), cancellationToken);
            TempData["StatusMessage"] = localizer["MicrosoftCredentialsSaved"].Value;
            return RedirectToAction(nameof(Index), new { tab = "sso" });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(ex, "Microsoft Integration settings rejected");
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToAction(nameof(Index), new { tab = "sso" });
        }
    }

    [HttpPost("migrate-sharepoint")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> MigrateSharePoint(CancellationToken cancellationToken)
    {
        var result = await microsoft.MigrateLegacySharePointAsync(cancellationToken);
        TempData[result.Succeeded ? "StatusMessage" : "ErrorMessage"] = result.Succeeded
            ? localizer["MicrosoftMigrationComplete"].Value
            : result.Error;
        return RedirectToAction(nameof(Index), new { tab = "sso" });
    }

    private async Task<AdminMicrosoftUnifiedViewModel> BuildUnifiedViewModelAsync(string? tab, CancellationToken cancellationToken)
    {
        var msSettings = await microsoft.GetSettingsAsync(cancellationToken);
        var sharepointConfig = await db.SharePointConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);

        var sharepointVm = sharepointConfig is null ? new SharePointSettingsViewModel() : ToSharePointViewModel(sharepointConfig);

        if (string.IsNullOrWhiteSpace(sharepointVm.TenantId)) sharepointVm.TenantId = msSettings.TenantId;
        if (string.IsNullOrWhiteSpace(sharepointVm.ClientId)) sharepointVm.ClientId = msSettings.ClientId;
        sharepointVm.HasStoredClientSecret = sharepointVm.HasStoredClientSecret || msSettings.HasStoredClientSecret;
        sharepointVm.IsMicrosoftIntegrationEnabled = msSettings.MicrosoftIntegrationEnabled;
        sharepointVm.IsMicrosoftLoginEnabled = msSettings.MicrosoftLoginEnabled;

        var pending = await db.SharePointNotificationOutbox.CountAsync(
            x => x.Status == SharePointNotificationStatus.Pending || x.Status == SharePointNotificationStatus.Processing, cancellationToken);
        var delivered = await db.SharePointNotificationOutbox.CountAsync(
            x => x.Status == SharePointNotificationStatus.Sent, cancellationToken);
        var deadLetter = await db.SharePointNotificationOutbox.CountAsync(
            x => x.Status == SharePointNotificationStatus.DeadLetter, cancellationToken);

        var activeTab = string.Equals(tab, "sharepoint", StringComparison.OrdinalIgnoreCase) ? "sharepoint" : "sso";
        var callbackUrl = $"{Request.Scheme}://{Request.Host}/account/microsoft/oauth-callback";

        return new AdminMicrosoftUnifiedViewModel
        {
            ActiveTab = activeTab,
            Microsoft = msSettings,
            SharePoint = sharepointVm,
            CallbackUrl = callbackUrl,
            OutboxPendingCount = pending,
            OutboxDeliveredCount = delivered,
            OutboxDeadLetterCount = deadLetter
        };
    }

    private static SharePointSettingsViewModel ToSharePointViewModel(SharePointConfiguration settings) => new()
    {
        Id = settings.Id,
        Enabled = settings.Enabled,
        TenantId = settings.TenantId,
        ClientId = settings.ClientId,
        SiteUrl = settings.SiteUrl,
        SiteId = settings.SiteId,
        SiteDisplayName = settings.SiteDisplayName,
        SelectedListId = settings.ListId,
        SelectedListDisplayName = settings.ListDisplayName,
        SelectedListWebUrl = settings.ListWebUrl,
        LastTestAt = settings.LastTestAt,
        LastTestSucceeded = settings.LastTestSucceeded,
        LastError = settings.LastError,
        HasStoredClientSecret = !string.IsNullOrWhiteSpace(settings.ProtectedClientSecret),
        UseSharedMicrosoftCredentials = settings.UseSharedMicrosoftCredentials
    };
}
