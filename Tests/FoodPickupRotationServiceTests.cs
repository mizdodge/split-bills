using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class FoodPickupRotationServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;

    public FoodPickupRotationServiceTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Users.AddRange(
            User("owner", "Owner"), User("mizan", "Mizan"), User("fawwaz", "Fawwaz"),
            User("agi", "Agi"), User("ragil", "Ragil"), User("disabled", "Disabled", DateTimeOffset.UtcNow.AddHours(1)));
        db.FoodPickupConfigurations.Add(new FoodPickupConfiguration { Id = 1, Enabled = true, UpdatedAt = DateTimeOffset.UtcNow });
        db.FoodPickupEligibleUsers.AddRange(
            Eligible("mizan"), Eligible("fawwaz"), Eligible("agi"), Eligible("ragil"), Eligible("disabled"));
        db.SaveChanges();
    }

    [Fact]
    public async Task PreviewUsesOnlyEligibleActiveUsersWhoAreParticipants()
    {
        var preview = await Service().PreviewAsync(["mizan", "fawwaz", "agi"]);

        Assert.Equal(["Agi", "Fawwaz", "Mizan"], preview.Candidates.Select(x => x.DisplayName));
        Assert.DoesNotContain(preview.Candidates, x => x.DisplayName == "Ragil");
        Assert.DoesNotContain(preview.Candidates, x => x.DisplayName == "Disabled");
        Assert.InRange(preview.Candidates.Sum(x => x.Probability), 0.999999m, 1.000001m);
    }

    [Fact]
    public async Task PriorCurrentAssignmentsReduceFutureProbability()
    {
        await AddAssignedTransactionAsync("COUNT-1", "mizan", 1);
        await AddAssignedTransactionAsync("COUNT-2", "fawwaz", 2);
        await AddAssignedTransactionAsync("COUNT-3", "fawwaz", 3);
        await AddAssignedTransactionAsync("COUNT-4", "agi", 4);

        var preview = await Service().PreviewAsync(["mizan", "fawwaz", "agi"]);
        var mizan = Assert.Single(preview.Candidates, x => x.UserId == "mizan");
        var fawwaz = Assert.Single(preview.Candidates, x => x.UserId == "fawwaz");
        var agi = Assert.Single(preview.Candidates, x => x.UserId == "agi");

        Assert.Equal(1, mizan.PriorPickupCount);
        Assert.Equal(2, fawwaz.PriorPickupCount);
        Assert.Equal(1, agi.PriorPickupCount);
        Assert.True(fawwaz.Probability < mizan.Probability);
        Assert.Equal(0.5m, mizan.RawWeight);
    }

    [Fact]
    public async Task InitialAssignmentIsStableWhenCalledAgain()
    {
        var transaction = await AddParticipantTransactionAsync("STABLE", ["mizan", "fawwaz"]);
        var service = Service(0d);

        var first = await service.AssignOrReconcileAsync(transaction, "owner");
        await db.SaveChangesAsync();
        var selected = first.Assignment!.SelectedUserId;
        var second = await service.AssignOrReconcileAsync(transaction, "owner");
        await db.SaveChangesAsync();

        Assert.Equal(selected, second.Assignment!.SelectedUserId);
        Assert.Equal(1, await db.FoodPickupAssignments.CountAsync(x => x.TransactionId == transaction.Id));
        Assert.Equal(1, await db.FoodPickupDrawHistories.CountAsync(x => x.TransactionId == transaction.Id));
    }

    [Fact]
    public async Task ZeroAndOneCandidateBehaveSafely()
    {
        var noCandidate = await AddParticipantTransactionAsync("NONE", ["owner"]);
        var none = await Service().AssignOrReconcileAsync(noCandidate, "owner");
        Assert.Null(none.Assignment);

        var oneCandidate = await AddParticipantTransactionAsync("ONE", ["ragil"]);
        var one = await Service(0.9d).AssignOrReconcileAsync(oneCandidate, "owner");
        Assert.Equal("ragil", one.Assignment!.SelectedUserId);
        Assert.Equal(1m, one.Assignment.RecordedProbability);
    }

    [Fact]
    public async Task RerollExcludesCurrentWinnerAndRequiresAnotherCandidate()
    {
        var transaction = await AddParticipantTransactionAsync("REROLL", ["mizan", "fawwaz"]);
        var service = Service(0d);
        var initial = await service.AssignOrReconcileAsync(transaction, "owner");
        await db.SaveChangesAsync();
        var initialUserId = initial.Assignment!.SelectedUserId;

        var rerolled = await service.RerollAsync(transaction, "owner", "Admin requested a new pickup person.");
        await db.SaveChangesAsync();

        Assert.NotNull(rerolled);
        Assert.NotEqual(initialUserId, rerolled!.Assignment!.SelectedUserId);
        Assert.Equal(FoodPickupDrawKind.AdminReroll, rerolled.History!.DrawKind);
        Assert.Equal("Admin requested a new pickup person.", rerolled.History.Reason);
        Assert.Equal(2, await db.FoodPickupDrawHistories.CountAsync(x => x.TransactionId == transaction.Id));
    }

    [Fact]
    public async Task RerollWithOneCandidateIsNoOp()
    {
        var transaction = await AddParticipantTransactionAsync("REROLL-ONE", ["mizan"]);
        var service = Service(0d);
        await service.AssignOrReconcileAsync(transaction, "owner");
        await db.SaveChangesAsync();

        Assert.Null(await service.RerollAsync(transaction, "owner", "Try again"));
        Assert.Equal(1, await db.FoodPickupDrawHistories.CountAsync(x => x.TransactionId == transaction.Id));
    }

    [Fact]
    public async Task StatisticsLoadsAndOrdersDateTimeOffsetHistoryWithSqlite()
    {
        var olderTransaction = await AddParticipantTransactionAsync("HISTORY-OLD", ["mizan"]);
        var newerTransaction = await AddParticipantTransactionAsync("HISTORY-NEW", ["fawwaz"]);
        var service = Service(0d);
        var older = await service.AssignOrReconcileAsync(olderTransaction, "owner");
        var newer = await service.AssignOrReconcileAsync(newerTransaction, "owner");
        older.History!.CreatedAt = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        newer.History!.CreatedAt = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();

        var statistics = await service.GetStatisticsAsync();

        Assert.Equal(["HISTORY-NEW", "HISTORY-OLD"], statistics.History.Select(x => x.TransactionNumber));
    }

    [Fact]
    public async Task RoundRobinSelectsNeverPickedThenLongestWaitingCandidate()
    {
        var configuration = await db.FoodPickupConfigurations.SingleAsync();
        configuration.Strategy = FoodPickupSelectionStrategy.RoundRobin;
        await AddAssignedTransactionAsync("ROUND-MIZAN", "mizan", 1);
        await AddAssignedTransactionAsync("ROUND-FAWWAZ", "fawwaz", 2);
        var mizanAssignment = await db.FoodPickupAssignments.SingleAsync(x => x.SelectedUserId == "mizan");
        var fawwazAssignment = await db.FoodPickupAssignments.SingleAsync(x => x.SelectedUserId == "fawwaz");
        mizanAssignment.SelectedAt = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.Zero);
        fawwazAssignment.SelectedAt = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        await db.SaveChangesAsync();

        var firstTransaction = await AddParticipantTransactionAsync("ROUND-FIRST", ["mizan", "fawwaz", "agi"]);
        var preview = await Service(0.99d).PreviewAsync(["mizan", "fawwaz", "agi"]);
        var first = await Service(0.99d).AssignOrReconcileAsync(firstTransaction, "owner");
        await db.SaveChangesAsync();

        Assert.Equal(FoodPickupSelectionStrategy.RoundRobin, preview.Strategy);
        Assert.Equal("agi", Assert.Single(preview.Candidates, x => x.Probability == 1m).UserId);
        Assert.Equal("agi", first.Assignment!.SelectedUserId);
        Assert.Equal(1m, first.Assignment.RecordedProbability);
        Assert.Equal(FoodPickupSelectionStrategy.RoundRobin, first.Assignment.Strategy);
        Assert.Equal(FoodPickupSelectionStrategy.RoundRobin, first.History!.Strategy);

        var secondTransaction = await AddParticipantTransactionAsync("ROUND-SECOND", ["mizan", "fawwaz", "agi"]);
        var second = await Service(0d).AssignOrReconcileAsync(secondTransaction, "owner");

        Assert.Equal("fawwaz", second.Assignment!.SelectedUserId);
    }

    [Fact]
    public async Task RoundRobinSkipsEligibleAccountsMissingFromTransaction()
    {
        var configuration = await db.FoodPickupConfigurations.SingleAsync();
        configuration.Strategy = FoodPickupSelectionStrategy.RoundRobin;
        await db.SaveChangesAsync();
        var transaction = await AddParticipantTransactionAsync("ROUND-SKIP", ["mizan", "agi"]);

        var result = await Service().AssignOrReconcileAsync(transaction, "owner");

        Assert.Contains(result.Assignment!.SelectedUserId, new[] { "mizan", "agi" });
        Assert.NotEqual("fawwaz", result.Assignment.SelectedUserId);
        Assert.NotEqual("ragil", result.Assignment.SelectedUserId);
    }

    private FoodPickupRotationService Service(double roll = 0.25d)
        => new(db, new FixedRandomSource(roll));

    private async Task<BillTransaction> AddParticipantTransactionAsync(string number, IReadOnlyList<string> userIds)
    {
        var transaction = new BillTransaction
        {
            TransactionNumber = number, MerchantName = "Cafe", UploadedByUserId = "owner",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            Participants = userIds.Select(userId => new TransactionParticipant
            {
                Name = userId, Amount = 1_000,
                AccountLink = new ParticipantAccountLink { UserId = userId, LinkedAt = DateTimeOffset.UtcNow }
            }).ToList()
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        return transaction;
    }

    private async Task AddAssignedTransactionAsync(string number, string userId, int id)
    {
        var transaction = await AddParticipantTransactionAsync(number, [userId]);
        db.FoodPickupAssignments.Add(new FoodPickupAssignment
        {
            TransactionId = transaction.Id, SelectedUserId = userId,
            SelectedParticipantId = transaction.Participants[0].Id,
            RecordedProbability = 1m, DrawKind = FoodPickupDrawKind.Initial,
            SelectedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private static ApplicationUser User(string id, string displayName, DateTimeOffset? lockoutEnd = null)
        => new() { Id = id, UserName = id, NormalizedUserName = id.ToUpperInvariant(), DisplayName = displayName, LockoutEnd = lockoutEnd };

    private static FoodPickupEligibleUser Eligible(string userId)
        => new() { UserId = userId, EnabledAt = DateTimeOffset.UtcNow, EnabledByUserId = "owner" };

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private sealed class FixedRandomSource(double value) : IFoodPickupRandomSource
    {
        public double NextUnitInterval() => value;
    }
}
