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
    IMicrosoftAccountService accounts, IMicrosoftIntegrationService integration, IStringLocalizer<SharedResource> localizer) : Controller
{
    public const string BindingCookie = "SplitBill.LinkBinding";
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return Challenge();
        var settings = await integration.GetSettingsAsync();
        return View(new AccountSettingsViewModel(user, await users.HasPasswordAsync(user), settings.MicrosoftLoginEnabled && settings.HasStoredClientSecret));
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
