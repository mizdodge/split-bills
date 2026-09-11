using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record WebPushSubscriptionInput(
    string Endpoint,
    string P256dh,
    string Auth,
    string InstallationId,
    string? Culture,
    string? BrowserLabel,
    DateTimeOffset? ExpiresAt);

public interface IWebPushSubscriptionService
{
    Task<(bool Succeeded, string? Error)> UpsertAsync(string userId, WebPushSubscriptionInput input, CancellationToken cancellationToken = default);
    Task<bool> HeartbeatAsync(string userId, string installationId, CancellationToken cancellationToken = default);
    Task<bool> RemoveAsync(string userId, string? endpoint, string? installationId, CancellationToken cancellationToken = default);
    Task<long?> FindActiveIdAsync(string userId, string installationId, CancellationToken cancellationToken = default);
    Task<int> CountActiveAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class WebPushSubscriptionService(
    ApplicationDbContext db,
    WebPushSecretProtector secrets) : IWebPushSubscriptionService
{
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromDays(90);

    public async Task<(bool Succeeded, string? Error)> UpsertAsync(
        string userId,
        WebPushSubscriptionInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return (false, "User tidak ditemukan.");
        if (!Uri.TryCreate(input.Endpoint?.Trim(), UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeHttps)
            return (false, "Endpoint push harus menggunakan HTTPS.");
        if (string.IsNullOrWhiteSpace(input.P256dh) || input.P256dh.Length > 500 ||
            string.IsNullOrWhiteSpace(input.Auth) || input.Auth.Length > 500)
            return (false, "Kunci subscription push tidak valid.");
        if (string.IsNullOrWhiteSpace(input.InstallationId) || input.InstallationId.Length > 160)
            return (false, "ID perangkat tidak valid.");

        var endpointText = endpoint.AbsoluteUri;
        var now = DateTimeOffset.UtcNow;
        var endpointHash = secrets.Hash(endpointText);
        var installationHash = secrets.Hash(input.InstallationId.Trim());
        var subscription = await db.WebPushSubscriptions
            .SingleOrDefaultAsync(x => x.EndpointHash == endpointHash, cancellationToken);

        if (subscription is not null && subscription.UserId != userId)
            return (false, "Endpoint push sudah terdaftar di akun lain.");

        subscription ??= new WebPushSubscription
        {
            UserId = userId,
            EndpointHash = endpointHash,
            CreatedAt = now
        };
        subscription.ProtectedEndpoint = secrets.ProtectSubscriptionValue(endpointText);
        subscription.ProtectedP256dh = secrets.ProtectSubscriptionValue(input.P256dh.Trim());
        subscription.ProtectedAuth = secrets.ProtectSubscriptionValue(input.Auth.Trim());
        subscription.InstallationIdHash = installationHash;
        subscription.Culture = NormalizeCulture(input.Culture);
        subscription.BrowserLabel = NormalizeLabel(input.BrowserLabel);
        subscription.LastSeenAt = now;
        subscription.ExpiresAt = NormalizeExpiry(input.ExpiresAt, now);
        subscription.DisabledAt = null;
        subscription.LastFailureAt = null;

        if (subscription.Id == 0) db.WebPushSubscriptions.Add(subscription);
        await db.SaveChangesAsync(cancellationToken);
        return (true, null);
    }

    public async Task<bool> HeartbeatAsync(
        string userId,
        string installationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(installationId)) return false;
        var hash = secrets.Hash(installationId.Trim());
        var now = DateTimeOffset.UtcNow;
        var subscriptions = await db.WebPushSubscriptions
            .Where(x => x.UserId == userId && x.InstallationIdHash == hash && x.DisabledAt == null)
            .ToListAsync(cancellationToken);
        if (subscriptions.Count == 0) return false;

        foreach (var subscription in subscriptions)
        {
            subscription.LastSeenAt = now;
            if (subscription.ExpiresAt < now) subscription.ExpiresAt = now.Add(DefaultLifetime);
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveAsync(
        string userId,
        string? endpoint,
        string? installationId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId)) return false;
        var query = db.WebPushSubscriptions.Where(x => x.UserId == userId && x.DisabledAt == null);
        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            var endpointHash = secrets.Hash(endpoint.Trim());
            query = query.Where(x => x.EndpointHash == endpointHash);
        }
        else if (!string.IsNullOrWhiteSpace(installationId))
        {
            var installationHash = secrets.Hash(installationId.Trim());
            query = query.Where(x => x.InstallationIdHash == installationHash);
        }
        else
        {
            return false;
        }

        var subscriptions = await query.ToListAsync(cancellationToken);
        if (subscriptions.Count == 0) return false;
        var now = DateTimeOffset.UtcNow;
        foreach (var subscription in subscriptions) subscription.DisabledAt = now;
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<long?> FindActiveIdAsync(string userId, string installationId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(installationId)) return null;
        var hash = secrets.Hash(installationId.Trim());
        var subscriptions = await db.WebPushSubscriptions
            .Where(x => x.UserId == userId && x.InstallationIdHash == hash && x.DisabledAt == null)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return subscriptions.Where(x => x.ExpiresAt > now).OrderByDescending(x => x.LastSeenAt).Select(x => (long?)x.Id).FirstOrDefault();
    }

    public Task<int> CountActiveAsync(string userId, CancellationToken cancellationToken = default)
    {
        return CountActiveCoreAsync(userId, cancellationToken);
    }

    private async Task<int> CountActiveCoreAsync(string userId, CancellationToken cancellationToken)
    {
        // SQLite cannot translate DateTimeOffset comparisons consistently. The
        // active device list is small, so materialize the indexed user slice
        // and apply the expiry check in memory.
        var subscriptions = await db.WebPushSubscriptions
            .Where(x => x.UserId == userId && x.DisabledAt == null)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        return subscriptions.Count(x => x.ExpiresAt > now);
    }

    private static string NormalizeCulture(string? culture) =>
        culture?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en-US" : "id-ID";

    private static string? NormalizeLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var value = label.Trim();
        return value.Length <= 160 ? value : value[..160];
    }

    private static DateTimeOffset NormalizeExpiry(DateTimeOffset? expiresAt, DateTimeOffset now)
    {
        var requested = expiresAt.GetValueOrDefault();
        if (requested <= now || requested > now.Add(DefaultLifetime)) return now.Add(DefaultLifetime);
        return requested;
    }
}
