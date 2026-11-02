using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using ClosedXML.Excel;
using Splitbill.Controllers;
using Splitbill.ViewModels;
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

    [Fact]
    public async Task ReportPagesPreserveFullTotalsAndFullExcelExport()
    {
        for (var index = 0; index < 27; index++)
            db.Transactions.Add(new BillTransaction
            {
                TransactionNumber = $"PAGE-{index:00}", MerchantName = "Pagination Cafe", UploadedByUserId = "owner",
                TransactionDate = new DateOnly(2026, 10, 1), Status = TransactionStatus.Unpaid, GrandTotal = 1000,
                Participants = [new TransactionParticipant { Name = "Guest", Amount = 1000 }]
            });
        await db.SaveChangesAsync();
        var controller = Controller(Principal("owner", DatabaseSeeder.AdminRole), "?page=2&pageSize=10");
        var view = Assert.IsType<ViewResult>(await controller.Index(null, null, null, null, default));
        var model = Assert.IsType<ReportViewModel>(view.Model);
        Assert.Equal(10, model.Transactions.Count);
        Assert.Equal(31, model.TotalTransactions);
        Assert.Equal(267_000, model.TotalAmount);
        Assert.Equal(2, Assert.IsType<PaginationViewModel>(controller.ViewData["Pagination:page"]).Page);
        var export = Assert.IsType<FileContentResult>(await controller.Export(null, null, null, null, default));
        using var workbook = new XLWorkbook(new MemoryStream(export.FileContents));
        Assert.Equal(267_000, workbook.Worksheet(1).Cell("B6").GetValue<decimal>());
        Assert.Equal(31, workbook.Worksheet(2).Table("PaymentDetails").DataRange.RowCount());

        var other = Controller(Principal("other", DatabaseSeeder.ModeratorRole), "?page=2");
        var scoped = Assert.IsType<ReportViewModel>(Assert.IsType<ViewResult>(await other.Index(null, null, null, null, default)).Model);
        Assert.Empty(scoped.Transactions); Assert.Equal(0, scoped.TotalTransactions);
    }

    [Fact]
    public async Task ReportPickupMethodFiltersStatisticsAndExportWithoutFilteringPayments()
    {
        db.FoodPickupConfigurations.Add(new FoodPickupConfiguration { Id = 1, Strategy = FoodPickupSelectionStrategy.RoundRobin });
        var transaction = new BillTransaction
        {
            TransactionNumber = "REPORT-ROUND", MerchantName = "Round Cafe", UploadedByUserId = "owner",
            Status = TransactionStatus.Unpaid, GrandTotal = 100_000,
            Participants = [new TransactionParticipant { Name = "Member", Amount = 40_000, AccountLink = new ParticipantAccountLink { UserId = "member" } },
                new TransactionParticipant { Name = "Other", Amount = 60_000, AccountLink = new ParticipantAccountLink { UserId = "other" } }]
        };
        db.Transactions.Add(transaction); await db.SaveChangesAsync();
        db.FoodPickupAssignments.Add(new FoodPickupAssignment { TransactionId = transaction.Id, SelectedUserId = "other",
            SelectedParticipantId = transaction.Participants[1].Id, Strategy = FoodPickupSelectionStrategy.RoundRobin, SelectedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var controller = Controller(Principal("owner", DatabaseSeeder.AdminRole));
        var round = Assert.IsType<ReportViewModel>(Assert.IsType<ViewResult>(await controller.Index(null, null, null, null, default)).Model);
        Assert.Equal(FoodPickupSelectionStrategy.RoundRobin, round.PickupStrategy);
        Assert.Equal(2, round.PickupPeople.Count);
        Assert.Equal(1, round.PickupPeople.Single(x => x.Username == "other").PickupCount);
        Assert.Equal(0, round.PickupPeople.Single(x => x.Username == "member").PickupCount);
        Assert.All(round.PickupPeople, x => Assert.Equal(1, x.ParticipationCount));
        var weighted = Assert.IsType<ReportViewModel>(Assert.IsType<ViewResult>(await controller.Index(null, null, null, null, default, FoodPickupSelectionStrategy.WeightedRandom)).Model);
        Assert.Equal("member", Assert.Single(weighted.PickupPeople).Username);
        Assert.Equal(round.TotalAmount, weighted.TotalAmount);
        Assert.Equal(round.Transactions.Count, weighted.Transactions.Count);
        var export = Assert.IsType<FileContentResult>(await controller.Export(null, null, null, null, default, FoodPickupSelectionStrategy.RoundRobin));
        using var workbook = new XLWorkbook(new MemoryStream(export.FileContents));
        var pickup = workbook.Worksheet(4);
        Assert.Equal("Round Robin", pickup.Cell("B2").GetString());
        Assert.Equal(2, pickup.Table("PickupRotation").DataRange.RowCount());
        Assert.Equal(0, pickup.Cell("C5").GetValue<int>());
        Assert.Equal(1, pickup.Cell("C6").GetValue<int>());
    }

    private ReportsController Controller(ClaimsPrincipal principal, string query = "") => new(db, users, Service(), new ExcelReportService(), new TestLocalizer())
    {
        ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = principal, Request = { QueryString = new QueryString(query) } } }
    };

    private sealed class TestLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private ReportQueryService Service() => new(db, users);

    private static ClaimsPrincipal Principal(string userId, string role) => new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId), new Claim(ClaimTypes.Role, role)], "test"));

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
