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

[AllowAnonymous]
public sealed class SetupController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    SignInManager<ApplicationUser> signInManager, IInstallationSetupService setupService,
    IStringLocalizer<SharedResource> localizer) : Controller
{
    [HttpGet("setup")]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var state = await setupService.EnsureStateAsync(db, cancellationToken);
        if (state.SetupCompletedAt is not null || await db.Users.AnyAsync(cancellationToken))
            return RedirectToAction("Login", "Account");
        return View(new FirstAdminSetupViewModel());
    }

    [HttpPost("setup"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Index(FirstAdminSetupViewModel model, CancellationToken cancellationToken)
    {
        var state = await setupService.EnsureStateAsync(db, cancellationToken);
        if (state.SetupCompletedAt is not null || await db.Users.AnyAsync(cancellationToken))
            return RedirectToAction("Login", "Account");
        if (!setupService.VerifyBootstrapCode(state, model.BootstrapCode))
            ModelState.AddModelError(nameof(model.BootstrapCode), localizer["BootstrapCodeInvalid"].Value);
        if (string.IsNullOrWhiteSpace(model.Username) || model.Username.Trim().Length < 3)
            ModelState.AddModelError(nameof(model.Username), localizer["UsernameRequired"].Value);
        if (!ModelState.IsValid) return View(model);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await db.Users.AnyAsync(cancellationToken))
            return RedirectToAction("Login", "Account");

        var user = new ApplicationUser
        {
            UserName = model.Username.Trim(), DisplayName = model.DisplayName.Trim(),
            Email = string.IsNullOrWhiteSpace(model.Email) ? null : model.Email.Trim(),
            EmailConfirmed = true, CreatedAt = DateTimeOffset.UtcNow
        };
        var result = await userManager.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            await transaction.RollbackAsync(cancellationToken);
            return View(model);
        }
        result = await userManager.AddToRoleAsync(user, DatabaseSeeder.AdminRole);
        if (!result.Succeeded)
        {
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            await transaction.RollbackAsync(cancellationToken);
            return View(model);
        }

        state.SetupCompletedAt = DateTimeOffset.UtcNow;
        state.BootstrapCodeHash = null;
        state.ProtectedBootstrapCode = null;
        state.BootstrapCodeExpiresAt = null;
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await signInManager.SignInAsync(user, isPersistent: false);
        TempData["Success"] = localizer["SetupComplete"].Value;
        return RedirectToAction("Index", "Dashboard");
    }
}
