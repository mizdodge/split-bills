using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize(Roles = DatabaseSeeder.AdminRole)]
public sealed class AdminUsersController(
    IAdminUserService adminUserService,
    UserManager<ApplicationUser> userManager,
    IStringLocalizer<SharedResource> localizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(string? search, string? role, string? status, CancellationToken cancellationToken)
    {
        var users = await adminUserService.ListAsync(new AdminUserListQuery(search, role, status), cancellationToken);
        return View(new AdminUserIndexViewModel
        {
            Search = search,
            Role = role,
            Status = status,
            Users = users.Select(x => new AdminUserRowViewModel(x.Id, x.Username, x.DisplayName, x.Email, x.Role, x.IsDisabled, x.CreatedAt)).ToArray()
        });
    }

    [HttpGet]
    public IActionResult Create() => View(new AdminUserCreateViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AdminUserCreateViewModel input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(input);
        var result = await adminUserService.CreateAsync(CurrentUserId(), input.Username, input.DisplayName, input.Email,
            input.Role, input.Password, cancellationToken);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View(input);
        }
        TempData["Success"] = localizer["AdminUserCreated"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpGet]
    public async Task<IActionResult> Edit(string id, CancellationToken cancellationToken)
    {
        var users = await adminUserService.ListAsync(new AdminUserListQuery(), cancellationToken);
        var user = users.SingleOrDefault(x => string.Equals(x.Id, id, StringComparison.Ordinal));
        if (user is null) return NotFound();
        return View(new AdminUserEditViewModel
        {
            Id = user.Id, Username = user.Username, DisplayName = user.DisplayName, Email = user.Email ?? string.Empty,
            Role = user.Role, IsDisabled = user.IsDisabled
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(AdminUserEditViewModel input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(input);
        var result = await adminUserService.UpdateAsync(CurrentUserId(), input.Id, input.DisplayName, input.Email,
            input.Role, cancellationToken);
        if (!result.Succeeded)
        {
            AddErrors(result);
            return View(input);
        }
        TempData["Success"] = localizer["AdminUserUpdated"].Value;
        return RedirectToAction(nameof(Index));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ResetPassword(AdminUserResetPasswordViewModel input, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return RedirectToAction(nameof(Edit), new { id = input.UserId });
        var result = await adminUserService.ResetPasswordAsync(CurrentUserId(), input.UserId, input.Password, cancellationToken);
        if (!result.Succeeded)
        {
            TempData["Error"] = ErrorFor(result);
            return RedirectToAction(nameof(Edit), new { id = input.UserId });
        }
        TempData["Success"] = localizer["AdminUserPasswordReset"].Value;
        return RedirectToAction(nameof(Edit), new { id = input.UserId });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Disable(string id, CancellationToken cancellationToken)
        => SetDisabled(id, true, cancellationToken);

    [HttpPost, ValidateAntiForgeryToken]
    public Task<IActionResult> Enable(string id, CancellationToken cancellationToken)
        => SetDisabled(id, false, cancellationToken);

    private async Task<IActionResult> SetDisabled(string id, bool disabled, CancellationToken cancellationToken)
    {
        var result = await adminUserService.SetDisabledAsync(CurrentUserId(), id, disabled, cancellationToken);
        if (!result.Succeeded)
            TempData["Error"] = ErrorFor(result);
        else
            TempData["Success"] = localizer[disabled ? "AdminUserDisabled" : "AdminUserEnabled"].Value;
        return RedirectToAction(nameof(Index));
    }

    private string CurrentUserId()
        => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? userManager.GetUserId(User) ?? string.Empty;

    private void AddErrors(AdminUserMutationResult result)
    {
        ModelState.AddModelError(string.Empty, ErrorFor(result));
        foreach (var error in result.Errors)
            ModelState.AddModelError(string.Empty, error.Description);
    }

    private string ErrorFor(AdminUserMutationResult result) => result.ErrorCode switch
    {
        "DuplicateUsername" => localizer["AdminDuplicateUsername"].Value,
        "DuplicateEmail" => localizer["AdminDuplicateEmail"].Value,
        "InvalidRole" => localizer["AdminInvalidRole"].Value,
        "UserNotFound" => localizer["AdminUserNotFound"].Value,
        "SelfDemotion" => localizer["AdminSelfDemotion"].Value,
        "SelfDisable" => localizer["AdminSelfDisable"].Value,
        "LastAdmin" => localizer["AdminLastAdmin"].Value,
        "InvalidInput" => localizer["AdminInvalidUserInput"].Value,
        _ => localizer["AdminUserOperationFailed"].Value
    };
}
