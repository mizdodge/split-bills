using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class AccountSelfServiceTests : IDisposable
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string Subject = "22222222-2222-2222-2222-222222222222";
    private const string Binding = "test-browser-binding-long-enough-for-request";
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ServiceProvider provider;
    private readonly IServiceScope scope;
    private ApplicationDbContext Db => scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    private UserManager<ApplicationUser> Users => scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
    private AccountSelfService Service => new(Db, Users);
    public AccountSelfServiceTests()
    {
        connection.Open(); var services = new ServiceCollection(); services.AddLogging();
        services.AddDbContext<ApplicationDbContext>(x => x.UseSqlite(connection));
        services.AddIdentityCore<ApplicationUser>(x => { x.Password.RequireUppercase = false; x.Password.RequireNonAlphanumeric = false; })
            .AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
        services.AddSingleton<IAuthenticationService, TestAuthentication>();
        provider = services.BuildServiceProvider(); scope = provider.CreateScope(); Db.Database.EnsureCreated();
        Db.MicrosoftIntegrationConfigurations.Add(new() { Id = 1, TenantId = Tenant, ProtectedClientSecret = "test-protected-value", CredentialRevision = 1 });
        Db.MicrosoftLoginConfigurations.Add(new() { Id = 1, Enabled = true }); Db.SaveChanges();
    }
    private async Task<ApplicationUser> User(bool password = false, string name = "member")
    {
        var user = new ApplicationUser { UserName = name, DisplayName = "Original", Email = name + "@example.test", MicrosoftTenantId = Tenant,
            MicrosoftSubject = name == "member" ? Subject : Guid.NewGuid().ToString(), MicrosoftLinkVersion = 1, MicrosoftCredentialRevision = 1 };
        var result = password ? await Users.CreateAsync(user, "Original123!") : await Users.CreateAsync(user);
        Assert.True(result.Succeeded); return user;
    }
    private static MicrosoftIdentity Identity(ApplicationUser user) => new(user.MicrosoftTenantId!, user.MicrosoftSubject!, user.Email, user.DisplayName);
    private async Task<string> Verified(ApplicationUser user, string purpose = AccountSelfService.PasswordPurpose)
    {
        var id = await Service.BeginVerificationAsync(user, Binding, purpose); Assert.NotNull(id);
        Assert.True(await Service.VerifyAsync(id!, user, Binding, Identity(user), DateTimeOffset.UtcNow)); return id!;
    }

    [Fact]
    public async Task ProfileUpdatesDisplayNameOnlyAndAuditsWithoutSecrets()
    {
        var user = await User(); var id = user.Id; var subject = user.MicrosoftSubject;
        Assert.True((await Service.SaveProfileAsync(user, "  New name <test>  ")).Succeeded);
        Assert.Equal("New name <test>", user.DisplayName); Assert.Equal("member", user.UserName);
        Assert.Equal("member@example.test", user.Email); Assert.Equal(id, user.Id); Assert.Equal(subject, user.MicrosoftSubject);
        var audit = await Db.AdminUserAuditLogs.SingleAsync(); Assert.Equal(AdminUserAuditAction.SelfProfileUpdated, audit.Action);
        Assert.DoesNotContain("Original123", audit.SummaryJson);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc\nxyz")]
    public async Task InvalidNamesAreRejected(string name)
    {
        var user = await User(); Assert.False((await Service.SaveProfileAsync(user, name)).Succeeded);
        Assert.Equal("Original", user.DisplayName); Assert.Empty(await Db.AdminUserAuditLogs.ToListAsync());
    }

    [Fact]
    public async Task PreferencesPersistAndCancelOnlyOwnersQueuedMessages()
    {
        var user = await User(); var other = await User(name: "other");
        var sub = new WebPushSubscription { UserId = user.Id, EndpointHash = "test", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) };
        var note = new UserNotification { UserId = user.Id, Type = NotificationType.PaymentSubmitted };
        Db.WebPushSubscriptions.Add(sub); Db.UserNotifications.Add(note); await Db.SaveChangesAsync();
        Db.WebPushDeliveries.Add(new() { UserNotificationId = note.Id, WebPushSubscriptionId = sub.Id, Status = WebPushDeliveryStatus.Pending });
        Db.SharePointNotificationOutbox.AddRange(new SharePointNotificationOutbox { EventId = "owner", RecipientUserId = user.Id, RecipientEmail = user.Email! },
            new SharePointNotificationOutbox { EventId = "other", RecipientUserId = other.Id, RecipientEmail = other.Email! });
        await Db.SaveChangesAsync();
        Assert.True((await Service.SavePreferencesAsync(user, "en-US", false, false)).Succeeded);
        Assert.Equal("en-US", user.PreferredCulture); Assert.False(user.TeamsNotificationsEnabled); Assert.False(user.BrowserNotificationsEnabled);
        Assert.Equal(SharePointNotificationStatus.Cancelled, (await Db.SharePointNotificationOutbox.SingleAsync(x => x.EventId == "owner")).Status);
        Assert.Equal(SharePointNotificationStatus.Pending, (await Db.SharePointNotificationOutbox.SingleAsync(x => x.EventId == "other")).Status);
        Assert.Equal(WebPushDeliveryStatus.Cancelled, (await Db.WebPushDeliveries.SingleAsync()).Status);
        Assert.Single(await Db.UserNotifications.ToListAsync());
        Assert.False((await Service.SavePreferencesAsync(user, "fr-FR", true, true)).Succeeded); Assert.Equal("en-US", user.PreferredCulture);
    }

    [Fact]
    public async Task PasswordCreationRequiresFreshVerifiedBoundMicrosoftIdentityAndIsSingleUse()
    {
        var user = await User(); var id = (await Service.BeginVerificationAsync(user, Binding, AccountSelfService.PasswordPurpose))!;
        Assert.False((await Service.CreatePasswordAsync(id, user, Binding, "Newpassword123!")).Succeeded);
        Assert.False(await Service.VerifyAsync(id, user, "different-browser", Identity(user), DateTimeOffset.UtcNow));
        Assert.False(await Service.VerifyAsync(id, user, Binding, Identity(user) with { ObjectId = Guid.NewGuid().ToString() }, DateTimeOffset.UtcNow));
        Assert.False(await Service.VerifyAsync(id, user, Binding, Identity(user), null));
        Assert.False(await Service.VerifyAsync(id, user, Binding, Identity(user), DateTimeOffset.UtcNow.AddMinutes(-10)));
        Assert.True(await Service.VerifyAsync(id, user, Binding, Identity(user), DateTimeOffset.UtcNow));
        Assert.False((await Service.CreatePasswordAsync(id, user, Binding, "bad")).Succeeded);
        Assert.True(await Service.IsVerifiedAsync(id, user, Binding, AccountSelfService.PasswordPurpose));
        Assert.True((await Service.CreatePasswordAsync(id, user, Binding, "Newpassword123!")).Succeeded);
        Assert.True(await Users.CheckPasswordAsync(user, "Newpassword123!")); Assert.Equal(Subject, user.MicrosoftSubject);
        Assert.False((await Service.CreatePasswordAsync(id, user, Binding, "Another123!")).Succeeded);
        Assert.Single(await Db.AdminUserAuditLogs.Where(x => x.Action == AdminUserAuditAction.SelfLocalPasswordCreated).ToListAsync());
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("stamp")]
    [InlineData("link")]
    [InlineData("revision")]
    [InlineData("disabled")]
    [InlineData("revoked")]
    public async Task ChangedSecurityStateInvalidatesExistingGrant(string change)
    {
        var user = await User(); var id = await Verified(user);
        switch (change)
        {
            case "expired": (await Db.AccountSecurityRequests.SingleAsync()).ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); break;
            case "stamp": await Users.UpdateSecurityStampAsync(user); break;
            case "link": user.MicrosoftLinkVersion++; break;
            case "revision": (await Db.MicrosoftIntegrationConfigurations.SingleAsync()).CredentialRevision++; break;
            case "disabled": (await Db.MicrosoftLoginConfigurations.SingleAsync()).Enabled = false; break;
            case "revoked": user.MicrosoftLinkRevoked = true; break;
        }
        await Db.SaveChangesAsync(); Assert.False(await Service.IsVerifiedAsync(id, user, Binding, AccountSelfService.PasswordPurpose));
        Assert.False((await Service.CreatePasswordAsync(id, user, Binding, "Password123!")).Succeeded); Assert.False(await Users.HasPasswordAsync(user));
    }

    [Fact]
    public async Task NewRequestInvalidatesOldGrantAndOtherUserCannotUseIt()
    {
        var user = await User(); var first = await Verified(user); var second = await Verified(user); var other = await User(name: "other");
        Assert.False(await Service.IsVerifiedAsync(first, user, Binding, AccountSelfService.PasswordPurpose));
        Assert.False(await Service.IsVerifiedAsync(second, other, Binding, AccountSelfService.PasswordPurpose));
        Assert.False(await Service.IsVerifiedAsync(second, user, Binding, AccountSelfService.SessionsPurpose));
        Assert.True(await Service.IsVerifiedAsync(second, user, Binding, AccountSelfService.PasswordPurpose));
    }

    [Fact]
    public async Task PasswordlessSessionRevocationNeedsSeparateProofAndDisablesSubscriptions()
    {
        var user = await User(); var oldStamp = user.SecurityStamp;
        Db.WebPushSubscriptions.Add(new() { UserId = user.Id, EndpointHash = "device", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) }); await Db.SaveChangesAsync();
        Assert.False((await Service.RevokeSessionsAsync(user)).Succeeded);
        var wrongPurpose = await Verified(user); Assert.False((await Service.RevokeSessionsAsync(user, wrongPurpose, Binding)).Succeeded);
        var id = await Verified(user, AccountSelfService.SessionsPurpose); Assert.True((await Service.RevokeSessionsAsync(user, id, Binding)).Succeeded);
        Assert.NotEqual(oldStamp, user.SecurityStamp); Assert.NotNull((await Db.WebPushSubscriptions.SingleAsync()).DisabledAt);
        Assert.False((await Service.RevokeSessionsAsync(user, id, Binding)).Succeeded);
    }

    [Fact]
    public async Task OldCookieIsRejectedImmediatelyAfterRevokingSessions()
    {
        var user = await User(password: true); var stamp = user.SecurityStamp!;
        Assert.True((await Service.RevokeSessionsAsync(user)).Succeeded);
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim("AspNet.Identity.SecurityStamp", stamp)], "test"));
        var ticket = new AuthenticationTicket(principal, IdentityConstants.ApplicationScheme);
        var context = new CookieValidatePrincipalContext(http, new AuthenticationScheme(IdentityConstants.ApplicationScheme, null, typeof(CookieAuthenticationHandler)), new CookieAuthenticationOptions(), ticket);
        await MicrosoftSessionValidator.ValidateAsync(context);
        Assert.Null(context.Principal); Assert.True(((TestAuthentication)provider.GetRequiredService<IAuthenticationService>()).SignedOut);
    }

    [Fact]
    public async Task AccountCultureOverridesBrowserAndLegacyUsersStillFallback()
    {
        var user = await User(); var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, user.Id)], "test")) };
        var culture = new AccountCultureProvider(); Assert.Null(await culture.DetermineProviderCultureResult(http));
        await Service.SavePreferencesAsync(user, "en-US", true, true);
        var result = await culture.DetermineProviderCultureResult(http); Assert.Equal("en-US", result!.Cultures.Single().Value);
    }

    [Fact]
    public async Task BrowserOptOutPreventsNewDeliveriesButKeepsInAppHistory()
    {
        var user = await User();
        Db.WebPushSubscriptions.Add(new() { UserId = user.Id, EndpointHash = "device", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1) });
        await Db.SaveChangesAsync();
        await Service.SavePreferencesAsync(user, "id-ID", true, false);
        var notification = new UserNotification { UserId = user.Id, Type = NotificationType.PaymentSubmitted };
        await new WebPushOutboxService(Db).EnqueueAsync(notification, DateTimeOffset.UtcNow);
        Assert.Empty(notification.WebPushDeliveries);
        Db.UserNotifications.Add(notification); await Db.SaveChangesAsync(); Assert.Single(await Db.UserNotifications.ToListAsync());
    }

    [Fact]
    public async Task RecoveryAdminAndDisabledAccountsCannotStartMicrosoftProof()
    {
        var admin = await User(name: "admin");
        Assert.Null(await Service.BeginVerificationAsync(admin, Binding, AccountSelfService.PasswordPurpose));
        var user = await User(); user.LockoutEnabled = true;
        await Users.SetLockoutEndDateAsync(user, DateTimeOffset.UtcNow.AddDays(1));
        Assert.Null(await Service.BeginVerificationAsync(user, Binding, AccountSelfService.PasswordPurpose));
    }

    [Fact]
    public async Task LegacySchemaUpgradePreservesUsersAndDefaultChannelPreferences()
    {
        var user = await User();
        await Db.Database.ExecuteSqlRawAsync("DROP TABLE AccountSecurityRequests");
        await Db.Database.ExecuteSqlRawAsync("ALTER TABLE AspNetUsers DROP COLUMN PreferredCulture; ALTER TABLE AspNetUsers DROP COLUMN TeamsNotificationsEnabled; ALTER TABLE AspNetUsers DROP COLUMN BrowserNotificationsEnabled;");
        await Db.Database.ExecuteSqlRawAsync("ALTER TABLE SharePointNotificationOutbox DROP COLUMN RecipientUserId");
        await DatabaseSchemaUpdater.EnsureAsync(Db); await DatabaseSchemaUpdater.EnsureAsync(Db); Db.ChangeTracker.Clear();
        var retained = await Db.Users.SingleAsync(); Assert.Equal(user.Id, retained.Id); Assert.Null(retained.PreferredCulture);
        Assert.True(retained.TeamsNotificationsEnabled); Assert.True(retained.BrowserNotificationsEnabled); Assert.Empty(await Db.AccountSecurityRequests.ToListAsync());
    }

    public void Dispose() { scope.Dispose(); provider.Dispose(); connection.Dispose(); }
    private sealed class TestAuthentication : IAuthenticationService
    {
        public bool SignedOut { get; private set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(AuthenticateResult.NoResult());
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) { SignedOut = true; return Task.CompletedTask; }
    }
}
