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
    public async Task<IActionResult> Index(DateTime? from, DateTime? to, TransactionStatus? status, string? uploaderId, CancellationToken cancellationToken)
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
        var pickupPeople = result.Participants
            .Where(x => !string.IsNullOrWhiteSpace(x.Username))
            .GroupBy(x => x.Username!, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var rows = group.ToList();
                var pickups = rows.Where(x => x.IsPickupPerson && x.PickupSelectedAt.HasValue)
                    .OrderByDescending(x => x.PickupSelectedAt).ToList();
                return new ReportPickupPersonRowViewModel
                {
                    ParticipantName = rows.Select(x => x.ParticipantName).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? group.Key,
                    Username = group.Key,
                    ParticipationCount = rows.Select(x => x.TransactionId).Distinct().Count(),
                    PickupCount = pickups.Count,
                    PickupRatio = rows.Count == 0 ? 0m : (decimal)pickups.Count / rows.Select(x => x.TransactionId).Distinct().Count(),
                    LastPickupAt = pickups.FirstOrDefault()?.PickupSelectedAt,
                    LastPickupMerchant = pickups.FirstOrDefault()?.MerchantName,
                    IsEligible = eligiblePickupIds?.Contains(rows.First().Username!)
                };
            })
            .OrderBy(x => x.ParticipantName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return View(new ReportViewModel
        {
            From = fromValue, To = toValue, Status = status, UploaderId = uploaderId, IsAdmin = isAdmin,
            IsMember = User.IsInRole(DatabaseSeeder.MemberRole) && !User.IsInRole(DatabaseSeeder.ModeratorRole),
            Uploaders = isAdmin ? await userManager.Users.Where(user => db.Transactions.Any(transaction => transaction.UploadedByUserId == user.Id)).OrderBy(x => x.DisplayName).ToListAsync(cancellationToken) : [],
            TotalTransactions = result.TotalTransactions,
            TotalAmount = result.TotalAmount,
            PaidAmount = result.PaidAmount,
            AwaitingAmount = result.AwaitingAmount,
            UnpaidAmount = result.UnpaidAmount,
            OutstandingAmount = result.OutstandingAmount,
            Transactions = result.Transactions.Select(x => new ReportTransactionRowViewModel
            {
                Id = x.Id, TransactionNumber = x.TransactionNumber, MerchantName = x.MerchantName,
                EffectiveDate = x.EffectiveDate, UploaderName = x.UploaderName, GrandTotal = x.GrandTotal,
                PaidAmount = x.Participants.Sum(p => p.PaidAmount), AwaitingAmount = x.Participants.Sum(p => p.AwaitingAmount),
                UnpaidAmount = x.Participants.Sum(p => p.UnpaidAmount), Status = x.Status, ParticipantCount = x.Participants.Count,
                PickupPersonName = x.PickupPersonName
            }).ToList(),
            PickupPeople = pickupPeople
        });
    }

    public async Task<IActionResult> Details(long id, CancellationToken cancellationToken)
    {
        var result = await reportQueryService.QueryAsync(User, null, null, null, null, cancellationToken);
        var transaction = result.Transactions.SingleOrDefault(x => x.Id == id);
        return transaction is null ? NotFound() : View(new ReportDetailsViewModel { Transaction = transaction });
    }

    [HttpGet]
    public async Task<IActionResult> Export(DateTime? from, DateTime? to, TransactionStatus? status, string? uploaderId, CancellationToken cancellationToken)
    {
        var fromValue = from?.Date;
        var toValue = to?.Date;
        if (fromValue.HasValue && toValue.HasValue && fromValue.Value > toValue.Value)
            return BadRequest(localizer["DateRangeInvalid"].Value);
        var result = await reportQueryService.QueryAsync(User,
            fromValue.HasValue ? DateOnly.FromDateTime(fromValue.Value) : null,
            toValue.HasValue ? DateOnly.FromDateTime(toValue.Value) : null,
            status, uploaderId, cancellationToken);
        var bytes = excelReportService.Build(result, CultureInfo.CurrentCulture);
        var fromLabel = fromValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "all";
        var toLabel = toValue?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "all";
        var filename = $"SplitBill_Report_{fromLabel}_{toLabel}.xlsx";
        return File(bytes, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", filename);
    }
}
