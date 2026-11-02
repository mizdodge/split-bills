using System.Globalization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize(Roles = $"{DatabaseSeeder.AdminRole},{DatabaseSeeder.ModeratorRole},{DatabaseSeeder.MemberRole}")]
public sealed class ReportsController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    IReportQueryService reportQueryService, IExcelReportService excelReportService, IStringLocalizer<SharedResource> localizer) : Controller
{
    public async Task<IActionResult> Index(DateTime? from, DateTime? to, TransactionStatus? status, string? uploaderId, CancellationToken cancellationToken, FoodPickupSelectionStrategy? pickupStrategy = null)
    {
        var fromValue = from?.Date;
        var toValue = to?.Date;
        if (fromValue.HasValue && toValue.HasValue && fromValue.Value > toValue.Value)
        {
            ModelState.AddModelError(string.Empty, localizer["DateRangeInvalid"]);
            (fromValue, toValue) = (toValue, fromValue);
        }
        var result = await reportQueryService.QueryAsync(User,
            fromValue.HasValue ? DateOnly.FromDateTime(fromValue.Value) : null,
            toValue.HasValue ? DateOnly.FromDateTime(toValue.Value) : null,
            status, uploaderId, cancellationToken);
        var isAdmin = User.IsInRole(DatabaseSeeder.AdminRole);
        var eligiblePickupIds = isAdmin
            ? (await db.FoodPickupEligibleUsers.AsNoTracking().Select(x => x.UserId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal)
            : null;
        var strategy = await ResolvePickupStrategyAsync(pickupStrategy, cancellationToken);
        var pickupPeople = ReportPickupSummary.Build(result.Participants, strategy).Select(person => new ReportPickupPersonRowViewModel
        {
            ParticipantName = person.Name, Username = person.Username, ParticipationCount = person.ParticipationCount,
            PickupCount = person.PickupCount, PickupRatio = person.PickupRatio, LastPickupAt = person.LastPickupAt,
            LastPickupMerchant = person.LastMerchant, PriorityPoints = person.CurrentPoints,
            IsEligible = eligiblePickupIds is null ? null : result.Participants.Any(x => x.Username == person.Username && x.AccountUserId != null && eligiblePickupIds.Contains(x.AccountUserId))
        }).ToList();
        return View(new ReportViewModel
        {
            From = fromValue, To = toValue, Status = status, UploaderId = uploaderId, IsAdmin = isAdmin, PickupStrategy = strategy,
            IsMember = User.IsInRole(DatabaseSeeder.MemberRole) && !User.IsInRole(DatabaseSeeder.ModeratorRole),
            Uploaders = isAdmin ? await userManager.Users.Where(user => db.Transactions.Any(transaction => transaction.UploadedByUserId == user.Id)).OrderBy(x => x.DisplayName).ToListAsync(cancellationToken) : [],
            TotalTransactions = result.TotalTransactions,
            TotalAmount = result.TotalAmount,
            PaidAmount = result.PaidAmount,
            AwaitingAmount = result.AwaitingAmount,
            UnpaidAmount = result.UnpaidAmount,
            OutstandingAmount = result.OutstandingAmount,
            Transactions = this.Paginate(result.Transactions.Select(x => new ReportTransactionRowViewModel
            {
                Id = x.Id, TransactionNumber = x.TransactionNumber, MerchantName = x.MerchantName,
                EffectiveDate = x.EffectiveDate, UploaderName = x.UploaderName, GrandTotal = x.GrandTotal,
                PaidAmount = x.Participants.Sum(p => p.PaidAmount), AwaitingAmount = x.Participants.Sum(p => p.AwaitingAmount),
                UnpaidAmount = x.Participants.Sum(p => p.UnpaidAmount), Status = x.Status, ParticipantCount = x.Participants.Count,
                PickupPersonName = x.PickupPersonName, PickupStrategy = x.PickupStrategy
            })),
            PickupPeople = this.Paginate(pickupPeople, "pickupPage"),
            PointHistory = strategy == FoodPickupSelectionStrategy.Points ? this.Paginate(result.PointHistory, "pointHistoryPage") : []
        });
    }

    public async Task<IActionResult> Details(long id, CancellationToken cancellationToken)
    {
        var result = await reportQueryService.QueryAsync(User, null, null, null, null, cancellationToken);
        var transaction = result.Transactions.SingleOrDefault(x => x.Id == id);
        return transaction is null ? NotFound() : View(new ReportDetailsViewModel { Transaction = transaction,
            PointHistory = this.Paginate(result.PointHistory.Where(x => x.TransactionId == id), "pointHistoryPage") });
    }

    [HttpGet]
    public async Task<IActionResult> Export(DateTime? from, DateTime? to, TransactionStatus? status, string? uploaderId, CancellationToken cancellationToken, FoodPickupSelectionStrategy? pickupStrategy = null)
    {
        var fromValue = from?.Date;
        var toValue = to?.Date;
        if (fromValue.HasValue && toValue.HasValue && fromValue.Value > toValue.Value)
            return BadRequest(localizer["DateRangeInvalid"].Value);
        var result = await reportQueryService.QueryAsync(User,
            fromValue.HasValue ? DateOnly.FromDateTime(fromValue.Value) : null,
            toValue.HasValue ? DateOnly.FromDateTime(toValue.Value) : null,
            status, uploaderId, cancellationToken);
        result.PickupStrategy = await ResolvePickupStrategyAsync(pickupStrategy, cancellationToken);
        var bytes = excelReportService.Build(result, CultureInfo.CurrentCulture);
        var fromLabel = fromValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "all";
        var toLabel = toValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "all";
        var filename = $"SplitBill_Report_{fromLabel}_{toLabel}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }

    private async Task<FoodPickupSelectionStrategy> ResolvePickupStrategyAsync(FoodPickupSelectionStrategy? strategy, CancellationToken cancellationToken)
        => strategy.HasValue && Enum.IsDefined(strategy.Value) ? strategy.Value :
            await db.FoodPickupConfigurations.AsNoTracking().Where(x => x.Id == 1)
                .Select(x => x.Strategy).SingleOrDefaultAsync(cancellationToken);
}
