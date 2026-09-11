using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public interface IWebPushKeyService
{
    Task<WebPushConfiguration> EnsureAsync(CancellationToken cancellationToken = default);

    string UnprotectPrivateKey(WebPushConfiguration configuration);
}

/// <summary>
/// Ensures the installation has one stable VAPID key pair. The private key is
/// protected with the same persistent Data Protection key ring used by the
/// rest of the application and is never returned to the browser.
/// </summary>
public sealed class WebPushKeyService(
    ApplicationDbContext db,
    WebPushSecretProtector secrets) : IWebPushKeyService
{
    private const int ConfigurationId = 1;

    public async Task<WebPushConfiguration> EnsureAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await db.WebPushConfigurations
            .SingleOrDefaultAsync(x => x.Id == ConfigurationId, cancellationToken);

        if (configuration is not null &&
            !string.IsNullOrWhiteSpace(configuration.PublicKey) &&
            !string.IsNullOrWhiteSpace(configuration.ProtectedPrivateKey))
        {
            return configuration;
        }

        var now = DateTimeOffset.UtcNow;
        var pair = VapidKeyGenerator.Generate();
        configuration ??= new WebPushConfiguration
        {
            Id = ConfigurationId,
            CreatedAt = now,
            Subject = "https://splitbill.local",
            Enabled = true
        };
        configuration.PublicKey = pair.PublicKey;
        configuration.ProtectedPrivateKey = secrets.ProtectVapidPrivateKey(pair.PrivateKey);
        configuration.UpdatedAt = now;

        if (configuration.Id == ConfigurationId && db.Entry(configuration).State == EntityState.Detached)
            db.WebPushConfigurations.Add(configuration);

        await db.SaveChangesAsync(cancellationToken);
        return configuration;
    }

    public string UnprotectPrivateKey(WebPushConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (string.IsNullOrWhiteSpace(configuration.ProtectedPrivateKey))
            throw new InvalidOperationException("Web Push VAPID private key belum tersedia.");

        try
        {
            return secrets.UnprotectVapidPrivateKey(configuration.ProtectedPrivateKey);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Web Push VAPID private key tidak dapat dibuka. Pastikan folder data-protection-keys ikut dipertahankan saat update.",
                exception);
        }
    }
}
