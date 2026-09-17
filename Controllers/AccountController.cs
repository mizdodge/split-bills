using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Microsoft.EntityFrameworkCore;
using Splitbill.Models;
using Splitbill.Data;
using Splitbill;
using Splitbill.ViewModels;
using Splitbill.Services;

namespace Splitbill.Controllers;

public sealed class AccountController(SignInManager<ApplicationUser> signInManager, UserManager<ApplicationUser> userManager,
    IStringLocalizer<SharedResource> localizer, IMicrosoftIntegrationService microsoftIntegration) : Controller
{
    [AllowAnonymous, HttpGet("account/login")]
    public async Task<IActionResult> Login(string? returnUrl = null, string? remoteError = null)
    {
        if (User.Identity?.IsAuthenticated == true) return RedirectToLanding();
        if (!userManager.Users.Any()) return RedirectToAction("Index", "Setup");
        if (!string.IsNullOrWhiteSpace(remoteError))
        {
            ModelState.AddModelError(string.Empty, remoteError);
        }
        ViewBag.ReturnUrl = returnUrl;
        var ms = await microsoftIntegration.GetSettingsAsync();
        ViewBag.ShowMicrosoftLogin = ms.MicrosoftLoginEnabled && ms.HasStoredClientSecret;
        return View(new LoginViewModel());
    }

    [AllowAnonymous, HttpPost("account/login"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        if (!await userManager.Users.AnyAsync()) return RedirectToAction("Index", "Setup");
        ViewBag.ReturnUrl = returnUrl;
        var ms = await microsoftIntegration.GetSettingsAsync();
        ViewBag.ShowMicrosoftLogin = ms.MicrosoftLoginEnabled && ms.HasStoredClientSecret;
        if (!ModelState.IsValid) return View(model);
        var user = await userManager.FindByNameAsync(model.Username.Trim());
        if (user is null)
        {
            ModelState.AddModelError(string.Empty, localizer["InvalidCredentials"]);
            return View(model);
        }
        var result = await signInManager.PasswordSignInAsync(user, model.Password, model.RememberMe, true);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.IsLockedOut ? localizer["AccountLocked"] : localizer["InvalidCredentials"]);
            return View(model);
        }
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : LandingUrl());
    }

    [Authorize, HttpPost("account/logout"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Logout()
    {
        await signInManager.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [Authorize, HttpGet("account/change-password")]
    public IActionResult ChangePassword() => View(new ChangePasswordViewModel());

    [Authorize, HttpPost("account/change-password"), ValidateAntiForgeryToken]
    public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
    {
        if (!ModelState.IsValid) return View(model);
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        var result = await userManager.ChangePasswordAsync(user, model.CurrentPassword, model.NewPassword);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, IdentityErrorMessage(error));
            return View(model);
        }
        await signInManager.RefreshSignInAsync(user);
        TempData["Success"] = localizer["PasswordChanged"].Value;
        // A password change is a completed POST. Use an explicit See Other so
        // every browser performs a fresh GET of the dashboard immediately.
        Response.StatusCode = StatusCodes.Status303SeeOther;
        Response.Headers.Location = LandingUrl();
        Response.Headers.CacheControl = "no-store";
        return new EmptyResult();
    }

    [AllowAnonymous, HttpGet("account/microsoft")]
    public IActionResult Microsoft(string? returnUrl = null)
        => RedirectToAction("Start", "MicrosoftSignIn", new { returnUrl });

    [AllowAnonymous, HttpGet("account/access-denied")]
    public IActionResult AccessDenied() => View();

    private string IdentityErrorMessage(IdentityError error) => error.Code switch
    {
        "PasswordMismatch" => localizer["CurrentPasswordIncorrect"].Value,
        "PasswordTooShort" => localizer["PasswordTooShort"].Value,
        "PasswordRequiresDigit" => localizer["PasswordRequiresDigit"].Value,
        "PasswordRequiresLower" => localizer["PasswordRequiresLower"].Value,
        _ => error.Description
    };

    private IActionResult RedirectToLanding() => RedirectToAction("Index", "Dashboard");
    private string LandingUrl() => Url.Action("Index", "Dashboard") ?? "/";
    private bool IsMemberOnly() => User.IsInRole(DatabaseSeeder.MemberRole) && !User.IsInRole(DatabaseSeeder.AdminRole) && !User.IsInRole(DatabaseSeeder.ModeratorRole);
}
