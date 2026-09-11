using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class ReportQueryServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly UserManager<ApplicationUser> users;

    public ReportQueryServiceTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser>(db), Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(), [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!, NullLogger<UserManager<ApplicationUser>>.Instance);
        db.Users.AddRange(new ApplicationUser { Id = "owner", UserName = "owner", DisplayName = "Owner" }, new ApplicationUser { Id = "member", UserName = "member", DisplayName = "Member" }, new ApplicationUser { Id = "other", UserName = "other", DisplayName = "Other" });
        var item = new TransactionItem { Name = "Nasi", Quantity = 1, UnitPrice = 100_000, TotalPrice = 100_000 };
        db.Transactions.AddRange(
            new BillTransaction
            {
                TransactionNumber = "REPORT-1", MerchantName = "Cafe", UploadedByUserId = "owner", TransactionDate = new DateOnly(2026, 9, 3), GrandTotal = 100_000, Status = TransactionStatus.Paid,
                Items = [item], Participants = [new TransactionParticipant { Name = "Member", Amount = 100_000, PaymentStatus = ParticipantPaymentStatus.Paid, AccountLink = new ParticipantAccountLink { UserId = "member", LinkedAt = DateTimeOffset.UtcNow } }]
            },
            new BillTransaction
            {
                TransactionNumber = "REPORT-UNPAID", MerchantName = "Unpaid Cafe", UploadedByUserId = "owner", TransactionDate = new DateOnly(2026, 8, 31), GrandTotal = 80_000, Status = TransactionStatus.Unpaid,
                Participants = [new TransactionParticipant { Name = "Guest", Amount = 80_000, PaymentStatus = ParticipantPaymentStatus.Unpaid }]
            },
            new BillTransaction
            {
                TransactionNumber = "REPORT-PARTIAL", MerchantName = "Partial Cafe", UploadedByUserId = "owner", TransactionDate = new DateOnly(2026, 7, 1), GrandTotal = 60_000, Status = TransactionStatus.Partial,
                Participants = [new TransactionParticipant { Name = "Paid Guest", Amount = 30_000, PaymentStatus = ParticipantPaymentStatus.Paid }, new TransactionParticipant { Name = "Unpaid Guest", Amount = 30_000, PaymentStatus = ParticipantPaymentStatus.Unpaid }]
            },
            new BillTransaction { TransactionNumber = "REPORT-DRAFT", MerchantName = "Draft Cafe", UploadedByUserId = "owner", TransactionDate = new DateOnly(2026, 9, 4), GrandTotal = 200_000, Status = TransactionStatus.Draft });
        db.SaveChanges();
        var pickupTransaction = db.Transactions.Include(x => x.Participants).Single(x => x.TransactionNumber == "REPORT-1");
        var pickupParticipant = pickupTransaction.Participants.Single();
        db.FoodPickupAssignments.Add(new FoodPickupAssignment
        {
            TransactionId = pickupTransaction.Id, SelectedUserId = "member", SelectedParticipantId = pickupParticipant.Id,
            RecordedProbability = 1m, DrawKind = FoodPickupDrawKind.Initial, SelectedAt = DateTimeOffset.UtcNow
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task MemberQuery_IsScopedToLinkedBills()
    {
        var result = await Service().QueryAsync(Principal("member", DatabaseSeeder.MemberRole), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null, null);

        Assert.Single(result.Transactions);
        Assert.Single(result.Participants);
        Assert.Equal(100_000m, result.TotalAmount);
        Assert.Equal(100_000m, result.PaidAmount);
        var member = Assert.Single(result.Participants);
        var item = Assert.Single(member.ItemDetails);
        Assert.Equal("Nasi", item.Name);
        Assert.Equal(1m, item.Quantity);
        Assert.Equal(100_000m, item.Amount);
    }

    [Fact]
    public async Task MemberQuery_IncludesEveryParticipantOnlyInsideTheirSharedTransaction()
    {
        db.Transactions.Add(new BillTransaction
        {
            TransactionNumber = "REPORT-SHARED",
            MerchantName = "Shared Cafe",
            UploadedByUserId = "owner",
            TransactionDate = new DateOnly(2026, 9, 10),
            GrandTotal = 100_000,
            Status = TransactionStatus.Unpaid,
            Participants =
            [
                new TransactionParticipant
                {
                    Name = "Member", Amount = 40_000, PaymentStatus = ParticipantPaymentStatus.Unpaid,
                    AccountLink = new ParticipantAccountLink { UserId = "member", LinkedAt = DateTimeOffset.UtcNow }
                },
                new TransactionParticipant
                {
                    Name = "Other", Amount = 60_000, PaymentStatus = ParticipantPaymentStatus.AwaitingConfirmation,
                    AccountLink = new ParticipantAccountLink { UserId = "other", LinkedAt = DateTimeOffset.UtcNow }
                }
            ]
        });
        await db.SaveChangesAsync();

        var result = await Service().QueryAsync(Principal("member", DatabaseSeeder.MemberRole),
            new DateOnly(2026, 9, 10), new DateOnly(2026, 9, 10), null, null);

        var transaction = Assert.Single(result.Transactions);
        Assert.Equal("REPORT-SHARED", transaction.TransactionNumber);
        Assert.Equal(2, transaction.Participants.Count);
        Assert.Contains(transaction.Participants, x => x.ParticipantName == "Other" &&
                                                      x.AwaitingAmount == 60_000m);
        Assert.Equal(100_000m, result.TotalAmount);
        Assert.Equal(100_000m, result.OutstandingAmount);
        Assert.DoesNotContain(result.Transactions, x => x.TransactionNumber == "REPORT-UNPAID");
    }

    [Fact]
    public async Task WinnerProjectionIncludesPickupMetadata()
    {
        var result = await Service().QueryAsync(Principal("member", DatabaseSeeder.MemberRole),
            new DateOnly(2026, 9, 3), new DateOnly(2026, 9, 3), null, null);

        var transaction = Assert.Single(result.Transactions);
        Assert.Equal("Member", transaction.PickupPersonName);
        var participant = Assert.Single(transaction.Participants);
        Assert.True(participant.IsPickupPerson);
        Assert.Equal(1m, participant.PickupProbability);
        Assert.NotNull(participant.PickupSelectedAt);
    }

    [Fact]
    public async Task AdminQuery_WithNoFilters_ReturnsEveryTransactionStatus()
    {
        var result = await Service().QueryAsync(Principal("owner", DatabaseSeeder.AdminRole), null, null, null, null);

        Assert.Equal(4, result.Transactions.Count);
        Assert.Equal(Enum.GetValues<TransactionStatus>().OrderBy(x => x), result.Transactions.Select(x => x.Status).OrderBy(x => x));
    }

    [Theory]
    [InlineData(TransactionStatus.Draft, "REPORT-DRAFT")]
    [InlineData(TransactionStatus.Unpaid, "REPORT-UNPAID")]
    [InlineData(TransactionStatus.Partial, "REPORT-PARTIAL")]
    [InlineData(TransactionStatus.Paid, "REPORT-1")]
    public async Task AdminQuery_StatusFilter_ReturnsOnlyRequestedStatus(TransactionStatus status, string transactionNumber)
    {
        var result = await Service().QueryAsync(Principal("owner", DatabaseSeeder.AdminRole), null, null, status, null);

        var transaction = Assert.Single(result.Transactions);
        Assert.Equal(transactionNumber, transaction.TransactionNumber);
    }

    [Fact]
    public async Task AdminQuery_DateRangeFiltersEffectiveReceiptDate()
    {
        var result = await Service().QueryAsync(Principal("owner", DatabaseSeeder.AdminRole), new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), null, null);

        Assert.Equal(new[] { "REPORT-1", "REPORT-DRAFT" }, result.Transactions.Select(x => x.TransactionNumber).OrderBy(x => x));
    }

    [Fact]
    public async Task ModeratorQuery_OnlySeesOwnedTransactions()
    {
        var result = await Service().QueryAsync(Principal("other", DatabaseSeeder.ModeratorRole), null, null, null, null);

        Assert.Empty(result.Transactions);
    }

    private ReportQueryService Service() => new(db, users);

    private static ClaimsPrincipal Principal(string userId, string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role)], "test"));

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
