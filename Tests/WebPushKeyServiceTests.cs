using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Services;
using Splitbill.Models;
using Microsoft.AspNetCore.Identity;

namespace Splitbill.Tests;

public sealed class WebPushKeyServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly WebPushSecretProtector secrets = new(new EphemeralDataProtectionProvider());

    public WebPushKeyServiceTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseSqlite(connection)
            .Options);
        db.Database.EnsureCreated();
    }

    [Fact]
    public void VapidKeyGeneratorCreatesUrlSafeP256Material()
    {
        var pair = VapidKeyGenerator.Generate();
        var publicKey = VapidKeyGenerator.Decode(pair.PublicKey);
        var privateKey = VapidKeyGenerator.Decode(pair.PrivateKey);

        Assert.Equal(65, publicKey.Length);
        Assert.Equal(0x04, publicKey[0]);
        Assert.Equal(32, privateKey.Length);
        Assert.DoesNotContain('=', pair.PublicKey);
        Assert.DoesNotContain('+', pair.PublicKey);
        Assert.DoesNotContain('/', pair.PublicKey);
        Assert.DoesNotContain('=', pair.PrivateKey);
        Assert.DoesNotContain('+', pair.PrivateKey);
        Assert.DoesNotContain('/', pair.PrivateKey);
    }

    [Fact]
    public async Task EnsureCreatesStableProtectedKeyPair()
    {
        var service = new WebPushKeyService(db, secrets);

        var first = await service.EnsureAsync();
        var second = await service.EnsureAsync();

        Assert.Equal(1, await db.WebPushConfigurations.CountAsync());
        Assert.Equal(first.PublicKey, second.PublicKey);
        Assert.Equal(first.ProtectedPrivateKey, second.ProtectedPrivateKey);
        Assert.NotEqual(first.PublicKey, first.ProtectedPrivateKey);

        var privateKey = service.UnprotectPrivateKey(first);
        Assert.Equal(32, VapidKeyGenerator.Decode(privateKey).Length);
    }

    [Fact]
    public async Task SubscriptionServiceUpsertsByEndpointAndSupportsHeartbeatRemoval()
    {
        db.Users.Add(new ApplicationUser { Id = "moderator", UserName = "moderator" });
        await db.SaveChangesAsync();
        var service = new WebPushSubscriptionService(db, secrets);
        var input = new WebPushSubscriptionInput(
            "https://push.example.test/subscription/abc", "p256dh", "auth", "installation-1", "en-US", "Chrome", null);

        var first = await service.UpsertAsync("moderator", input);
        var second = await service.UpsertAsync("moderator", input with { BrowserLabel = "Chrome on laptop" });

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(1, await db.WebPushSubscriptions.CountAsync());
        Assert.Equal(1, await service.CountActiveAsync("moderator"));
        Assert.True(await service.HeartbeatAsync("moderator", "installation-1"));
        Assert.True(await service.RemoveAsync("moderator", null, "installation-1"));
        Assert.Equal(0, await service.CountActiveAsync("moderator"));
    }

    [Fact]
    public async Task SubscriptionServiceRejectsNonHttpsEndpoint()
    {
        var service = new WebPushSubscriptionService(db, secrets);
        var result = await service.UpsertAsync("moderator", new WebPushSubscriptionInput(
            "http://push.example.test/subscription/abc", "p256dh", "auth", "installation-1", null, null, null));

        Assert.False(result.Succeeded);
        Assert.Contains("HTTPS", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task OutboxFansOutOnlyToActiveRecipientSubscriptions()
    {
        db.Users.Add(new ApplicationUser { Id = "moderator", UserName = "moderator" });
        await db.SaveChangesAsync();
        var subscriptionService = new WebPushSubscriptionService(db, secrets);
        await subscriptionService.UpsertAsync("moderator", new WebPushSubscriptionInput(
            "https://push.example.test/subscription/abc", "p256dh", "auth", "installation-1", "id-ID", null, null));

        var notification = new UserNotification
        {
            UserId = "moderator",
            Type = NotificationType.PaymentSubmitted,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRead = false
        };
        db.UserNotifications.Add(notification);
        await new WebPushOutboxService(db).EnqueueAsync(notification, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        Assert.Equal(1, await db.WebPushDeliveries.CountAsync());
        Assert.Equal(WebPushDeliveryStatus.Pending, (await db.WebPushDeliveries.SingleAsync()).Status);
    }

    [Fact]
    public async Task ProcessorMarksSuccessfulDeliverySent()
    {
        const string userId = "moderator";
        db.Users.Add(new ApplicationUser { Id = userId, UserName = userId });
        db.Roles.Add(new IdentityRole { Id = "moderator-role", Name = DatabaseSeeder.ModeratorRole, NormalizedName = "MODERATOR" });
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = "moderator-role" });
        await db.SaveChangesAsync();

        var keyService = new WebPushKeyService(db, secrets);
        var configuration = await keyService.EnsureAsync();
        var subscriptionService = new WebPushSubscriptionService(db, secrets);
        await subscriptionService.UpsertAsync(userId, new WebPushSubscriptionInput(
            "https://push.example.test/subscription/processor", "p256dh", "auth", "installation-processor", "en-US", null, null));
        var notification = new UserNotification
        {
            UserId = userId,
            Type = NotificationType.PaymentSubmitted,
            CreatedAt = DateTimeOffset.UtcNow,
            IsRead = false
        };
        db.UserNotifications.Add(notification);
        await new WebPushOutboxService(db).EnqueueAsync(notification, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var transport = new RecordingTransport();
        var processor = new WebPushDeliveryProcessor(db, transport, Microsoft.Extensions.Logging.Abstractions.NullLogger<WebPushDeliveryProcessor>.Instance);
        Assert.Equal(1, await processor.ProcessDueAsync(1));

        var delivery = await db.WebPushDeliveries.SingleAsync();
        Assert.Equal(WebPushDeliveryStatus.Sent, delivery.Status);
        Assert.Equal(1, delivery.AttemptCount);
        Assert.Equal(configuration.PublicKey, transport.ConfigurationPublicKey);
        Assert.NotNull(transport.Payload);
    }

    [Fact]
    public async Task ProcessorDisablesGoneEndpointAndDeadLettersDelivery()
    {
        const string userId = "moderator";
        db.Users.Add(new ApplicationUser { Id = userId, UserName = userId });
        db.Roles.Add(new IdentityRole { Id = "moderator-role", Name = DatabaseSeeder.ModeratorRole, NormalizedName = "MODERATOR" });
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = userId, RoleId = "moderator-role" });
        await db.SaveChangesAsync();
        await new WebPushKeyService(db, secrets).EnsureAsync();
        var subscriptionService = new WebPushSubscriptionService(db, secrets);
        await subscriptionService.UpsertAsync(userId, new WebPushSubscriptionInput(
            "https://push.example.test/subscription/gone", "p256dh", "auth", "installation-gone", "id-ID", null, null));
        var notification = new UserNotification { UserId = userId, Type = NotificationType.PaymentSubmitted, CreatedAt = DateTimeOffset.UtcNow };
        db.UserNotifications.Add(notification);
        await new WebPushOutboxService(db).EnqueueAsync(notification, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var processor = new WebPushDeliveryProcessor(db, new GoneTransport(), Microsoft.Extensions.Logging.Abstractions.NullLogger<WebPushDeliveryProcessor>.Instance);
        await processor.ProcessDueAsync(1);

        var delivery = await db.WebPushDeliveries.SingleAsync();
        var subscription = await db.WebPushSubscriptions.SingleAsync();
        Assert.Equal(WebPushDeliveryStatus.DeadLetter, delivery.Status);
        Assert.Equal("http-410", delivery.LastErrorCode);
        Assert.NotNull(subscription.DisabledAt);
    }

    private sealed class RecordingTransport : IWebPushTransport
    {
        public string? ConfigurationPublicKey { get; private set; }
        public string? Payload { get; private set; }

        public Task<WebPushSendResult> SendAsync(WebPushConfiguration configuration, WebPushSubscription subscription, string payload, CancellationToken cancellationToken = default)
        {
            ConfigurationPublicKey = configuration.PublicKey;
            Payload = payload;
            return Task.FromResult(new WebPushSendResult(true, false, "sent"));
        }
    }

    private sealed class GoneTransport : IWebPushTransport
    {
        public Task<WebPushSendResult> SendAsync(WebPushConfiguration configuration, WebPushSubscription subscription, string payload, CancellationToken cancellationToken = default) =>
            Task.FromResult(new WebPushSendResult(false, true, "http-410"));
    }

    [Fact]
    public void SubscriptionValuesAreProtectedAndHashedForLookup()
    {
        const string endpoint = "https://push.example.test/subscription/abc";
        var protectedValue = secrets.ProtectSubscriptionValue(endpoint);

        Assert.NotEqual(endpoint, protectedValue);
        Assert.Equal(endpoint, secrets.UnprotectSubscriptionValue(protectedValue));
        Assert.Equal(64, secrets.Hash(endpoint).Length);
        Assert.Equal(secrets.Hash(endpoint), secrets.Hash(endpoint));
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
