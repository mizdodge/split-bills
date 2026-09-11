using Microsoft.EntityFrameworkCore;
using Splitbill.Data;

namespace Splitbill.Tests;

public sealed class UserEmailSchemaTests
{
    [Fact]
    public async Task FreshDatabase_StoresBuiltInEmailColumnsOnAspNetUsers()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"splitbill-email-schema-{Guid.NewGuid():N}.db");

        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;

            await using var db = new ApplicationDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseSchemaUpdater.EnsureAsync(db);
            await db.Database.OpenConnectionAsync();

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            await using (var command = db.Database.GetDbConnection().CreateCommand())
            {
                command.CommandText = "PRAGMA table_info(\"AspNetUsers\");";
                await using var reader = await command.ExecuteReaderAsync();
                while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            }

            Assert.Contains("Email", columns);
            Assert.Contains("NormalizedEmail", columns);
            await db.Database.CloseConnectionAsync();
        }
        finally
        {
            File.Delete(databasePath);
        }
    }
}
