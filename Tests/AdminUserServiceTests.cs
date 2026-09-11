using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class AdminUserServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly UserManager<ApplicationUser> users;
    private readonly RoleManager<IdentityRole> roles;
    private readonly AdminUserService service;

    public AdminUserServiceTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        var identityOptions = new IdentityOptions();
        identityOptions.Tokens.ProviderMap["Default"] = new TokenProviderDescriptor(typeof(DataProtectorTokenProvider<ApplicationUser>));
        var identityServiceCollection = new ServiceCollection();
        identityServiceCollection.AddDataProtection();
        identityServiceCollection.AddLogging();
        identityServiceCollection.AddSingleton<DataProtectorTokenProvider<ApplicationUser>>();
        identityServiceCollection.AddSingleton<IUserTwoFactorTokenProvider<ApplicationUser>>(sp =>
            sp.GetRequiredService<DataProtectorTokenProvider<ApplicationUser>>());
        var identityServices = identityServiceCollection.BuildServiceProvider();
        users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser>(db), Options.Create(identityOptions),
            new PasswordHasher<ApplicationUser>(), [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), identityServices,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        roles = new RoleManager<IdentityRole>(new RoleStore<IdentityRole>(db), [], new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(), NullLogger<RoleManager<IdentityRole>>.Instance);
        service = new AdminUserService(db, users, roles);
    }

    [Fact]
    public async Task CreateAddsOneRoleAndSanitizedAudit()
    {
        await SeedRoleAsync(DatabaseSeeder.AdminRole);
        await SeedRoleAsync(DatabaseSeeder.MemberRole);

        var result = await service.CreateAsync("admin", "new-user", "New User", "new@example.com",
            DatabaseSeeder.MemberRole, "password1");

        Assert.True(result.Succeeded, Errors(result));
        var created = await users.FindByNameAsync("new-user");
        Assert.NotNull(created);
        Assert.True(await users.IsInRoleAsync(created!, DatabaseSeeder.MemberRole));
        var audit = await db.AdminUserAuditLogs.SingleAsync();
        Assert.Equal(AdminUserAuditAction.Created, audit.Action);
        Assert.DoesNotContain("password", audit.SummaryJson ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DuplicateEmailIsRejectedCaseInsensitively()
    {
        await SeedRoleAsync(DatabaseSeeder.MemberRole);
        await CreateUserAsync("one", "one@example.com", DatabaseSeeder.MemberRole);

        var result = await service.CreateAsync("admin", "two", "Two", "ONE@example.com",
            DatabaseSeeder.MemberRole, "password1");

        Assert.False(result.Succeeded);
        Assert.Equal("DuplicateEmail", result.ErrorCode);
        Assert.Null(await users.FindByNameAsync("two"));
    }

    [Fact]
    public async Task DisableAndEnableChangesLoginStateAndAudits()
    {
        await SeedRoleAsync(DatabaseSeeder.AdminRole);
        await SeedRoleAsync(DatabaseSeeder.MemberRole);
        var member = await CreateUserAsync("member", "member@example.com", DatabaseSeeder.MemberRole);

        var disabled = await service.SetDisabledAsync("admin", member.Id, true);

        Assert.True(disabled.Succeeded, Errors(disabled));
        Assert.True((await users.FindByIdAsync(member.Id))!.LockoutEnd > DateTimeOffset.UtcNow);
        Assert.Equal(AdminUserAuditAction.Disabled, await db.AdminUserAuditLogs.Select(x => x.Action).SingleAsync());

        var enabled = await service.SetDisabledAsync("admin", member.Id, false);

        Assert.True(enabled.Succeeded, Errors(enabled));
        Assert.Null((await users.FindByIdAsync(member.Id))!.LockoutEnd);
        Assert.Equal(2, await db.AdminUserAuditLogs.CountAsync());
    }

    [Fact]
    public async Task CannotDisableLastAdminOrSelf()
    {
        await SeedRoleAsync(DatabaseSeeder.AdminRole);
        var admin = await CreateUserAsync("admin", "admin@example.com", DatabaseSeeder.AdminRole);

        var self = await service.SetDisabledAsync(admin.Id, admin.Id, true);
        var last = await service.UpdateAsync("another-admin", admin.Id, "Admin", "admin@example.com", DatabaseSeeder.MemberRole);

        Assert.Equal("SelfDisable", self.ErrorCode);
        Assert.Equal("LastAdmin", last.ErrorCode);
    }

    [Fact]
    public async Task PasswordResetReplacesHashWithoutRecordingPassword()
    {
        await SeedRoleAsync(DatabaseSeeder.MemberRole);
        var member = await CreateUserAsync("member", "member@example.com", DatabaseSeeder.MemberRole, "old123");

        var result = await service.ResetPasswordAsync("admin", member.Id, "new123");

        Assert.True(result.Succeeded, Errors(result));
        var refreshed = await users.FindByIdAsync(member.Id);
        Assert.True(await users.CheckPasswordAsync(refreshed!, "new123"));
        Assert.False(await users.CheckPasswordAsync(refreshed!, "old123"));
        var audit = await db.AdminUserAuditLogs.SingleAsync();
        Assert.Equal(AdminUserAuditAction.PasswordReset, audit.Action);
        Assert.Null(audit.SummaryJson);
    }

    [Fact]
    public async Task ListSearchAndStatusFilterMaterializeLockoutInMemory()
    {
        await SeedRoleAsync(DatabaseSeeder.MemberRole);
        var member = await CreateUserAsync("member", "member@example.com", DatabaseSeeder.MemberRole);
        await service.SetDisabledAsync("admin", member.Id, true);

        var disabled = await service.ListAsync(new AdminUserListQuery(Search: "MEMBER", Status: "disabled"));
        var active = await service.ListAsync(new AdminUserListQuery(Status: "active"));

        Assert.Single(disabled);
        Assert.Equal("member", disabled[0].Username);
        Assert.DoesNotContain(active, x => x.Username == "member");
    }

    private async Task<ApplicationUser> CreateUserAsync(string username, string email, string role, string password = "password1")
    {
        var user = new ApplicationUser { UserName = username, Email = email, EmailConfirmed = true, DisplayName = username, LockoutEnabled = true };
        var result = await users.CreateAsync(user, password);
        Assert.True(result.Succeeded, string.Join(", ", result.Errors.Select(x => x.Description)));
        var roleResult = await users.AddToRoleAsync(user, role);
        Assert.True(roleResult.Succeeded, string.Join(", ", roleResult.Errors.Select(x => x.Description)));
        return user;
    }

    private async Task SeedRoleAsync(string name)
    {
        if (!await roles.RoleExistsAsync(name))
            Assert.True((await roles.CreateAsync(new IdentityRole(name))).Succeeded);
    }

    private static string Errors(AdminUserMutationResult result)
        => string.Join(", ", result.Errors.Select(x => x.Description));

    public void Dispose()
    {
        users.Dispose();
        roles.Dispose();
        db.Dispose();
        connection.Dispose();
    }
}
