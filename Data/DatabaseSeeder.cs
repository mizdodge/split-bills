using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Data;

public static class DatabaseSeeder
{
    public const string AdminRole = "Admin";
    public const string ModeratorRole = "Moderator";
    public const string MemberRole = "Member";

    public static async Task SeedAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await db.Database.EnsureCreatedAsync();
        await DatabaseSchemaUpdater.EnsureAsync(db);
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "TransactionCharges" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TransactionCharges" PRIMARY KEY AUTOINCREMENT,
                "TransactionId" INTEGER NOT NULL,
                "Label" TEXT NOT NULL,
                "Amount" TEXT NOT NULL,
                "Operation" INTEGER NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                CONSTRAINT "FK_TransactionCharges_Transactions_TransactionId"
                    FOREIGN KEY ("TransactionId") REFERENCES "Transactions" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_TransactionCharges_TransactionId"
                ON "TransactionCharges" ("TransactionId");
            CREATE TABLE IF NOT EXISTS "TransactionReceiptImages" (
                "Id" INTEGER NOT NULL CONSTRAINT "PK_TransactionReceiptImages" PRIMARY KEY AUTOINCREMENT,
                "TransactionId" INTEGER NOT NULL,
                "FileName" TEXT NOT NULL,
                "ContentType" TEXT NOT NULL,
                "SortOrder" INTEGER NOT NULL,
                CONSTRAINT "FK_TransactionReceiptImages_Transactions_TransactionId"
                    FOREIGN KEY ("TransactionId") REFERENCES "Transactions" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_TransactionReceiptImages_TransactionId"
                ON "TransactionReceiptImages" ("TransactionId");
            """);

        // Create the installation's stable VAPID key pair once. The private
        // key is protected by the persistent Data Protection key ring.
        await scope.ServiceProvider.GetRequiredService<IWebPushKeyService>()
            .EnsureAsync();

        // One-time compatibility migration: move the former OpenAI file setting into the encrypted database field.
        var legacyOpenAiKey = scope.ServiceProvider.GetRequiredService<IConfiguration>()["OpenAI:ApiKey"];
        if (!string.IsNullOrWhiteSpace(legacyOpenAiKey))
        {
            var openAiSettings = await db.AiConfigurations.SingleOrDefaultAsync(x => x.IsActive && x.Provider == AiProvider.OpenAi);
            if (openAiSettings is not null && string.IsNullOrWhiteSpace(openAiSettings.ProtectedApiKey))
            {
                var protector = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>()
                    .CreateProtector(AiApiKeyProtection.Purpose);
                openAiSettings.ProtectedApiKey = protector.Protect(legacyOpenAiKey.Trim());
                openAiSettings.UpdatedAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync();
            }
        }

        var roles = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();

        foreach (var role in new[] { AdminRole, ModeratorRole, MemberRole })
        {
            if (!await roles.RoleExistsAsync(role))
            {
                await roles.CreateAsync(new IdentityRole(role));
            }
        }
        // Fresh installations intentionally contain roles only. The first
        // administrator is created through the one-time /Setup flow. Existing
        // installations keep every existing account untouched.
        await scope.ServiceProvider.GetRequiredService<IInstallationSetupService>()
            .EnsureStateAsync(db);
    }
}
