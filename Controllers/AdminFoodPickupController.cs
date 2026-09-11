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
public sealed class AdminFoodPickupController(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IAdminUserService adminUserService,
    IFoodPickupRotationService rotationService,
    IStringLocalizer<SharedResource> localizer) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var configuration = await db.FoodPickupConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        return View(await BuildViewModelAsync(
            configuration?.Enabled ?? false,
            configuration?.Strategy ?? FoodPickupSelectionStrategy.WeightedRandom,
            cancellationToken));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(FoodPickupSettingsPostViewModel input, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(input.Strategy))
            ModelState.AddModelError(nameof(input.Strategy), localizer["FoodPickupInvalidStrategy"]);
        var requestedIds = (input.EligibleUserIds ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var users = await userManager.Users.AsNoTracking().ToListAsync(cancellationToken);
        var byId = users.ToDictionary(x => x.Id, StringComparer.Ordinal);
        var now = DateTimeOffset.UtcNow;
        var invalidIds = requestedIds.Where(id => !byId.TryGetValue(id, out var user) || IsDisabled(user, now)).ToArray();
        if (invalidIds.Length > 0 || !ModelState.IsValid)
        {
            if (invalidIds.Length > 0)
                ModelState.AddModelError(nameof(input.EligibleUserIds), localizer["FoodPickupInvalidAccounts"]);
            return View("Index", await BuildViewModelAsync(input.Enabled, input.Strategy, cancellationToken));
        }

        var configuration = await db.FoodPickupConfigurations
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken) ?? new FoodPickupConfiguration { Id = 1 };
        configuration.Enabled = input.Enabled;
        configuration.Strategy = input.Strategy;
        configuration.UpdatedAt = now;
        configuration.UpdatedByUserId = CurrentUserId();
        if (db.Entry(configuration).State == EntityState.Detached) db.FoodPickupConfigurations.Add(configuration);

        var currentRows = await db.FoodPickupEligibleUsers.ToListAsync(cancellationToken);
        var requested = requestedIds.ToHashSet(StringComparer.Ordinal);
        db.FoodPickupEligibleUsers.RemoveRange(currentRows.Where(x => !requested.Contains(x.UserId)));
        var existingIds = currentRows.Select(x => x.UserId).ToHashSet(StringComparer.Ordinal);
        foreach (var userId in requestedIds.Where(id => !existingIds.Contains(id)))
            db.FoodPickupEligibleUsers.Add(new FoodPickupEligibleUser
            {
                UserId = userId, EnabledAt = now, EnabledByUserId = CurrentUserId()
            });

        await db.SaveChangesAsync(cancellationToken);
        TempData["Success"] = localizer["FoodPickupSettingsSaved"].Value;
        return RedirectToAction(nameof(Index));
    }

    private async Task<FoodPickupSettingsViewModel> BuildViewModelAsync(
        bool enabled,
        FoodPickupSelectionStrategy strategy,
        CancellationToken cancellationToken)
    {
        var users = await adminUserService.ListAsync(new AdminUserListQuery(), cancellationToken);
        var statistics = await rotationService.GetStatisticsAsync(cancellationToken);
        var statById = statistics.Users.ToDictionary(x => x.UserId, StringComparer.Ordinal);
        var displayById = users.ToDictionary(x => x.Id, x => x.DisplayName, StringComparer.Ordinal);
        return new FoodPickupSettingsViewModel
        {
            Enabled = enabled,
            Strategy = strategy,
            Users = users.Select(user =>
            {
                var stat = statById.GetValueOrDefault(user.Id);
                return new FoodPickupUserRowViewModel(
                    user.Id, user.Username, user.DisplayName, user.Role,
                    stat?.IsEligible ?? false, user.IsDisabled,
                    stat?.ParticipationCount ?? 0, stat?.PickupCount ?? 0,
                    stat?.PickupRatio ?? 0m, stat?.LastPickupAt, stat?.LastPickupMerchant);
            }).ToArray(),
            History = statistics.History.Select(row => new FoodPickupHistoryRowViewModel(
                row.TransactionId, row.TransactionNumber, row.MerchantName,
                row.PreviousSelectedUserId is { } previous ? displayById.GetValueOrDefault(previous, previous) : null,
                displayById.GetValueOrDefault(row.SelectedUserId, row.SelectedUserId),
                row.Strategy, row.DrawKind, row.Reason, row.CreatedAt)).ToArray()
        };
    }

    private string CurrentUserId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty;

    private static bool IsDisabled(ApplicationUser user, DateTimeOffset now)
        => user.LockoutEnd is not null && user.LockoutEnd > now;
}
