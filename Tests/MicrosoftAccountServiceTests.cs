using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Xunit;

namespace Splitbill.Tests;
public sealed class MicrosoftAccountServiceTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;
    private ApplicationDbContext Db => scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    private UserManager<ApplicationUser> Users => scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    private IMicrosoftAccountService Service => scope.ServiceProvider.GetRequiredService<IMicrosoftAccountService>();
    private static MicrosoftIdentity Identity(string email = "ragil@example.com") => new(Tenant, "22222222-2222-2222-2222-222222222222", email, "Ragil");

    public MicrosoftAccountServiceTests()
    {
        connection.Open();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(o => o.UseSqlite(connection));
        services.AddIdentityCore<ApplicationUser>().AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddScoped<IMicrosoftAccountService, MicrosoftAccountService>();
        provider = services.BuildServiceProvider(); scope = provider.CreateScope();
        Db.Database.EnsureCreated();
        Db.MicrosoftIntegrationConfigurations.Add(new() { Id = 1, TenantId = Tenant, CredentialRevision = 1 });
        Db.MicrosoftLoginConfigurations.Add(new() { Id = 1, Enabled = true });
        Db.Roles.Add(new IdentityRole(DatabaseSeeder.MemberRole) { NormalizedName = DatabaseSeeder.MemberRole.ToUpperInvariant() });
        Db.SaveChanges();
    }
    private async Task<ApplicationUser> Local(string name = "ragil", string email = "ragil@example.com")
    {
        var user = new ApplicationUser { UserName = name, Email = email, DisplayName = name };
        Assert.True((await Users.CreateAsync(user, "Local123!" )).Succeeded);
        return user;
    }
    private async Task EnableRegistration()
    {
        (await Db.MicrosoftLoginConfigurations.SingleAsync()).AllowAutoRegistration = true;
        await Db.SaveChangesAsync();
    }
    [Fact]
    public async Task ExistingEmailAndUsernameNeverAutoLink()
    {
        var user = await Local();
        var result = await Service.SignInAsync(Identity());
        Assert.Equal("MicrosoftLinkRequired", result.Error);
        Assert.Null(user.MicrosoftSubject);
        await EnableRegistration();
        Assert.Equal("MicrosoftAccountCollision", (await Service.SignInAsync(Identity())).Error);
        Assert.Equal(1, await Db.Users.CountAsync());
    }
    [Fact]
    public async Task NewAccountIsPasswordlessMemberAndReusesStableIdentity()
    {
        await EnableRegistration();
        var result = await Service.SignInAsync(Identity());
        Assert.Null(result.Error); Assert.NotNull(result.User);
        Assert.Equal("ragil", result.User.UserName);
        Assert.Equal("ragil@example.com", result.User.Email);
        Assert.False(await Users.HasPasswordAsync(result.User));
        Assert.Equal(new[] { DatabaseSeeder.MemberRole }, await Users.GetRolesAsync(result.User));
        var again = await Service.SignInAsync(Identity("changed@example.com"));
        Assert.Equal(result.User.Id, again.User!.Id);
        Assert.Equal(1, await Db.Users.CountAsync());
        Assert.Equal("MicrosoftLocalPasswordRequired", await Service.UnlinkAsync(result.User));
    }
    [Fact]
    public async Task UsernameCollisionDoesNotAddSuffixOrTakeOver()
    {
        await Local("ragil", "different@example.com"); await EnableRegistration();
        Assert.Equal("MicrosoftAccountCollision", (await Service.SignInAsync(Identity())).Error);
        Assert.Equal(1, await Db.Users.CountAsync());
    }
    [Fact]
    public async Task RecoveryAdminCannotLinkOrRegister()
    {
        var admin = await Local("admin");
        Assert.Null(await Service.BeginLinkAsync(admin, "browser"));
        await EnableRegistration();
        Assert.Equal("MicrosoftAccountCollision", (await Service.SignInAsync(Identity("admin@example.com"))).Error);
    }
    [Fact]
    public async Task ExplicitLinkKeepsUserAndIsSingleUse()
    {
        var user = await Local(); var id = await Service.BeginLinkAsync(user, "browser");
        Assert.NotNull(id);
        Assert.True(await Service.ReceiveAsync(id, user, "browser", Identity()));
        Db.ChangeTracker.Clear(); user = (await Users.FindByIdAsync(user.Id))!;
        Assert.NotNull(await Service.PreviewAsync(id, user, "browser"));
        Assert.Null(await Service.ConfirmAsync(id, user, "browser"));
        Assert.Equal(user.Id, (await Service.SignInAsync(Identity())).User!.Id);
        Assert.Equal("MicrosoftLinkExpired", await Service.ConfirmAsync(id, user, "browser"));
        Assert.Equal(1, await Db.Users.CountAsync());
        Assert.True(await Users.HasPasswordAsync(user));
    }
    [Fact]
    public async Task WrongBrowserExpiredOrChangedCredentialsRejectIntent()
    {
        var user = await Local(); var id = (await Service.BeginLinkAsync(user, "browser"))!;
        Assert.False(await Service.ReceiveAsync(id, user, "other-browser", Identity()));
        (await Db.MicrosoftIntegrationConfigurations.SingleAsync()).CredentialRevision++;
        await Db.SaveChangesAsync();
        Assert.False(await Service.ReceiveAsync(id, user, "browser", Identity()));
        var second = (await Service.BeginLinkAsync(user, "browser"))!;
        (await Db.MicrosoftAccountLinkIntents.SingleAsync(x => x.Id == second)).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await Db.SaveChangesAsync();
        Assert.False(await Service.ReceiveAsync(second, user, "browser", Identity()));
    }
    [Fact]
    public async Task IdentityOwnedByAnotherAccountCannotBeLinked()
    {
        await EnableRegistration(); var owner = (await Service.SignInAsync(Identity())).User!;
        var other = await Local("another", "another@example.com");
        var id = (await Service.BeginLinkAsync(other, "browser"))!;
        Assert.True(await Service.ReceiveAsync(id, other, "browser", Identity()));
        Db.ChangeTracker.Clear(); other = (await Users.FindByIdAsync(other.Id))!;
        Assert.Equal("MicrosoftAccountCollision", await Service.ConfirmAsync(id, other, "browser"));
        Assert.Equal(owner.Id, (await Service.SignInAsync(Identity())).User!.Id);
    }
    [Fact]
    public async Task RevokedIdentityCannotReturnThroughRegistration()
    {
        var user = await Local(); var id = (await Service.BeginLinkAsync(user, "browser"))!;
        await Service.ReceiveAsync(id, user, "browser", Identity());
        Db.ChangeTracker.Clear(); user = (await Users.FindByIdAsync(user.Id))!;
        Assert.Null(await Service.ConfirmAsync(id, user, "browser"));
        Assert.Null(await Service.UnlinkAsync(user)); await EnableRegistration();
        Assert.Equal("MicrosoftAccountUnavailable", (await Service.SignInAsync(Identity())).Error);
    }
    [Fact]
    public void IdentityRequiresAuthenticatedExactTenantAndObjectId()
    {
        var claims = new[] { new Claim("tid", Tenant), new Claim("oid", Identity().ObjectId), new Claim("email", "ragil@example.com") };
        Assert.Null(MicrosoftIdentity.Read(new ClaimsPrincipal(new ClaimsIdentity(claims)), Tenant));
        Assert.NotNull(MicrosoftIdentity.Read(new ClaimsPrincipal(new ClaimsIdentity(claims,"oidc")), Tenant));
        Assert.Null(MicrosoftIdentity.Read(new ClaimsPrincipal(new ClaimsIdentity(claims,"oidc")), Guid.NewGuid().ToString()));
    }
    [Fact]
    public async Task DisabledSsoAndWrongTenantCannotProvision()
    {
        await EnableRegistration();
        Assert.Equal("MicrosoftUnavailable", (await Service.SignInAsync(Identity() with { TenantId = Guid.NewGuid().ToString() })).Error);
        (await Db.MicrosoftLoginConfigurations.SingleAsync()).Enabled = false; await Db.SaveChangesAsync();
        Assert.Equal("MicrosoftUnavailable", (await Service.SignInAsync(Identity())).Error);
        Assert.Empty(await Db.Users.ToListAsync());
    }
    [Fact]
    public async Task EmailCollisionIgnoresCaseEvenWithDifferentUsername()
    {
        await Local("existing", "RAGIL@EXAMPLE.COM"); await EnableRegistration();
        Assert.Equal("MicrosoftAccountCollision", (await Service.SignInAsync(Identity())).Error);
    }
    [Fact]
    public async Task PasswordChangeInvalidatesPendingLink()
    {
        var user = await Local(); var id = (await Service.BeginLinkAsync(user, "browser"))!;
        Assert.True((await Users.UpdateSecurityStampAsync(user)).Succeeded);
        Assert.False(await Service.ReceiveAsync(id, user, "browser", Identity()));
    }
    [Fact]
    public async Task CancelledIntentCannotBeConfirmed()
    {
        var user = await Local(); var id = (await Service.BeginLinkAsync(user, "browser"))!;
        await Service.ReceiveAsync(id, user, "browser", Identity());
        Db.ChangeTracker.Clear(); user = (await Users.FindByIdAsync(user.Id))!;
        await Service.CancelAsync(id, user, "browser");
        Db.ChangeTracker.Clear(); user = (await Users.FindByIdAsync(user.Id))!;
        Assert.Equal("MicrosoftLinkExpired", await Service.ConfirmAsync(id, user, "browser"));
        Assert.Null(user.MicrosoftSubject);
    }
    [Fact]
    public async Task DisabledLocalAccountCannotBeginLink()
    {
        var user = await Local();
        await Users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Null(await Service.BeginLinkAsync(user, "browser"));
    }
    [Fact]
    public async Task SchemaUpgradeAddsDefaultOffRegistrationWithoutDataLoss()
    {
        var user = await Local();
        await Db.Database.ExecuteSqlRawAsync("ALTER TABLE MicrosoftLoginConfigurations DROP COLUMN AllowAutoRegistration");
        await DatabaseSchemaUpdater.EnsureAsync(Db);
        await DatabaseSchemaUpdater.EnsureAsync(Db);
        Db.ChangeTracker.Clear();
        Assert.False((await Db.MicrosoftLoginConfigurations.SingleAsync()).AllowAutoRegistration);
        Assert.Equal(user.Id, (await Db.Users.SingleAsync()).Id);
    }
    public void Dispose() { scope.Dispose(); provider.Dispose(); connection.Dispose(); }
}
