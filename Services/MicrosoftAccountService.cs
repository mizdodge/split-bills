using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

// Construct only from an authenticated OIDC ticket, after middleware token validation.
public sealed record MicrosoftIdentity(string TenantId, string ObjectId, string? Email, string Name)
{
    public static MicrosoftIdentity? Read(ClaimsPrincipal? principal, string expectedTenant)
    {
        if (principal?.Identity?.IsAuthenticated != true ||
            !Guid.TryParse(expectedTenant, out var expected) ||
            !Guid.TryParse(principal.FindFirstValue("tid"), out var tenant) || tenant != expected ||
            !Guid.TryParse(principal.FindFirstValue("oid"), out var oid)) return null;
        var email = principal.FindFirstValue("email") ?? principal.FindFirstValue("preferred_username");
        email = email?.Trim();
        if (email?.Length > 256 || !MailAddress.TryCreate(email, out var parsed) || parsed.Address != email)
            email = null;
        var name = principal.FindFirstValue("name")?.Trim() ?? email ?? "Microsoft user";
        return new(tenant.ToString("D"), oid.ToString("D"), email, name[..Math.Min(100, name.Length)]);
    }
}

public sealed record MicrosoftAccountResult(ApplicationUser? User, string? Error);
public sealed record MicrosoftLinkPreview(string IntentId, string LocalName, string MicrosoftName, string? Email);

public interface IMicrosoftAccountService
{
    Task<MicrosoftAccountResult> SignInAsync(MicrosoftIdentity identity);
    Task<string?> BeginLinkAsync(ApplicationUser user, string binding);
    Task<bool> ReceiveAsync(string intentId, ApplicationUser user, string binding, MicrosoftIdentity identity);
    Task<MicrosoftLinkPreview?> PreviewAsync(string intentId, ApplicationUser user, string binding);
    Task<string?> ConfirmAsync(string intentId, ApplicationUser user, string binding);
    Task CancelAsync(string intentId, ApplicationUser user, string binding);
    Task<string?> UnlinkAsync(ApplicationUser user);
}

public sealed class MicrosoftAccountService(ApplicationDbContext db, UserManager<ApplicationUser> users,
    IDataProtectionProvider protection) : IMicrosoftAccountService
{
    private readonly IDataProtector protector = protection.CreateProtector("SplitBill.MicrosoftAccountLink.v2");
    public static bool IsRecoveryAdmin(ApplicationUser user) =>
        string.Equals(user.UserName, "admin", StringComparison.OrdinalIgnoreCase);

    public async Task<MicrosoftAccountResult> SignInAsync(MicrosoftIdentity identity)
    {
        var config = await db.MicrosoftIntegrationConfigurations.SingleAsync(x => x.Id == 1);
        var login = await db.MicrosoftLoginConfigurations.SingleAsync(x => x.Id == 1);
        if (!login.Enabled || !string.Equals(identity.TenantId, config.TenantId, StringComparison.OrdinalIgnoreCase))
            return new(null, "MicrosoftUnavailable");

        // Serialize creation and collision checks. No email/username can select an existing account.
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var linked = await users.Users.SingleOrDefaultAsync(x => x.MicrosoftTenantId == identity.TenantId && x.MicrosoftSubject == identity.ObjectId);
            if (linked is not null)
            {
                if (IsRecoveryAdmin(linked) || linked.MicrosoftLinkRevoked || await users.IsLockedOutAsync(linked))
                    return new(null, "MicrosoftAccountUnavailable");
                await transaction.CommitAsync();
                return new(linked, null);
            }
            if (!login.AllowAutoRegistration) return new(null, "MicrosoftLinkRequired");
            if (identity.Email is null) return new(null, "MicrosoftEmailRequired");
            var username = identity.Email.Split('@')[0];
            var normalizedName = users.NormalizeName(username);
            var normalizedEmail = users.NormalizeEmail(identity.Email);
            // Include legacy, non-normalized records; do not rewrite existing accounts.
            var existing = await users.Users.Select(x => new { x.UserName, x.NormalizedUserName, x.Email, x.NormalizedEmail }).ToListAsync();
            if (string.Equals(username, "admin", StringComparison.OrdinalIgnoreCase) ||
                existing.Any(x => users.NormalizeName(x.UserName ?? "") == normalizedName || x.NormalizedUserName == normalizedName ||
                    users.NormalizeEmail(x.Email ?? "") == normalizedEmail || x.NormalizedEmail == normalizedEmail))
                return new(null, "MicrosoftAccountCollision");
            var user = new ApplicationUser { UserName = username, Email = identity.Email, DisplayName = identity.Name, CreatedAt = DateTimeOffset.UtcNow };
            ApplyIdentity(user, identity, config.CredentialRevision);
            var created = await users.CreateAsync(user); // Passwordless; never generate a default password.
            if (!created.Succeeded) return new(null, "MicrosoftAccountCollision");
            var role = await users.AddToRoleAsync(user, DatabaseSeeder.MemberRole);
            if (!role.Succeeded) return new(null, "MicrosoftAccountUnavailable");
            Audit(user, AdminUserAuditAction.Created);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return new(user, null);
        }
        catch (DbUpdateException) { return new(null, "MicrosoftAccountCollision"); }
    }

    public async Task<string?> BeginLinkAsync(ApplicationUser user, string binding)
    {
        var config = await db.MicrosoftIntegrationConfigurations.SingleAsync(x => x.Id == 1);
        if (IsRecoveryAdmin(user) || await users.IsLockedOutAsync(user) || !await users.HasPasswordAsync(user) ||
            !await db.MicrosoftLoginConfigurations.AnyAsync(x => x.Id == 1 && x.Enabled) ||
            (!user.MicrosoftLinkRevoked && user.MicrosoftSubject is not null)) return null;
        var id = Guid.NewGuid().ToString("N");
        db.MicrosoftAccountLinkIntents.Add(new()
        {
            Id = id, UserId = user.Id, CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            BrowserBindingHash = MicrosoftLinkStateProtector.Hash(binding), SecurityStampHash = MicrosoftLinkStateProtector.Hash(user.SecurityStamp ?? ""),
            CredentialRevision = config.CredentialRevision, ProtectedState = ""
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<MicrosoftAccountLinkIntent?> ValidIntentAsync(string id, ApplicationUser user, string binding)
    {
        var intent = await db.MicrosoftAccountLinkIntents.SingleOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        var config = await db.MicrosoftIntegrationConfigurations.SingleAsync(x => x.Id == 1);
        if (intent is null || intent.UsedAt is not null || intent.ExpiresAt <= DateTimeOffset.UtcNow ||
            IsRecoveryAdmin(user) || await users.IsLockedOutAsync(user) ||
            intent.CredentialRevision != config.CredentialRevision ||
            intent.SecurityStampHash != MicrosoftLinkStateProtector.Hash(user.SecurityStamp ?? "") ||
            intent.BrowserBindingHash != MicrosoftLinkStateProtector.Hash(binding) ||
            !await db.MicrosoftLoginConfigurations.AnyAsync(x => x.Id == 1 && x.Enabled)) return null;
        return intent;
    }

    public async Task<bool> ReceiveAsync(string intentId, ApplicationUser user, string binding, MicrosoftIdentity identity)
    {
        var intent = await ValidIntentAsync(intentId, user, binding);
        var config = await db.MicrosoftIntegrationConfigurations.SingleAsync(x => x.Id == 1);
        if (intent is null || intent.ProtectedState.Length != 0 ||
            !string.Equals(identity.TenantId, config.TenantId, StringComparison.OrdinalIgnoreCase)) return false;
        var value = protector.Protect(JsonSerializer.Serialize(identity));
        return await db.MicrosoftAccountLinkIntents.Where(x => x.Id == intentId && x.UsedAt == null && x.ProtectedState == "")
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ProtectedState, value)) == 1;
    }

    public async Task<MicrosoftLinkPreview?> PreviewAsync(string intentId, ApplicationUser user, string binding)
    {
        var intent = await ValidIntentAsync(intentId, user, binding);
        if (intent is null || intent.ProtectedState.Length == 0) return null;
        try
        {
            var identity = JsonSerializer.Deserialize<MicrosoftIdentity>(protector.Unprotect(intent.ProtectedState));
            return identity is null ? null : new(intentId, user.DisplayName, identity.Name, identity.Email);
        }
        catch (System.Security.Cryptography.CryptographicException) { return null; }
    }

    public async Task<string?> ConfirmAsync(string intentId, ApplicationUser user, string binding)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        try
        {
            var intent = await ValidIntentAsync(intentId, user, binding);
            if (intent is null || intent.ProtectedState.Length == 0) return "MicrosoftLinkExpired";
            var identity = JsonSerializer.Deserialize<MicrosoftIdentity>(protector.Unprotect(intent.ProtectedState));
            if (identity is null) return "MicrosoftLinkExpired";
            if ((!user.MicrosoftLinkRevoked && user.MicrosoftSubject is not null) ||
                await users.Users.AnyAsync(x => x.Id != user.Id && x.MicrosoftTenantId == identity.TenantId && x.MicrosoftSubject == identity.ObjectId))
                return "MicrosoftAccountCollision";
            var consumedAt = DateTimeOffset.UtcNow;
            var consumed = await db.MicrosoftAccountLinkIntents.Where(x => x.Id == intentId && x.UsedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, consumedAt));
            if (consumed != 1) return "MicrosoftLinkExpired";
            ApplyIdentity(user, identity, intent.CredentialRevision);
            var result = await users.UpdateSecurityStampAsync(user);
            if (!result.Succeeded) return "MicrosoftAccountUnavailable";
            Audit(user, AdminUserAuditAction.MicrosoftLinked);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            return null;
        }
        catch (DbUpdateException) { return "MicrosoftAccountCollision"; }
        catch (System.Security.Cryptography.CryptographicException) { return "MicrosoftLinkExpired"; }
    }

    public async Task<string?> UnlinkAsync(ApplicationUser user)
    {
        if (IsRecoveryAdmin(user) || !await users.HasPasswordAsync(user) || await users.IsLockedOutAsync(user))
            return "MicrosoftLocalPasswordRequired";
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Retain the revoked identity as a tombstone so JIT cannot recreate a duplicate account.
        user.MicrosoftLinkRevoked = true;
        user.MicrosoftLinkVersion++;
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) return "MicrosoftAccountUnavailable";
        Audit(user, AdminUserAuditAction.MicrosoftUnlinked);
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        return null;
    }

    public async Task CancelAsync(string intentId, ApplicationUser user, string binding)
    {
        var intent = await ValidIntentAsync(intentId, user, binding);
        if (intent is null) return;
        var usedAt = DateTimeOffset.UtcNow;
        await db.MicrosoftAccountLinkIntents.Where(x => x.Id == intentId && x.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.UsedAt, usedAt));
    }

    private void Audit(ApplicationUser user, AdminUserAuditAction action) => db.AdminUserAuditLogs.Add(new()
    {
        ActorUserId = user.Id, TargetUserId = user.Id, Action = action,
        SummaryJson = "{\"source\":\"MicrosoftAccount\"}", CreatedAt = DateTimeOffset.UtcNow
    });

    private static void ApplyIdentity(ApplicationUser user, MicrosoftIdentity identity, long revision)
    {
        user.MicrosoftTenantId = identity.TenantId; user.MicrosoftSubject = identity.ObjectId;
        user.MicrosoftAccountEmail = identity.Email; user.MicrosoftAccountDisplayName = identity.Name;
        user.MicrosoftLinkedAt = DateTimeOffset.UtcNow; user.MicrosoftLastVerifiedAt = DateTimeOffset.UtcNow;
        user.MicrosoftLinkRevoked = false; user.MicrosoftCredentialRevision = revision; user.MicrosoftLinkVersion++;
    }
}
