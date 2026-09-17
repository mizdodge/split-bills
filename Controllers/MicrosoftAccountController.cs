using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Controllers;

[Authorize]
[Route("account/microsoft")]
public sealed class MicrosoftAccountController(
    ApplicationDbContext db,
    IMicrosoftIntegrationService integration,
    IMicrosoftLinkStateProtector stateProtector,
    UserManager<ApplicationUser> users,
    ILogger<MicrosoftAccountController> logger) : Controller
{
    [HttpGet("link")]
    public async Task<IActionResult> Link(CancellationToken cancellationToken)
    {
        var settings = await integration.GetSettingsAsync(cancellationToken);
        if (!settings.MicrosoftLoginEnabled || !settings.HasStoredClientSecret) return Forbid();
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var intent = new MicrosoftAccountLinkIntent
        {
            Id = Guid.NewGuid().ToString("N"), UserId = user.Id,
            ProtectedState = stateProtector.Protect(new MicrosoftLinkState("pending", user.Id, Guid.NewGuid().ToString("N"))),
            SecurityStampHash = MicrosoftLinkStateProtector.Hash(await users.GetSecurityStampAsync(user) ?? string.Empty),
            BrowserBindingHash = MicrosoftLinkStateProtector.Hash(GetBinding()),
            CredentialRevision = await db.MicrosoftIntegrationConfigurations.Where(x => x.Id == 1).Select(x => x.CredentialRevision).SingleAsync(cancellationToken),
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        var state = stateProtector.Protect(new MicrosoftLinkState(intent.Id, user.Id, Guid.NewGuid().ToString("N")));
        intent.ProtectedState = state;
        db.MicrosoftAccountLinkIntents.Add(intent);
        await db.SaveChangesAsync(cancellationToken);
        return Challenge(new AuthenticationProperties { RedirectUri = Url.Action(nameof(Callback), "MicrosoftAccount", new { state = intent.Id }) }, "MicrosoftLink");
    }

    [AllowAnonymous]
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string state, CancellationToken cancellationToken)
    {
        var intent = await db.MicrosoftAccountLinkIntents.SingleOrDefaultAsync(x => x.Id == state, cancellationToken);
        if (intent is null || intent.UsedAt is not null || intent.ExpiresAt <= DateTimeOffset.UtcNow) return BadRequest("The Microsoft link request is invalid or expired.");
        var auth = await HttpContext.AuthenticateAsync("MicrosoftLinkCookie");
        if (!auth.Succeeded) auth = await HttpContext.AuthenticateAsync("MicrosoftLink");
        if (!auth.Succeeded) return RedirectToAction("Login", "Account");
        var subject = auth.Principal?.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier")
            ?? auth.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
        var tenant = auth.Principal?.FindFirstValue("http://schemas.microsoft.com/identity/claims/tenantid");
        var email = auth.Principal?.FindFirstValue(ClaimTypes.Email) ?? auth.Principal?.FindFirstValue("preferred_username");
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(tenant)) return BadRequest("Microsoft did not provide a verifiable identity.");
        var currentUser = await users.FindByIdAsync(intent.UserId);
        if (currentUser is null || MicrosoftLinkStateProtector.Hash(GetBinding()) != intent.BrowserBindingHash) return BadRequest("The Microsoft link request is not valid for this browser.");
        var stamp = await users.GetSecurityStampAsync(currentUser) ?? string.Empty;
        if (MicrosoftLinkStateProtector.Hash(stamp) != intent.SecurityStampHash) return BadRequest("Your account changed while linking. Try again.");
        var credentialRevision = await db.MicrosoftIntegrationConfigurations.Where(x => x.Id == 1).Select(x => x.CredentialRevision).SingleAsync(cancellationToken);
        if (credentialRevision != intent.CredentialRevision) return BadRequest("Microsoft credentials changed while linking. Try again.");
        if (await db.Users.AnyAsync(x => x.Id != currentUser.Id && x.MicrosoftTenantId == tenant && x.MicrosoftSubject == subject, cancellationToken)) return Conflict("That Microsoft account is already linked.");
        currentUser.MicrosoftTenantId = tenant;
        currentUser.MicrosoftSubject = subject;
        currentUser.MicrosoftAccountEmail = email;
        currentUser.MicrosoftAccountDisplayName = auth.Principal?.FindFirstValue(ClaimTypes.Name) ?? email;
        currentUser.MicrosoftLinkedAt = DateTimeOffset.UtcNow;
        currentUser.MicrosoftLastVerifiedAt = DateTimeOffset.UtcNow;
        currentUser.MicrosoftLinkRevoked = false;
        currentUser.MicrosoftCredentialRevision = credentialRevision;
        currentUser.MicrosoftLinkVersion++;
        intent.UsedAt = DateTimeOffset.UtcNow;
        await users.UpdateSecurityStampAsync(currentUser);
        await db.SaveChangesAsync(cancellationToken);
        await HttpContext.SignOutAsync("MicrosoftLinkCookie");
        logger.LogInformation("User {UserId} linked Microsoft account {Subject}", currentUser.Id, subject);
        TempData["StatusMessage"] = "Microsoft account linked after verification.";
        return RedirectToAction("Index", "Account");
    }

    [HttpPost("unlink")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unlink(CancellationToken cancellationToken)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        user.MicrosoftSubject = null; user.MicrosoftTenantId = null; user.MicrosoftAccountEmail = null;
        user.MicrosoftAccountDisplayName = null; user.MicrosoftLinkRevoked = true;
        user.MicrosoftLinkVersion++; user.MicrosoftLastVerifiedAt = null;
        await users.UpdateSecurityStampAsync(user);
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("User {UserId} unlinked Microsoft account", user.Id);
        return RedirectToAction("Index", "Account");
    }

    private string GetBinding() => HttpContext.Session.Id;
}
