using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;

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

        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var columnCommand = connection.CreateCommand();
        columnCommand.CommandText = "PRAGMA table_info(\"Transactions\");";
        await using var columnReader = await columnCommand.ExecuteReaderAsync();
        while (await columnReader.ReadAsync()) columns.Add(columnReader.GetString(1));
        Assert.Contains("NotificationBaseUrl", columns);

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
}
