using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using ClosedXML.Excel;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;

namespace Splitbill.Tests;

public sealed class FoodPickupPointsTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private static readonly string[] Members = ["Mizan", "Ragil", "Agi", "Fawwaz", "Iyan", "Lucky", "Rendi", "Wisnu", "Roby", "Afwan"];
    private static readonly string[] Ordering = Members.Take(5).ToArray();
    private FoodPickupPointsService Points => new(db);

    public FoodPickupPointsTests()
    {
        connection.Open(); db = new(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        foreach (var name in Members) { db.Users.Add(new ApplicationUser { Id = name, UserName = name, DisplayName = name }); db.FoodPickupEligibleUsers.Add(new() { UserId = name }); }
        db.FoodPickupConfigurations.Add(new() { Id = 1, Enabled = true, Strategy = FoodPickupSelectionStrategy.Points });
        db.SaveChanges();
    }

    [Fact]
    public async Task FirstAndSecondOrdersPrioritizeHighestAndLeaveNonparticipantsUntouched()
    {
        var first = await Transaction("POINT-1");
        var selected = await Rotation(.65).AssignOrReconcileAsync(first, "Mizan"); await db.SaveChangesAsync();
        Assert.Equal("Mizan", selected.Assignment!.SelectedUserId);
        var balances = await Points.GetBalancesAsync();
        Assert.Equal(0, balances["Mizan"]); Assert.All(Ordering.Skip(1), id => Assert.Equal(1, balances[id]));
        Assert.All(Members.Skip(5), id => Assert.Equal(0, balances.GetValueOrDefault(id)));
        var preview = await Rotation(.9).PreviewAsync(Members);
        Assert.Equal(4, preview.Candidates.Count(x => x.RawWeight > 0));
        Assert.All(preview.Candidates.Where(x => Members.Skip(5).Contains(x.UserId)), x => Assert.Equal(0m, x.Probability));
        var second = await Transaction("POINT-2");
        var next = await Rotation(.9).AssignOrReconcileAsync(second, "Mizan"); await db.SaveChangesAsync();
        Assert.Equal("Ragil", next.Assignment!.SelectedUserId);
        balances = await Points.GetBalancesAsync();
        Assert.Equal(0, balances["Ragil"]); Assert.Equal(1, balances["Mizan"]);
        Assert.All(new[] { "Agi", "Fawwaz", "Iyan" }, id => Assert.Equal(2, balances[id]));
        var history = await Points.HistoryAsync([second.Id]);
        Assert.Equal(4, Assert.Single(history).Snapshot.TiedCandidates);
        Assert.Equal(1, history[0].Snapshot.HighestPoints);
        Assert.Equal(0, Assert.Single(history[0].Snapshot.Changes, x => x.UserId == "Ragil").After);
    }

    [Fact]
    public async Task RepeatSaveIsIdempotentAndRosterEditReplacesContribution()
    {
        var transaction = await Transaction("POINT-IDEMPOTENT"); var rotation = Rotation(.65);
        await rotation.AssignOrReconcileAsync(transaction, "Mizan"); await db.SaveChangesAsync();
        await rotation.AssignOrReconcileAsync(transaction, "Mizan"); await db.SaveChangesAsync();
        Assert.Equal(1, await db.FoodPickupPointAudits.CountAsync());
        Assert.Equal(1, await db.FoodPickupPointRounds.CountAsync());
        transaction.Participants.Remove(transaction.Participants.Single(x => x.Name == "Agi"));
        await rotation.AssignOrReconcileAsync(transaction, "Mizan"); await db.SaveChangesAsync();
        var balances = await Points.GetBalancesAsync(); Assert.Equal(0, balances.GetValueOrDefault("Agi"));
        Assert.Equal(1, balances["Ragil"]); Assert.Equal(0, balances["Mizan"]);
        Assert.Equal(2, await db.FoodPickupPointAudits.CountAsync());
        Assert.Equal(1, await db.FoodPickupPointRounds.CountAsync());
    }

    [Fact]
    public async Task RerollReplacesWinnerWithoutDoubleIncrement()
    {
        var transaction = await Transaction("POINT-REROLL"); var rotation = Rotation(.65);
        await rotation.AssignOrReconcileAsync(transaction, "Mizan"); await db.SaveChangesAsync();
        var reroll = await rotation.RerollAsync(transaction, "Mizan", "Different collector needed"); await db.SaveChangesAsync();
        var balances = await Points.GetBalancesAsync();
        Assert.NotNull(reroll); Assert.NotEqual("Mizan", reroll!.Assignment!.SelectedUserId);
        Assert.Equal(0, balances[reroll.Assignment.SelectedUserId]);
        Assert.All(Ordering.Where(x => x != reroll.Assignment.SelectedUserId), id => Assert.Equal(1, balances[id]));
        Assert.Equal(1, await db.FoodPickupPointRounds.CountAsync()); Assert.Equal(2, await db.FoodPickupPointAudits.CountAsync());
    }

    [Fact]
    public async Task DeleteOlderRoundReplaysLaterRoundAndRetainsAuditWithoutRedrawing()
    {
        var first = await Transaction("POINT-DELETE-1"); await Rotation(.65).AssignOrReconcileAsync(first, "Mizan"); await db.SaveChangesAsync();
        var second = await Transaction("POINT-DELETE-2"); await Rotation(.9).AssignOrReconcileAsync(second, "Mizan"); await db.SaveChangesAsync();
        await Points.ReconcileAsync(first, null, "Mizan", removalKind: "TransactionDeleted");
        db.Transactions.Remove(first); await db.SaveChangesAsync();
        var balances = await Points.GetBalancesAsync();
        Assert.Equal(0, balances["Ragil"]); Assert.Equal(1, balances["Agi"]); Assert.Equal(1, balances["Mizan"]);
        Assert.Equal("Ragil", (await db.FoodPickupAssignments.SingleAsync()).SelectedUserId);
        Assert.Contains(await Points.HistoryAsync(), x => x.Kind == "TransactionDeleted" && x.TransactionId is null);
    }

    [Fact]
    public async Task OtherStrategiesDoNotChangePointsAndSwitchingBackRestoresThem()
    {
        var first = await Transaction("POINT-SEPARATE"); await Rotation(.65).AssignOrReconcileAsync(first, "Mizan"); await db.SaveChangesAsync();
        var config = await db.FoodPickupConfigurations.SingleAsync(); config.Strategy = FoodPickupSelectionStrategy.WeightedRandom; await db.SaveChangesAsync();
        var weighted = await Transaction("WEIGHTED"); await Rotation(.1).AssignOrReconcileAsync(weighted, "Mizan"); await db.SaveChangesAsync();
        Assert.Equal(1, await db.FoodPickupPointRounds.CountAsync());
        Assert.Equal(1, (await Points.GetBalancesAsync())["Ragil"]);
        config.Strategy = FoodPickupSelectionStrategy.Points; await db.SaveChangesAsync();
        var stats = await Rotation().GetStatisticsAsync();
        Assert.Equal(1, stats.Users.Single(x => x.UserId == "Ragil").PriorityPoints);
        Assert.Single(stats.History);
    }

    [Fact]
    public async Task GuestsIneligibleAndAbsentAccountsNeverGainPoints()
    {
        db.FoodPickupEligibleUsers.Remove(await db.FoodPickupEligibleUsers.SingleAsync(x => x.UserId == "Iyan")); await db.SaveChangesAsync();
        var tx = await Transaction("POINT-ELIGIBLE"); tx.Participants.Add(new() { Name = "Guest", Amount = 1000 }); await db.SaveChangesAsync();
        await Rotation(.65).AssignOrReconcileAsync(tx, "Mizan"); await db.SaveChangesAsync();
        var balances = await Points.GetBalancesAsync();
        Assert.False(balances.ContainsKey("Iyan")); Assert.False(balances.ContainsKey("Guest")); Assert.False(balances.ContainsKey("Lucky"));
    }

    [Fact]
    public async Task ClearingPickupUndoesRoundOnceAndSchemaUpgradeIsIdempotent()
    {
        await DatabaseSchemaUpdater.EnsureAsync(db); await DatabaseSchemaUpdater.EnsureAsync(db);
        var tx = await Transaction("POINT-CLEAR"); await Rotation(.65).AssignOrReconcileAsync(tx, "Mizan"); await db.SaveChangesAsync();
        await Points.ReconcileAsync(tx, null, "Mizan"); await db.SaveChangesAsync();
        await Points.ReconcileAsync(tx, null, "Mizan"); await db.SaveChangesAsync();
        Assert.Empty(await Points.GetBalancesAsync()); Assert.Equal(2, await db.FoodPickupPointAudits.CountAsync());
    }

    [Fact]
    public async Task LegacyDatabaseAddsPointTablesWithoutResettingExistingConfiguration()
    {
        var config = await db.FoodPickupConfigurations.SingleAsync();
        config.Strategy = FoodPickupSelectionStrategy.RoundRobin;
        await db.SaveChangesAsync();
        // This connection is the isolated in-memory test database only.
        await db.Database.ExecuteSqlRawAsync("DROP TABLE FoodPickupPointAudits");
        await db.Database.ExecuteSqlRawAsync("DROP TABLE FoodPickupPointRounds");
        await DatabaseSchemaUpdater.EnsureAsync(db);
        await DatabaseSchemaUpdater.EnsureAsync(db);
        Assert.Empty(await db.FoodPickupPointRounds.ToListAsync());
        Assert.Empty(await db.FoodPickupPointAudits.ToListAsync());
        Assert.Equal(FoodPickupSelectionStrategy.RoundRobin, (await db.FoodPickupConfigurations.SingleAsync()).Strategy);
        Assert.Equal(10, await db.Users.CountAsync());
        Assert.Equal(0, (int)FoodPickupSelectionStrategy.WeightedRandom);
        Assert.Equal(1, (int)FoodPickupSelectionStrategy.RoundRobin);
        Assert.Equal(2, (int)FoodPickupSelectionStrategy.Points);
    }

    [Fact]
    public async Task ScopedReportsAndExcelExposePointsAndAuditWithoutLeakingOtherBills()
    {
        var tx = await Transaction("POINT-REPORT");
        await Rotation(.65).AssignOrReconcileAsync(tx, "Mizan"); await db.SaveChangesAsync();
        using var users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!, NullLogger<UserManager<ApplicationUser>>.Instance);
        var query = new ReportQueryService(db, users);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "Ragil"), new Claim(ClaimTypes.Role, DatabaseSeeder.MemberRole)], "test"));
        var result = await query.QueryAsync(principal, null, null, null, null);
        Assert.Single(result.PointHistory); Assert.Equal(1, result.Participants.Single(x => x.AccountUserId == "Ragil").CurrentPickupPoints);
        result.PickupStrategy = FoodPickupSelectionStrategy.Points;
        using var workbook = new XLWorkbook(new MemoryStream(new ExcelReportService().Build(result, new System.Globalization.CultureInfo("en-US"))));
        Assert.Equal(5, workbook.Worksheets.Count);
        Assert.Equal("Points System", workbook.Worksheet("Pickup Rotation").Cell("B2").GetString());
        Assert.Equal(5, workbook.Worksheet("Points History").Table("PickupPointHistory").DataRange.RowCount());
        Assert.Equal(0, workbook.Worksheet("Points History").Cell("F5").GetValue<int>());
        Assert.Equal(1, workbook.Worksheet("Points History").Cell("G5").GetValue<int>());
        Assert.Contains("priority candidates", workbook.Worksheet("Points History").Cell("K5").GetString());
        var outsider = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "Lucky"), new Claim(ClaimTypes.Role, DatabaseSeeder.MemberRole)], "test"));
        var denied = await query.QueryAsync(outsider, null, null, null, null);
        Assert.Empty(denied.Transactions); Assert.Empty(denied.PointHistory);
    }

    private FoodPickupRotationService Rotation(double value = .5) => new(db, new Fixed(value));
    private sealed class Fixed(double value) : IFoodPickupRandomSource { public double NextUnitInterval() => value; }
    private async Task<BillTransaction> Transaction(string number)
    {
        var tx = new BillTransaction { TransactionNumber = number, MerchantName = "Cafe", UploadedByUserId = "Mizan", Status = TransactionStatus.Unpaid, RequiresFoodPickup = true,
            Participants = Ordering.Select(id => new TransactionParticipant { Name = id, Amount = 1000, AccountLink = new ParticipantAccountLink { UserId = id } }).ToList() };
        db.Transactions.Add(tx); await db.SaveChangesAsync(); return tx;
    }
    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
