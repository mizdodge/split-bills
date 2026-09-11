using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize]
public sealed class DashboardController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    IDashboardAnalyticsService analytics) : Controller
{
    public async Task<IActionResult> Index()
    {
        var memberOnly = User.IsInRole(DatabaseSeeder.MemberRole) && !User.IsInRole(DatabaseSeeder.ModeratorRole) && !User.IsInRole(DatabaseSeeder.AdminRole);
        if (memberOnly)
        {
            var memberUserId = userManager.GetUserId(User)!;
            var participants = await db.Set<TransactionParticipant>().AsNoTracking()
                .Include(x => x.Transaction)
                .Include(x => x.AccountLink)
                .Where(x => x.AccountLink != null && x.AccountLink.UserId == memberUserId)
                .ToListAsync();
            var memberInsight = analytics.CalculateMember(participants, DateTimeOffset.Now);
            return View(new DashboardViewModel
            {
                IsMemberDashboard = true,
                MemberCurrentMonthAmount = memberInsight.CurrentMonthAmount,
                MemberPreviousMonthAmount = memberInsight.PreviousMonthAmount,
                MemberPaidAmount = memberInsight.PaidAmount,
                OutstandingAmount = memberInsight.OutstandingAmount,
                MemberMonthlySpending = memberInsight.MonthlySpending.ToList(),
                RecentMemberBills = memberInsight.RecentBills.ToList()
            });
        }
        var query = db.Transactions.AsNoTracking()
            .Include(x => x.Participants).ThenInclude(x => x.AccountLink)
            .AsQueryable();
        if (!User.IsInRole(DatabaseSeeder.AdminRole))
        {
            var userId = userManager.GetUserId(User);
            query = query.Where(x => x.UploadedByUserId == userId);
        }
        var transactions = (await query.ToListAsync()).OrderByDescending(x => x.UploadDate).ToList();
        var insight = analytics.Calculate(transactions, DateTimeOffset.Now);
        return View(new DashboardViewModel
        {
            TotalTransactions = transactions.Count,
            UnpaidTransactions = transactions.Count(x => x.Status == TransactionStatus.Unpaid),
            PartialTransactions = transactions.Count(x => x.Status == TransactionStatus.Partial),
            PaidTransactions = transactions.Count(x => x.Status == TransactionStatus.Paid),
            OutstandingAmount = transactions.SelectMany(x => x.Participants).Where(x => x.PaymentStatus != ParticipantPaymentStatus.Paid).Sum(x => x.Amount),
            RecentTransactions = transactions.Take(6).ToList(),
            TopOutstandingPeople = insight.TopOutstandingPeople.ToList(),
            OldestOutstanding = insight.OldestOutstanding,
            MonthlySpending = insight.MonthlySpending.ToList(),
            TopMerchants = insight.TopMerchants.ToList(),
            AverageSettlementHours = insight.AverageSettlementHours,
            SettledTransactionCount = insight.SettledTransactionCount
        });
    }
}
