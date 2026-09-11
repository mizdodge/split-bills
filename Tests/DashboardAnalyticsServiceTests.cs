using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class DashboardAnalyticsServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 10, 12, 0, 0, TimeSpan.FromHours(7));
    private readonly DashboardAnalyticsService service = new();

    [Fact]
    public void OperationalAnalytics_AggregatesOutstandingMonthsMerchantsAndSettlement()
    {
        var settledAt = Now.AddDays(-2);
        var transactions = new List<BillTransaction>
        {
            Transaction(1, "Old Cafe", new DateOnly(2026, 8, 1), 80_000, TransactionStatus.Unpaid,
                Participant("Mizan", 80_000, ParticipantPaymentStatus.Unpaid, "mizan")),
            Transaction(2, "Popular Cafe", new DateOnly(2026, 9, 2), 50_000, TransactionStatus.Partial,
                Participant("Mizan", 20_000, ParticipantPaymentStatus.AwaitingConfirmation, "mizan"),
                Participant("Ragil", 30_000, ParticipantPaymentStatus.Paid, "ragil", Now.AddDays(-3))),
            Transaction(3, "Popular Cafe", new DateOnly(2026, 9, 5), 40_000, TransactionStatus.Paid,
                Participant("Agi", 40_000, ParticipantPaymentStatus.Paid, "agi", settledAt)),
            Transaction(4, "Ignored Draft", new DateOnly(2026, 9, 6), 500_000, TransactionStatus.Draft)
        };
        transactions[2].CreatedAt = settledAt.AddHours(-48);

        var result = service.Calculate(transactions, Now);

        var person = Assert.Single(result.TopOutstandingPeople);
        Assert.Equal("Mizan", person.Name);
        Assert.Equal(100_000m, person.Amount);
        Assert.Equal(2, person.BillCount);
        Assert.Equal(1, result.OldestOutstanding!.TransactionId);
        Assert.Equal(40, result.OldestOutstanding.AgeDays);
        Assert.Equal(80_000m, result.MonthlySpending.Single(x => x.Month == new DateOnly(2026, 8, 1)).Amount);
        Assert.Equal(90_000m, result.MonthlySpending.Single(x => x.Month == new DateOnly(2026, 9, 1)).Amount);
        Assert.Equal("Popular Cafe", result.TopMerchants[0].Name);
        Assert.Equal(2, result.TopMerchants[0].TransactionCount);
        Assert.Equal(48m, result.AverageSettlementHours);
        Assert.Equal(1, result.SettledTransactionCount);
    }

    [Fact]
    public void MemberAnalytics_UsesOnlyProvidedParticipantsAndSeparatesPaidFromOutstanding()
    {
        var august = Transaction(1, "August Cafe", new DateOnly(2026, 8, 12), 30_000, TransactionStatus.Paid,
            Participant("Mizan", 30_000, ParticipantPaymentStatus.Paid, "mizan", Now.AddDays(-20)));
        var september = Transaction(2, "September Cafe", new DateOnly(2026, 9, 4), 45_000, TransactionStatus.Unpaid,
            Participant("Mizan", 45_000, ParticipantPaymentStatus.Unpaid, "mizan"),
            Participant("Other", 90_000, ParticipantPaymentStatus.Unpaid, "other"));

        var result = service.CalculateMember([august.Participants[0], september.Participants[0]], Now);

        Assert.Equal(45_000m, result.CurrentMonthAmount);
        Assert.Equal(30_000m, result.PreviousMonthAmount);
        Assert.Equal(30_000m, result.PaidAmount);
        Assert.Equal(45_000m, result.OutstandingAmount);
        Assert.Equal(45_000m, result.MonthlySpending.Last().TotalAmount);
        Assert.Equal(45_000m, result.MonthlySpending.Last().OutstandingAmount);
        Assert.Equal(2, result.RecentBills.Count);
        Assert.DoesNotContain(result.RecentBills, x => x.Amount == 90_000m);
    }

    private static BillTransaction Transaction(long id, string merchant, DateOnly date, decimal total,
        TransactionStatus status, params TransactionParticipant[] participants)
    {
        var transaction = new BillTransaction
        {
            Id = id,
            TransactionNumber = $"TRX-{id}",
            MerchantName = merchant,
            TransactionDate = date,
            UploadDate = Now,
            CreatedAt = Now.AddDays(-5),
            GrandTotal = total,
            Status = status,
            Participants = participants.ToList()
        };
        foreach (var participant in participants) participant.Transaction = transaction;
        return transaction;
    }

    private static TransactionParticipant Participant(string name, decimal amount, ParticipantPaymentStatus status,
        string userId, DateTimeOffset? paidAt = null) => new()
    {
        Name = name,
        Amount = amount,
        PaymentStatus = status,
        PaidAt = paidAt,
        AccountLink = new ParticipantAccountLink { UserId = userId }
    };
}
