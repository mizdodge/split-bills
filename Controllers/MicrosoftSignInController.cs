using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Controllers;

[AllowAnonymous]
[Route("account/microsoft-signin")]
public sealed class MicrosoftSignInController(
    IMicrosoftIntegrationService integration,
    SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager,
    IStringLocalizer<SharedResource> localizer,
    ILogger<MicrosoftSignInController> logger) : Controller
{
    [HttpGet("start")]
    public async Task<IActionResult> Start(string? returnUrl = null, CancellationToken cancellationToken = default)
    {
        var settings = await integration.GetSettingsAsync(cancellationToken);
        if (!settings.MicrosoftLoginEnabled || !settings.HasStoredClientSecret)
            return RedirectToAction("Login", "Account", new { returnUrl });
        logger.LogInformation("Initiating Microsoft sign-in challenge");
        var properties = new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(Callback), "MicrosoftSignIn", new { returnUrl })!
        };
        return Challenge(properties, "MicrosoftLink");
    }

    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string? returnUrl = null)
    {
        var auth = await HttpContext.AuthenticateAsync("MicrosoftLinkCookie");
        if (!auth.Succeeded) auth = await HttpContext.AuthenticateAsync("MicrosoftLink");
        if (!auth.Succeeded || auth.Principal == null)
        {
            logger.LogWarning("Microsoft sign-in callback authentication failed.");
            return RedirectToAction("Login", "Account", new { returnUrl });
        }

        var subject = auth.Principal.FindFirstValue(MicrosoftOAuthClaimsParser.OidClaimType)
            ?? auth.Principal.FindFirstValue("oid")
            ?? auth.Principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? auth.Principal.FindFirstValue("sub");
        var tenant = auth.Principal.FindFirstValue(MicrosoftOAuthClaimsParser.TenantIdClaimType)
            ?? auth.Principal.FindFirstValue("tid");
        var email = auth.Principal.FindFirstValue(ClaimTypes.Email)
            ?? auth.Principal.FindFirstValue("preferred_username")
            ?? auth.Principal.FindFirstValue(ClaimTypes.Upn)
            ?? auth.Principal.FindFirstValue("email");

        if (string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("Microsoft sign-in callback missing subject/objectidentifier.");
            TempData["ErrorMessage"] = localizer["MicrosoftInvalidIdentity"].Value;
            return RedirectToAction("Login", "Account", new { returnUrl });
        }

        // 1. Cari user yang sudah terhubung dengan (tenant, subject)
        ApplicationUser? user = null;
        if (!string.IsNullOrWhiteSpace(tenant) && !string.IsNullOrWhiteSpace(subject))
        {
            user = await userManager.Users.FirstOrDefaultAsync(u =>
                u.MicrosoftTenantId == tenant && u.MicrosoftSubject == subject && !u.MicrosoftLinkRevoked);
        }

        // 2. Jika belum terhubung dengan (tenant, subject), cocokkan dengan email atau username
        if (user == null && !string.IsNullOrWhiteSpace(email))
        {
            var cleanEmail = email.Trim();
            var normalizedEmail = userManager.NormalizeEmail(cleanEmail);
            var emailPrefix = cleanEmail.Contains('@') ? cleanEmail.Split('@')[0] : cleanEmail;
            var normalizedPrefix = userManager.NormalizeName(emailPrefix.Trim());

            user = await userManager.Users.FirstOrDefaultAsync(u =>
                u.NormalizedEmail == normalizedEmail ||
                u.Email == cleanEmail ||
                u.NormalizedUserName == normalizedEmail ||
                u.UserName == cleanEmail ||
                u.NormalizedUserName == normalizedPrefix ||
                u.UserName == emailPrefix ||
                u.MicrosoftAccountEmail == cleanEmail);

            if (user != null)
            {
                user.MicrosoftTenantId = tenant;
                user.MicrosoftSubject = subject;
                user.MicrosoftAccountEmail = cleanEmail;
                user.MicrosoftAccountDisplayName = auth.Principal.FindFirstValue(ClaimTypes.Name) ?? cleanEmail;
                user.MicrosoftLinkedAt = DateTimeOffset.UtcNow;
                user.MicrosoftLastVerifiedAt = DateTimeOffset.UtcNow;
                user.MicrosoftLinkRevoked = false;
                await userManager.UpdateAsync(user);
                logger.LogInformation("Auto-linked Microsoft account {Email} to user {Username}", cleanEmail, user.UserName);
            }
        }

        // 3. Jika user tidak ditemukan di database SplitBill
        if (user == null)
        {
            logger.LogWarning("No SplitBill user found for Microsoft login (email: {Email}, subject: {Subject})", email, subject);
            TempData["ErrorMessage"] = localizer["AccountNotRegistered", email ?? subject].Value;
            return RedirectToAction("Login", "Account", new { returnUrl });
        }

        // 4. Periksa apakah user terkunci
        if (await userManager.IsLockedOutAsync(user))
        {
            logger.LogWarning("User {Username} is locked out", user.UserName);
            TempData["ErrorMessage"] = localizer["AccountLocked"].Value;
            return RedirectToAction("Login", "Account", new { returnUrl });
        }

        // 5. Login user ke sesi ASP.NET Core Identity
        await signInManager.SignInAsync(user, isPersistent: true);
        await HttpContext.SignOutAsync("MicrosoftLinkCookie");
        logger.LogInformation("User {Username} successfully signed in via Microsoft SSO", user.UserName);

        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }
        return RedirectToAction("Index", "Dashboard");
    }
}
