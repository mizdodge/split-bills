using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using System.Text.Json;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize(Roles = DatabaseSeeder.AdminRole)]
public sealed class AdminSystemController(UserManager<ApplicationUser> userManager, IBackupService backupService,
    IStringLocalizer<SharedResource> localizer) : Controller
{
    [HttpGet]
    public IActionResult Index() => View(new SystemBackupViewModel());

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Backup(SystemBackupViewModel model, CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!ModelState.IsValid || !await userManager.CheckPasswordAsync(user, model.Password))
        {
            ModelState.AddModelError(nameof(model.Password), localizer["CurrentPasswordIncorrect"].Value);
            return View(nameof(Index), model);
        }
        var package = await backupService.CreateAsync(cancellationToken);
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Pragma = "no-cache";
        return PhysicalFile(package.PhysicalPath, "application/zip", package.DownloadName, enableRangeProcessing: true);
    }

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(536_870_912)]
    public async Task<IActionResult> StageRestore(SystemRestoreViewModel model, CancellationToken cancellationToken)
    {
        var user = await userManager.GetUserAsync(User);
        if (user is null) return Challenge();
        if (!ModelState.IsValid || !await userManager.CheckPasswordAsync(user, model.Password))
        {
            ModelState.AddModelError(nameof(model.Password), localizer["CurrentPasswordIncorrect"].Value);
            return View(nameof(Index), new SystemBackupViewModel());
        }
        try
        {
            var staged = await backupService.StageAsync(model.BackupFile!, cancellationToken);
            TempData["Success"] = localizer["RestoreStaged", staged.PackageName, staged.FileCount].Value;
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException)
        {
            TempData["Error"] = localizer["RestoreInvalid", exception.Message].Value;
        }
        return RedirectToAction(nameof(Index));
    }
}
