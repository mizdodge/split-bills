using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.ViewModels;

namespace Splitbill.Services;

public sealed class MicrosoftIntegrationService(
    ApplicationDbContext db,
    IMicrosoftSecretProtector secrets,
    IMicrosoftGraphIdentityService graph,
    IHttpContextAccessor httpContextAccessor,
    IDataProtectionProvider dataProtectionProvider,
    ILogger<MicrosoftIntegrationService> logger) : IMicrosoftIntegrationService
{
    public async Task<MicrosoftIntegrationSettingsViewModel> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await db.MicrosoftIntegrationConfigurations.SingleAsync(x => x.Id == 1, cancellationToken);
        var login = await db.MicrosoftLoginConfigurations.SingleAsync(x => x.Id == 1, cancellationToken);
        return new()
        {
            TenantId = credentials.TenantId,
            ClientId = credentials.ClientId,
            MicrosoftIntegrationEnabled = login.Enabled || !string.IsNullOrWhiteSpace(credentials.ProtectedClientSecret),
            MicrosoftLoginEnabled = login.Enabled,
            HasStoredClientSecret = !string.IsNullOrWhiteSpace(credentials.ProtectedClientSecret),
            LegacyMigrationAttempted = credentials.LegacyMigrationAttempted,
            LegacyMigrationCompleted = credentials.LegacyMigrationCompleted,
            UpdatedAt = credentials.UpdatedAt > login.UpdatedAt ? credentials.UpdatedAt : login.UpdatedAt,
            LastError = credentials.LegacyMigrationError ?? login.LastError
        };
    }

    public Task<MicrosoftIntegrationConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        db.MicrosoftIntegrationConfigurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);

    public async Task SaveAsync(MicrosoftIntegrationSaveViewModel model, string? actorUserId, CancellationToken cancellationToken = default)
    {
        var credentials = await db.MicrosoftIntegrationConfigurations.SingleAsync(x => x.Id == 1, cancellationToken);
        var login = await db.MicrosoftLoginConfigurations.SingleAsync(x => x.Id == 1, cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var tenantId = model.TenantId.Trim();
        var clientId = model.ClientId.Trim();
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _)) throw new InvalidOperationException("Tenant ID and client ID must be valid GUIDs.");

        // Stage credentials before enabling either feature. They become authoritative only after validation.
        if (!string.IsNullOrWhiteSpace(model.ClientSecret))
        {
            var result = await graph.TestCredentialsAsync(tenantId, clientId, model.ClientSecret, cancellationToken);
            if (!result.Succeeded) throw new InvalidOperationException(result.Error ?? "Microsoft credential validation failed.");
            credentials.TenantId = tenantId;
            credentials.ClientId = clientId;
            credentials.ProtectedClientSecret = secrets.Protect(model.ClientSecret);
            credentials.CredentialRevision++;
            credentials.LegacyMigrationCompleted = false;
            credentials.LegacyMigrationError = null;
        }
        else if (credentials.TenantId != tenantId || credentials.ClientId != clientId || string.IsNullOrWhiteSpace(credentials.ProtectedClientSecret))
            throw new InvalidOperationException("Enter a client secret when changing Microsoft credentials.");

        credentials.UpdatedAt = now;
        credentials.UpdatedByUserId = actorUserId;
        login.Enabled = model.MicrosoftLoginEnabled;
        login.ConfigurationRevision++;
        login.UpdatedAt = now;
        login.UpdatedByUserId = actorUserId;
        login.CanonicalOrigin = GetCanonicalOrigin(httpContextAccessor.HttpContext?.Request);
        login.LastError = null;
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Called during startup so legacy SharePoint credentials can be migrated safely once.</summary>
    public async Task<MicrosoftConnectionResult> MigrateLegacySharePointAsync(CancellationToken cancellationToken = default)
    {
        var credentials = await db.MicrosoftIntegrationConfigurations.SingleAsync(x => x.Id == 1, cancellationToken);
        if (credentials.LegacyMigrationAttempted) return new(credentials.LegacyMigrationCompleted, credentials.LegacyMigrationError);
        credentials.LegacyMigrationAttempted = true;
        var legacy = await db.SharePointConfigurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (legacy is null || string.IsNullOrWhiteSpace(legacy.ProtectedClientSecret) || string.IsNullOrWhiteSpace(legacy.TenantId) || string.IsNullOrWhiteSpace(legacy.ClientId))
        {
            credentials.LegacyMigrationCompleted = true;
            credentials.LegacyMigrationError = null;
            await db.SaveChangesAsync(cancellationToken);
            return new(true, null);
        }

        try
        {
            var secret = new SharePointSecretProtector(dataProtectionProvider).Unprotect(legacy.ProtectedClientSecret);
            credentials.TenantId = legacy.TenantId;
            credentials.ClientId = legacy.ClientId;
            credentials.ProtectedClientSecret = secrets.Protect(secret);
            credentials.CredentialRevision++;
            legacy.UseSharedMicrosoftCredentials = true;
            credentials.LegacyMigrationCompleted = true;
            credentials.LegacyMigrationError = null;
            credentials.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
            return new(true, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Legacy SharePoint Microsoft credential migration failed");
            credentials.LegacyMigrationError = "Existing SharePoint credentials could not be migrated automatically.";
            await db.SaveChangesAsync(cancellationToken);
            return new(false, credentials.LegacyMigrationError);
        }
    }

    private static string? GetCanonicalOrigin(HttpRequest? request)
    {
        if (request is null) return null;
        return $"{request.Scheme}://{request.Host}".TrimEnd('/');
    }
}

public interface IMicrosoftIntegrationService
{
    Task<MicrosoftIntegrationSettingsViewModel> GetSettingsAsync(CancellationToken cancellationToken = default);
    Task<MicrosoftIntegrationConfiguration?> GetConfigurationAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(MicrosoftIntegrationSaveViewModel model, string? actorUserId, CancellationToken cancellationToken = default);
    Task<MicrosoftConnectionResult> MigrateLegacySharePointAsync(CancellationToken cancellationToken = default);
}
