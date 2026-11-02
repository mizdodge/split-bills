using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Splitbill.Models;
using Splitbill.Services;
namespace Splitbill.Controllers;

[AllowAnonymous, Route("account/microsoft-signin")]
public sealed class MicrosoftSignInController(IMicrosoftIntegrationService integration,
    IMicrosoftAccountService accounts, SignInManager<ApplicationUser> signInManager,
    UserManager<ApplicationUser> userManager, IStringLocalizer<SharedResource> localizer, IAccountSelfService? selfService = null) : Controller
{
    [HttpPost("start"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(string? returnUrl = null)
    {
        var settings = await integration.GetSettingsAsync();
        if (!settings.MicrosoftLoginEnabled || !settings.HasStoredClientSecret || !Request.IsHttps)
            return Failure("MicrosoftUnavailable");
        await HttpContext.SignOutAsync("MicrosoftLinkCookie");
        return Challenge(new AuthenticationProperties
        {
            RedirectUri = Url.Action(nameof(Callback), new { returnUrl = Url.IsLocalUrl(returnUrl) ? returnUrl : null })
        }, "MicrosoftLink");
    }

    // Protocol completion: the ticket is minted only by validated OIDC middleware.
    [HttpGet("callback")]
    public async Task<IActionResult> Callback(string? returnUrl = null)
    {
        var auth = await HttpContext.AuthenticateAsync("MicrosoftLinkCookie");
        await HttpContext.SignOutAsync("MicrosoftLinkCookie");
        var settings = await integration.GetSettingsAsync();
        var identity = auth.Succeeded ? MicrosoftIdentity.Read(auth.Principal, settings.TenantId) : null;
        if (identity is null || !settings.MicrosoftLoginEnabled)
            return auth.Properties?.Items.ContainsKey("security-intent") == true ? SecurityFailure() : Failure("MicrosoftAccountUnavailable");
        if (auth.Properties?.Items.TryGetValue("security-intent", out var securityId) == true && securityId is not null)
        {
            var local = await userManager.GetUserAsync(User);
            auth.Properties.Items.TryGetValue("security-purpose", out var purpose);
            DateTimeOffset? authenticatedAt = long.TryParse(auth.Principal?.FindFirst("auth_time")?.Value, out var seconds) && seconds is >= 0 and <= 253402300799
                ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;
            if (local is null || selfService is null || !await selfService.VerifyAsync(securityId, local,
                Request.Cookies[AccountSettingsController.SecurityBindingCookie] ?? "", identity, authenticatedAt) ||
                !await selfService.IsVerifiedAsync(securityId, local, Request.Cookies[AccountSettingsController.SecurityBindingCookie] ?? "", purpose ?? ""))
                return SecurityFailure();
            return RedirectToAction(purpose == AccountSelfService.PasswordPurpose ? "CreatePassword" : "ConfirmSessions", "AccountSettings", new { requestId = securityId });
        }
        if (auth.Properties?.Items.TryGetValue("link-intent", out var intentId) == true && intentId is not null)
        {
            var local = await userManager.GetUserAsync(User);
            if (local is null || !await accounts.ReceiveAsync(intentId, local,
                Request.Cookies[AccountSettingsController.BindingCookie] ?? "", identity)) return Failure("MicrosoftLinkExpired");
            return RedirectToAction("Confirm", "AccountSettings", new { intentId });
        }
        var result = await accounts.SignInAsync(identity);
        if (result.User is null) return Failure(result.Error ?? "MicrosoftAccountUnavailable");
        if (!await signInManager.CanSignInAsync(result.User) || await userManager.IsLockedOutAsync(result.User))
            return Failure("MicrosoftAccountUnavailable");
        if (await userManager.GetTwoFactorEnabledAsync(result.User)) return Failure("MicrosoftUseLocalTwoFactor");
        await signInManager.SignInWithClaimsAsync(result.User, false, new[]
        {
            new System.Security.Claims.Claim("splitbill:microsoft", result.User.MicrosoftLinkVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
        });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : "/Dashboard");
    }

    private IActionResult SecurityFailure()
    {
        TempData["ErrorMessage"] = localizer["AccountVerificationExpired"].Value;
        return RedirectToAction("Index", "AccountSettings");
    }

    private IActionResult Failure(string key)
    {
        TempData["ErrorMessage"] = localizer[key].Value;
        return RedirectToAction("Login", "Account");
    }
}
