using Splitbill.Models;
using Splitbill.ViewModels;

namespace Splitbill.Services;

public interface IDashboardAnalyticsService
{
    DashboardAnalyticsResult Calculate(IReadOnlyList<BillTransaction> transactions, DateTimeOffset now);
    DashboardMemberAnalyticsResult CalculateMember(IReadOnlyList<TransactionParticipant> participants, DateTimeOffset now);
}

public sealed record DashboardAnalyticsResult(
    IReadOnlyList<DashboardOutstandingPersonViewModel> TopOutstandingPeople,
    DashboardOldestOutstandingViewModel? OldestOutstanding,
    IReadOnlyList<DashboardMonthlySpendViewModel> MonthlySpending,
    IReadOnlyList<DashboardMerchantViewModel> TopMerchants,
    decimal? AverageSettlementHours,
    int SettledTransactionCount);

public sealed record DashboardMemberAnalyticsResult(
    decimal CurrentMonthAmount,
    decimal PreviousMonthAmount,
    decimal PaidAmount,
    decimal OutstandingAmount,
    IReadOnlyList<DashboardMemberMonthViewModel> MonthlySpending,
    IReadOnlyList<DashboardMemberBillViewModel> RecentBills);

public sealed class DashboardAnalyticsService : IDashboardAnalyticsService
{
    public DashboardAnalyticsResult Calculate(IReadOnlyList<BillTransaction> transactions, DateTimeOffset now)
    {
        var finalized = transactions.Where(x => x.Status != TransactionStatus.Draft).ToList();
        var outstandingParticipants = finalized
            .SelectMany(x => x.Participants.Select(p => new { Transaction = x, Participant = p }))
            .Where(x => x.Participant.PaymentStatus != ParticipantPaymentStatus.Paid)
            .ToList();

        var people = outstandingParticipants
            .GroupBy(x => x.Participant.AccountLink is not null
                    ? $"user:{x.Participant.AccountLink.UserId}"
                    : $"guest:{x.Participant.Name.Trim()}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new DashboardOutstandingPersonViewModel(
                group.Select(x => x.Participant.Name.Trim()).FirstOrDefault(x => x.Length > 0) ?? "-",
                group.Sum(x => x.Participant.Amount),
                group.Select(x => x.Transaction.Id).Distinct().Count()))
            .OrderByDescending(x => x.Amount)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(5)
            .ToList();

        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var oldest = finalized
            .Select(x => new
            {
                Transaction = x,
                Date = EffectiveDate(x),
                Amount = x.Participants.Where(p => p.PaymentStatus != ParticipantPaymentStatus.Paid).Sum(p => p.Amount)
            })
            .Where(x => x.Amount > 0)
            .OrderBy(x => x.Date)
            .ThenBy(x => x.Transaction.UploadDate)
            .FirstOrDefault();
        var oldestModel = oldest is null ? null : new DashboardOldestOutstandingViewModel(
            oldest.Transaction.Id,
            oldest.Transaction.MerchantName,
            oldest.Transaction.TransactionNumber,
            oldest.Date,
            Math.Max(0, today.DayNumber - oldest.Date.DayNumber),
            oldest.Amount);

        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var months = Enumerable.Range(0, 6)
            .Select(offset => currentMonth.AddMonths(offset - 5))
            .ToList();
        var totalsByMonth = finalized
            .GroupBy(x =>
            {
                var date = EffectiveDate(x);
                return new DateOnly(date.Year, date.Month, 1);
            })
            .ToDictionary(x => x.Key, x => x.Sum(transaction => transaction.GrandTotal));
        var maximumMonthlyAmount = months.Select(month => totalsByMonth.GetValueOrDefault(month)).DefaultIfEmpty().Max();
        var monthly = months.Select(month =>
        {
            var amount = totalsByMonth.GetValueOrDefault(month);
            var barPercent = maximumMonthlyAmount <= 0 ? 0 : Math.Round(amount / maximumMonthlyAmount * 100m, 2);
            return new DashboardMonthlySpendViewModel(month, amount, barPercent);
        }).ToList();

        var merchants = finalized
            .GroupBy(x => x.MerchantName.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(group => new DashboardMerchantViewModel(
                group.Key.Length == 0 ? "-" : group.Key,
                group.Count(),
                group.Sum(x => x.GrandTotal)))
            .OrderByDescending(x => x.TransactionCount)
            .ThenByDescending(x => x.TotalAmount)
            .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(5)
            .ToList();

        var settlementHours = finalized
            .Where(x => x.CreatedAt != default && x.Participants.Count > 0 && x.Participants.All(p => p.PaymentStatus == ParticipantPaymentStatus.Paid && p.PaidAt.HasValue))
            .Select(x => x.Participants.Max(p => p.PaidAt!.Value) - x.CreatedAt)
            .Where(duration => duration >= TimeSpan.Zero)
            .Select(duration => (decimal)duration.TotalHours)
            .ToList();

        return new DashboardAnalyticsResult(
            people,
            oldestModel,
            monthly,
            merchants,
            settlementHours.Count == 0 ? null : Math.Round(settlementHours.Average(), 1),
            settlementHours.Count);
    }

    public DashboardMemberAnalyticsResult CalculateMember(IReadOnlyList<TransactionParticipant> participants, DateTimeOffset now)
    {
        var visible = participants
            .Where(x => x.Transaction is not null && x.Transaction.Status != TransactionStatus.Draft)
            .ToList();
        var today = DateOnly.FromDateTime(now.LocalDateTime);
        var currentMonth = new DateOnly(today.Year, today.Month, 1);
        var months = Enumerable.Range(0, 6).Select(offset => currentMonth.AddMonths(offset - 5)).ToList();

        var monthlyTotals = visible
            .GroupBy(x => MonthOf(x.Transaction!))
            .ToDictionary(group => group.Key, group => new
            {
                Total = group.Sum(x => x.Amount),
                Paid = group.Where(x => x.PaymentStatus == ParticipantPaymentStatus.Paid).Sum(x => x.Amount),
                Outstanding = group.Where(x => x.PaymentStatus != ParticipantPaymentStatus.Paid).Sum(x => x.Amount)
            });
        var maximum = months.Select(month => monthlyTotals.TryGetValue(month, out var value) ? value.Total : 0m).DefaultIfEmpty().Max();
        var history = months.Select(month =>
        {
            monthlyTotals.TryGetValue(month, out var value);
            var total = value?.Total ?? 0m;
            return new DashboardMemberMonthViewModel(
                month,
                total,
                value?.Paid ?? 0m,
                value?.Outstanding ?? 0m,
                maximum <= 0 ? 0 : Math.Round(total / maximum * 100m, 2));
        }).ToList();

        var recent = visible
            .OrderByDescending(x => EffectiveDate(x.Transaction!))
            .ThenByDescending(x => x.Transaction!.UploadDate)
            .Take(6)
            .Select(x => new DashboardMemberBillViewModel(
                x.Id,
                x.Transaction!.MerchantName,
                x.Transaction.TransactionNumber,
                EffectiveDate(x.Transaction),
                x.Amount,
                x.PaymentStatus))
            .ToList();

        return new DashboardMemberAnalyticsResult(
            history.Last().TotalAmount,
            history.Count > 1 ? history[^2].TotalAmount : 0,
            visible.Where(x => x.PaymentStatus == ParticipantPaymentStatus.Paid).Sum(x => x.Amount),
            visible.Where(x => x.PaymentStatus != ParticipantPaymentStatus.Paid).Sum(x => x.Amount),
            history,
            recent);
    }

    private static DateOnly EffectiveDate(BillTransaction transaction) =>
        transaction.TransactionDate ?? DateOnly.FromDateTime(transaction.UploadDate.LocalDateTime);

    private static DateOnly MonthOf(BillTransaction transaction)
    {
        var date = EffectiveDate(transaction);
        return new DateOnly(date.Year, date.Month, 1);
    }
}
