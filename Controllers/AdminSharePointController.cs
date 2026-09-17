using System.Security.Claims;
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
public sealed class AdminSharePointController(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    SharePointSecretProtector secretProtector,
    ISharePointGraphService graphService,
    ISharePointTestStateProtector testStateProtector,
    IStringLocalizer<SharedResource> localizer) : Controller
{
    private const int ConfigurationId = 1;

    [HttpGet]
    public IActionResult Index()
        => RedirectToAction("Index", "AdminMicrosoft", new { tab = "sharepoint" });

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> TestConnection(
        SharePointConnectionTestViewModel input,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
            return BadRequest(new { message = localizer["SharePointInvalidInput"].Value });

        var existing = await db.SharePointConfigurations
            .SingleOrDefaultAsync(x => x.Id == ConfigurationId, cancellationToken);
        var clientSecret = await ResolveSecretAsync(input.TenantId, input.ClientId, input.ClientSecret, existing);
        if (clientSecret is null)
            return BadRequest(new { message = localizer["SharePointClientSecretRequired"].Value });

        try
        {
            var request = new SharePointConnectionRequest(input.TenantId, input.ClientId, clientSecret, input.SiteUrl);
            var result = await graphService.TestConnectionAsync(request, cancellationToken);
            var userId = CurrentUserId();
            var token = testStateProtector.Protect(userId, request, result, clientSecret, DateTimeOffset.UtcNow);
            return Json(new
            {
                success = true,
                siteId = result.SiteId,
                siteDisplayName = result.SiteDisplayName,
                siteWebUrl = result.SiteWebUrl,
                testedStateToken = token,
                lists = result.Lists.Select(x => new { id = x.Id, displayName = x.DisplayName, webUrl = x.WebUrl })
            });
        }
        catch (SharePointGraphException exception)
        {
            if (existing is not null)
            {
                existing.LastTestAt = DateTimeOffset.UtcNow;
                existing.LastTestSucceeded = false;
                existing.LastError = ErrorMessage(exception);
                await db.SaveChangesAsync(cancellationToken);
            }
            return BadRequest(new { message = ErrorMessage(exception) });
        }
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SharePointSaveViewModel input, CancellationToken cancellationToken)
    {
        var existing = await db.SharePointConfigurations
            .SingleOrDefaultAsync(x => x.Id == ConfigurationId, cancellationToken);
        var clientSecret = await ResolveSecretAsync(input.TenantId, input.ClientId, input.ClientSecret, existing);
        if (clientSecret is null)
        {
            ModelState.AddModelError(nameof(input.ClientSecret), localizer["SharePointClientSecretRequired"]);
            return await InvalidSaveAsync(input, cancellationToken);
        }

        if (!Guid.TryParse(input.TenantId?.Trim(), out var tenantGuid))
            ModelState.AddModelError(nameof(input.TenantId), localizer["SharePointInvalidInput"]);
        if (!Guid.TryParse(input.ClientId?.Trim(), out var clientGuid))
            ModelState.AddModelError(nameof(input.ClientId), localizer["SharePointInvalidInput"]);

        Uri? normalizedSite = null;
        try
        {
            normalizedSite = SharePointUrlNormalizer.Normalize(input.SiteUrl);
        }
        catch (SharePointGraphException)
        {
            ModelState.AddModelError(nameof(input.SiteUrl), localizer["SharePointInvalidInput"]);
        }
        if (!ModelState.IsValid || normalizedSite is null)
            return await InvalidSaveAsync(input, cancellationToken);

        var userId = CurrentUserId();
        if (!testStateProtector.TryUnprotect(input.TestedStateToken ?? string.Empty, userId, out var state, DateTimeOffset.UtcNow) ||
            state is null ||
            !string.Equals(state.TenantId, tenantGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.ClientId, clientGuid.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(state.SiteUrl, normalizedSite.ToString(), StringComparison.Ordinal) ||
            !string.Equals(state.SiteId, input.SiteId, StringComparison.Ordinal) ||
            !string.Equals(state.Fingerprint, SharePointTestStateProtector.CreateFingerprint(
                tenantGuid.ToString("D"), clientGuid.ToString("D"), normalizedSite.ToString(), clientSecret), StringComparison.Ordinal) ||
            state.Lists.All(x => !string.Equals(x.Id, input.ListId, StringComparison.Ordinal)))
        {
            ModelState.AddModelError(string.Empty, localizer["SharePointTestRequired"]);
            return await InvalidSaveAsync(input, cancellationToken);
        }

        if (!ModelState.IsValid)
            return await InvalidSaveAsync(input, cancellationToken);

        var selectedList = state.Lists.Single(x => string.Equals(x.Id, input.ListId, StringComparison.Ordinal));
        var now = DateTimeOffset.UtcNow;
        existing ??= new SharePointConfiguration { Id = ConfigurationId };
        existing.Enabled = input.Enabled;
        existing.TenantId = tenantGuid.ToString("D");
        existing.ClientId = clientGuid.ToString("D");
        existing.ProtectedClientSecret = string.IsNullOrWhiteSpace(input.ClientSecret)
            ? existing.ProtectedClientSecret
            : secretProtector.Protect(input.ClientSecret.Trim());
        existing.SiteUrl = normalizedSite.ToString();
        existing.SiteId = state.SiteId;
        existing.SiteDisplayName = state.SiteDisplayName;
        existing.ListId = selectedList.Id;
        existing.ListDisplayName = selectedList.DisplayName;
        existing.ListWebUrl = selectedList.WebUrl;
        existing.LastTestAt = state.TestedAt;
        existing.LastTestSucceeded = true;
        existing.LastError = null;
        existing.UpdatedAt = now;
        existing.UpdatedByUserId = userId;
        if (existing.Id == ConfigurationId && db.Entry(existing).State == EntityState.Detached)
            db.SharePointConfigurations.Add(existing);

        await db.SaveChangesAsync(cancellationToken);
        TempData["StatusMessage"] = localizer["SharePointConfigurationSaved"].Value;
        return RedirectToAction("Index", "AdminMicrosoft", new { tab = "sharepoint" });
    }

    private Task<IActionResult> InvalidSaveAsync(SharePointSaveViewModel input, CancellationToken cancellationToken)
    {
        TempData["ErrorMessage"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
        return Task.FromResult<IActionResult>(RedirectToAction("Index", "AdminMicrosoft", new { tab = "sharepoint" }));
    }

    private async Task<string?> ResolveSecretAsync(
        string tenantId,
        string clientId,
        string? submittedSecret,
        SharePointConfiguration? existing)
    {
        if (!string.IsNullOrWhiteSpace(submittedSecret)) return submittedSecret.Trim();
        if (existing is not null && string.Equals(existing.TenantId, NormalizeGuidOrEmpty(tenantId), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(existing.ClientId, NormalizeGuidOrEmpty(clientId), StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(existing.ProtectedClientSecret))
        {
            try
            {
                return secretProtector.Unprotect(existing.ProtectedClientSecret);
            }
            catch (InvalidOperationException) { }
        }

        var msConfig = await db.MicrosoftIntegrationConfigurations.FindAsync(1);
        if (msConfig is not null &&
            string.Equals(msConfig.TenantId, NormalizeGuidOrEmpty(tenantId), StringComparison.OrdinalIgnoreCase) &&
            string.Equals(msConfig.ClientId, NormalizeGuidOrEmpty(clientId), StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(msConfig.ProtectedClientSecret))
        {
            try
            {
                var msProtector = HttpContext.RequestServices.GetRequiredService<IMicrosoftSecretProtector>();
                return msProtector.Unprotect(msConfig.ProtectedClientSecret);
            }
            catch (InvalidOperationException) { }
        }

        return null;
    }

    private string CurrentUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? userManager.GetUserId(User) ?? string.Empty;

    private string ErrorMessage(SharePointGraphErrorCategory category) => category switch
    {
        SharePointGraphErrorCategory.InvalidInput => localizer["SharePointInvalidInput"].Value,
        SharePointGraphErrorCategory.AuthenticationFailed => localizer["SharePointAuthenticationFailed"].Value,
        SharePointGraphErrorCategory.PermissionDenied => localizer["SharePointPermissionDenied"].Value,
        SharePointGraphErrorCategory.NotFound => localizer["SharePointSiteNotFound"].Value,
        SharePointGraphErrorCategory.NoLists => localizer["SharePointNoLists"].Value,
        SharePointGraphErrorCategory.RateLimited => localizer["SharePointRateLimited"].Value,
        SharePointGraphErrorCategory.NetworkFailure => localizer["SharePointNetworkFailure"].Value,
        _ => localizer["SharePointInvalidResponse"].Value
    };

    private string ErrorMessage(SharePointGraphException exception)
    {
        if (exception.Category == SharePointGraphErrorCategory.NetworkFailure &&
            exception.DiagnosticCode?.StartsWith("GRAPH_HTTP_", StringComparison.Ordinal) == true)
            return string.IsNullOrWhiteSpace(exception.ProviderMessage)
                ? localizer["SharePointGraphRequestFailedWithCode", exception.DiagnosticCode].Value
                : localizer["SharePointGraphRequestFailedWithDetail", exception.DiagnosticCode, exception.ProviderMessage].Value;

        if (exception.Category != SharePointGraphErrorCategory.AuthenticationFailed ||
            string.IsNullOrWhiteSpace(exception.DiagnosticCode))
            return ErrorMessage(exception.Category);

        return exception.DiagnosticCode switch
        {
            "AADSTS7000215" => localizer["SharePointInvalidClientSecret"].Value,
            "AADSTS7000222" => localizer["SharePointExpiredClientSecret"].Value,
            "AADSTS700016" => localizer["SharePointApplicationNotFound"].Value,
            "AADSTS7000112" => localizer["SharePointApplicationDisabled"].Value,
            "AADSTS900023" => localizer["SharePointTenantInvalid"].Value,
            var code when code.StartsWith("GRAPH_HTTP_401", StringComparison.Ordinal) => localizer["SharePointGraphTokenRejected"].Value,
            _ => localizer["SharePointAuthenticationFailedWithCode", exception.DiagnosticCode].Value
        };
    }

    private static string NormalizeGuid(string value) => Guid.Parse(value.Trim()).ToString("D");

    private static string NormalizeGuidOrEmpty(string? value)
        => Guid.TryParse(value?.Trim(), out var guid) ? guid.ToString("D") : string.Empty;

    private static SharePointSettingsViewModel ToViewModel(SharePointConfiguration settings) => new()
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
        HasStoredClientSecret = !string.IsNullOrWhiteSpace(settings.ProtectedClientSecret)
    };
}
