using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Tests;

public sealed class AdminSchemaTests
{
    [Fact]
    public async Task SchemaUpdaterCreatesSharePointAndPickupTablesAdditively()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);

        await db.Database.EnsureCreatedAsync();
        await DatabaseSchemaUpdater.EnsureAsync(db);
        await DatabaseSchemaUpdater.EnsureAsync(db);

        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table';";
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) tables.Add(reader.GetString(0));

        Assert.Contains("SharePointConfigurations", tables);
        Assert.Contains("SharePointNotificationOutbox", tables);
        Assert.Contains("AdminUserAuditLogs", tables);
        Assert.Contains("AspNetUsers", tables);
        Assert.Contains("FoodPickupConfigurations", tables);
        Assert.Contains("FoodPickupEligibleUsers", tables);
        Assert.Contains("FoodPickupAssignments", tables);
        Assert.Contains("FoodPickupDrawHistories", tables);
        Assert.Contains("MicrosoftIntegrationConfigurations", tables);
        Assert.Contains("MicrosoftLoginConfigurations", tables);
        Assert.Contains("MicrosoftAccountLinkIntents", tables);

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "PRAGMA table_info(\"Transactions\");";
        await using var columnReader = await columnCommand.ExecuteReaderAsync();
        while (await columnReader.ReadAsync()) columns.Add(columnReader.GetString(1));
        Assert.Contains("NotificationBaseUrl", columns);
        Assert.Contains("RequiresFoodPickup", columns);

        foreach (var (table, expectedColumn) in new[]
                 {
                     ("FoodPickupConfigurations", "Strategy"),
                     ("FoodPickupAssignments", "Strategy"),
                     ("FoodPickupDrawHistories", "Strategy")
                 })
        {
            var pickupColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using var pickupCommand = connection.CreateCommand();
            pickupCommand.CommandText = $"PRAGMA table_info(\"{table}\");";
            await using var pickupReader = await pickupCommand.ExecuteReaderAsync();
            while (await pickupReader.ReadAsync()) pickupColumns.Add(pickupReader.GetString(1));
            Assert.Contains(expectedColumn, pickupColumns);
        }
    }

    [Fact]
    public async Task SchemaUpdaterBackfillsTransactionsWithCurrentPickupAssignment()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True");
        await connection.OpenAsync();
        await using var db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection).Options);
        await db.Database.EnsureCreatedAsync();

        var user = new ApplicationUser { Id = "pickup-user", UserName = "pickup-user", DisplayName = "Pickup User" };
        var participant = new TransactionParticipant { Name = "Pickup User", AccountLink = new ParticipantAccountLink { UserId = user.Id } };
        var transaction = new BillTransaction
        {
            TransactionNumber = "TEST-PICKUP-FLAG", MerchantName = "Test", UploadedByUserId = user.Id,
            GrandTotal = 100, Participants = [participant]
        };
        db.Users.Add(user);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        db.FoodPickupAssignments.Add(new FoodPickupAssignment
        {
            TransactionId = transaction.Id, SelectedUserId = user.Id, SelectedParticipantId = participant.Id,
            SelectedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        // EnsureAsync is idempotent and applies the assignment-based backfill.
        await DatabaseSchemaUpdater.EnsureAsync(db);
        db.ChangeTracker.Clear();
        Assert.True(await db.Transactions.Where(x => x.Id == transaction.Id).Select(x => x.RequiresFoodPickup).SingleAsync());
    }
}
