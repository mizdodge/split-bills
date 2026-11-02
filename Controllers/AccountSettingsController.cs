using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize, Route("account/settings")]
public sealed class AccountSettingsController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
    IMicrosoftAccountService accounts, IMicrosoftIntegrationService integration, IStringLocalizer<SharedResource> localizer,
    IAccountSelfService selfService) : Controller
{
    public const string BindingCookie = "SplitBill.LinkBinding";
    public const string SecurityBindingCookie = "SplitBill.AccountSecurityBinding";
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (Request.Query["verificationError"] == "true") TempData["ErrorMessage"] = localizer["AccountVerificationExpired"].Value;
        var settings = await integration.GetSettingsAsync();
        return View(new AccountSettingsViewModel(user, await users.HasPasswordAsync(user), settings.MicrosoftLoginEnabled && settings.HasStoredClientSecret)
        { Roles = (await users.GetRolesAsync(user)).ToArray() });
    }

    [HttpPost("profile"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Profile(AccountProfileInput input)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var result = await selfService.SaveProfileAsync(user, input.DisplayName ?? "");
        if (!result.Succeeded) return Failed("AccountNameInvalid");
        TempData["Success"] = localizer["AccountProfileSaved"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("preferences"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Preferences(AccountPreferencesInput input)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var result = await selfService.SavePreferencesAsync(user, input.Culture ?? "", input.TeamsNotificationsEnabled, input.BrowserNotificationsEnabled);
        if (!result.Succeeded) return Failed("AccountLanguageInvalid");
        TempData["Success"] = localizer["AccountPreferencesSaved"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("verify-microsoft"), ValidateAntiForgeryToken]
    public async Task<IActionResult> VerifyMicrosoft(string purpose)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var settings = await integration.GetSettingsAsync();
        if (!Request.IsHttps || !settings.MicrosoftLoginEnabled || !settings.HasStoredClientSecret) return Failed("MicrosoftUnavailable");
        var binding = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var id = await selfService.BeginVerificationAsync(user, binding, purpose);
        if (id is null) return Failed("AccountVerificationExpired");
        Response.Cookies.Append(SecurityBindingCookie, binding, new CookieOptions
        { HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(5), IsEssential = true, Path = "/account" });
        await HttpContext.SignOutAsync("MicrosoftLinkCookie");
        var properties = new AuthenticationProperties { RedirectUri = Url.Action("Callback", "MicrosoftSignIn") };
        properties.Items["security-intent"] = id; properties.Items["security-purpose"] = purpose;
        return Challenge(properties, "MicrosoftLink");
    }

    [HttpGet("create-password")]
    public async Task<IActionResult> CreatePassword(string requestId)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        return await selfService.IsVerifiedAsync(requestId, user, Request.Cookies[SecurityBindingCookie] ?? "", AccountSelfService.PasswordPurpose)
            && !await users.HasPasswordAsync(user) ? View(new AccountPasswordInput { RequestId = requestId }) : Failed("AccountVerificationExpired");
    }

    [HttpPost("create-password"), ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePassword(AccountPasswordInput input)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var binding = Request.Cookies[SecurityBindingCookie] ?? "";
        if (!await selfService.IsVerifiedAsync(input.RequestId ?? "", user, binding, AccountSelfService.PasswordPurpose)) return Failed("AccountVerificationExpired");
        if (string.IsNullOrEmpty(input.Password) || input.Password != input.ConfirmPassword)
        { ModelState.AddModelError(string.Empty, localizer["AccountPasswordsMismatch"]); return View(input); }
        var result = await selfService.CreatePasswordAsync(input.RequestId ?? "", user, binding, input.Password);
        if (!result.Succeeded)
        { ModelState.AddModelError(string.Empty, localizer[result.Errors.Any(x => x.Code == "AccountVerificationExpired") ? "AccountVerificationExpired" : "AccountPasswordPolicy"]); return View(input); }
        Response.Cookies.Delete(SecurityBindingCookie, new CookieOptions { Path = "/account" });
        await signIn.RefreshSignInAsync(user);
        TempData["Success"] = localizer["AccountPasswordCreated"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("confirm-sessions")]
    public async Task<IActionResult> ConfirmSessions(string requestId)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        return await selfService.IsVerifiedAsync(requestId, user, Request.Cookies[SecurityBindingCookie] ?? "", AccountSelfService.SessionsPurpose)
            ? View("ConfirmSessions", requestId) : Failed("AccountVerificationExpired");
    }

    [HttpPost("revoke-sessions"), ValidateAntiForgeryToken]
    public async Task<IActionResult> RevokeSessions(string? password, string? requestId)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (await users.HasPasswordAsync(user) && (string.IsNullOrEmpty(password) || !(await signIn.CheckPasswordSignInAsync(user, password, true)).Succeeded))
            return Failed("CurrentPasswordIncorrect");
        var result = await selfService.RevokeSessionsAsync(user, requestId, Request.Cookies[SecurityBindingCookie]);
        if (!result.Succeeded) return Failed("AccountVerificationExpired");
        await signIn.SignOutAsync(); await HttpContext.SignOutAsync("MicrosoftLinkCookie");
        Response.Cookies.Delete(SecurityBindingCookie, new CookieOptions { Path = "/account" });
        TempData["StatusMessage"] = localizer["AccountSessionsRevoked"].Value;
        return RedirectToAction("Login", "Account");
    }

    [HttpPost("connect"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Connect(string? password)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (MicrosoftAccountService.IsRecoveryAdmin(user) || !Request.IsHttps) return Failed("MicrosoftUnavailable");
        if (string.IsNullOrEmpty(password) || !(await signIn.CheckPasswordSignInAsync(user, password, true)).Succeeded)
            return Failed("CurrentPasswordIncorrect");
        var binding = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var id = await accounts.BeginLinkAsync(user, binding);
        if (id is null) return Failed("MicrosoftAccountUnavailable");
        Response.Cookies.Append(BindingCookie, binding, new CookieOptions
        {
            HttpOnly = true, Secure = true, SameSite = SameSiteMode.Lax, MaxAge = TimeSpan.FromMinutes(5), IsEssential = true, Path = "/"
        });
        await HttpContext.SignOutAsync("MicrosoftLinkCookie");
        var properties = new AuthenticationProperties { RedirectUri = Url.Action("Callback", "MicrosoftSignIn") };
        properties.Items["link-intent"] = id;
        return Challenge(properties, "MicrosoftLink");
    }

    [HttpGet("confirm")]
    public async Task<IActionResult> Confirm(string intentId)
    {
        Response.Headers.CacheControl = "no-store";
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var model = await accounts.PreviewAsync(intentId, user, Request.Cookies[BindingCookie] ?? "");
        return model is null ? Failed("MicrosoftLinkExpired") : View(model);
    }

    [HttpPost("confirm"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ConfirmLink(string intentId)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var error = await accounts.ConfirmAsync(intentId, user, Request.Cookies[BindingCookie] ?? "");
        Response.Cookies.Delete(BindingCookie);
        if (error is not null) return Failed(error);
        await signIn.SignInAsync(user, false);
        TempData["Success"] = localizer["MicrosoftLinkSuccess"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("cancel"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Cancel(string intentId)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        await accounts.CancelAsync(intentId, user, Request.Cookies[BindingCookie] ?? "");
        Response.Cookies.Delete(BindingCookie);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("disconnect"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Disconnect(string? password)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        if (string.IsNullOrEmpty(password) || !(await signIn.CheckPasswordSignInAsync(user, password, true)).Succeeded)
            return Failed("CurrentPasswordIncorrect");
        var error = await accounts.UnlinkAsync(user);
        if (error is not null) return Failed(error);
        await signIn.SignInAsync(user, false);
        TempData["Success"] = localizer["MicrosoftUnlinkSuccess"].Value;
        return RedirectToAction(nameof(Index));
    }

    private IActionResult Failed(string key)
    {
        TempData["ErrorMessage"] = localizer[key].Value;
        return RedirectToAction(nameof(Index));
    }
}
