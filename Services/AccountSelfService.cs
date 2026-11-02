using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public interface IAccountSelfService
{
    Task<IdentityResult> SaveProfileAsync(ApplicationUser user, string name);
    Task<IdentityResult> SavePreferencesAsync(ApplicationUser user, string culture, bool teams, bool browser);
    Task<string?> BeginVerificationAsync(ApplicationUser user, string binding, string purpose);
    Task<bool> VerifyAsync(string id, ApplicationUser user, string binding, MicrosoftIdentity identity, DateTimeOffset? authenticatedAt);
    Task<bool> IsVerifiedAsync(string id, ApplicationUser user, string binding, string purpose);
    Task<IdentityResult> CreatePasswordAsync(string id, ApplicationUser user, string binding, string password);
    Task<IdentityResult> RevokeSessionsAsync(ApplicationUser user, string? requestId = null, string? binding = null);
}

public sealed class AccountSelfService(ApplicationDbContext db, UserManager<ApplicationUser> users) : IAccountSelfService
{
    public const string PasswordPurpose = "CreatePassword";
    public const string SessionsPurpose = "RevokeSessions";
    private static IdentityResult Invalid() => IdentityResult.Failed(new IdentityError { Code = "AccountVerificationExpired" });

    public async Task<IdentityResult> SaveProfileAsync(ApplicationUser user, string name)
    {
        name = name?.Trim() ?? "";
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) return IdentityResult.Failed(new IdentityError { Code = "AccountNameInvalid" });
        user.DisplayName = name; Audit(user, AdminUserAuditAction.SelfProfileUpdated);
        return await users.UpdateAsync(user);
    }

    public async Task<IdentityResult> SavePreferencesAsync(ApplicationUser user, string culture, bool teams, bool browser)
    {
        if (culture is not ("id-ID" or "en-US")) return IdentityResult.Failed(new IdentityError { Code = "AccountLanguageInvalid" });
        await using var transaction = await db.Database.BeginTransactionAsync();
        user.PreferredCulture = culture; user.TeamsNotificationsEnabled = teams; user.BrowserNotificationsEnabled = browser;
        if (!teams)
        {
            var pending = await db.SharePointNotificationOutbox.Where(x =>
                (x.RecipientUserId == user.Id || (x.RecipientUserId == null && user.Email != null && x.RecipientEmail == user.Email)) &&
                (x.Status == SharePointNotificationStatus.Pending || x.Status == SharePointNotificationStatus.Processing)).ToListAsync();
            foreach (var item in pending) { item.Status = SharePointNotificationStatus.Cancelled; item.LockedUntil = null; item.LastErrorCode = "USER_PREFERENCE"; }
        }
        if (!browser)
        {
            var pending = await db.WebPushDeliveries.Include(x => x.WebPushSubscription).Where(x => x.WebPushSubscription != null &&
                x.WebPushSubscription.UserId == user.Id && (x.Status == WebPushDeliveryStatus.Pending || x.Status == WebPushDeliveryStatus.Processing)).ToListAsync();
            foreach (var item in pending) { item.Status = WebPushDeliveryStatus.Cancelled; item.LockedUntil = null; }
        }
        Audit(user, AdminUserAuditAction.SelfPreferencesUpdated);
        var result = await users.UpdateAsync(user);
        if (result.Succeeded) await transaction.CommitAsync();
        return result;
    }

    public async Task<string?> BeginVerificationAsync(ApplicationUser user, string binding, string purpose)
    {
        if (purpose is not (PasswordPurpose or SessionsPurpose) || binding.Length < 32 ||
            !await MicrosoftAllowedAsync(user) || (purpose == PasswordPurpose && await users.HasPasswordAsync(user))) return null;
        var config = await db.MicrosoftIntegrationConfigurations.SingleAsync(x => x.Id == 1);
        var previous = await db.AccountSecurityRequests.Where(x => x.UserId == user.Id && x.Purpose == purpose && x.UsedAt == null).ToListAsync();
        foreach (var item in previous) item.UsedAt = DateTimeOffset.UtcNow;
        var request = new AccountSecurityRequest { Id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), UserId = user.Id,
            Purpose = purpose, BindingHash = Hash(binding), StampHash = Hash(user.SecurityStamp ?? ""),
            CredentialRevision = config.CredentialRevision, LinkVersion = user.MicrosoftLinkVersion,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5) };
        db.AccountSecurityRequests.Add(request); await db.SaveChangesAsync(); return request.Id;
    }

    public async Task<bool> VerifyAsync(string id, ApplicationUser user, string binding, MicrosoftIdentity identity, DateTimeOffset? authenticatedAt)
    {
        var request = await FindAsync(id, user, binding);
        if (request is null || request.VerifiedAt is not null || authenticatedAt is null ||
            authenticatedAt < request.CreatedAt.AddMinutes(-1) || authenticatedAt > DateTimeOffset.UtcNow.AddMinutes(1) ||
            !string.Equals(identity.TenantId, user.MicrosoftTenantId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(identity.ObjectId, user.MicrosoftSubject, StringComparison.OrdinalIgnoreCase)) return false;
        request.VerifiedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(); return true;
    }

    public async Task<bool> IsVerifiedAsync(string id, ApplicationUser user, string binding, string purpose)
        => await FindAsync(id, user, binding) is { VerifiedAt: not null } request && request.Purpose == purpose;

    public async Task<IdentityResult> CreatePasswordAsync(string id, ApplicationUser user, string binding, string password)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        var request = await FindAsync(id, user, binding);
        if (request?.Purpose != PasswordPurpose || request.VerifiedAt is null || await users.HasPasswordAsync(user)) return Invalid();
        var result = await users.AddPasswordAsync(user, password);
        if (!result.Succeeded) return result;
        request.UsedAt = DateTimeOffset.UtcNow; Audit(user, AdminUserAuditAction.SelfLocalPasswordCreated);
        await db.SaveChangesAsync(); await transaction.CommitAsync(); return result;
    }

    // Local-password callers must first pass CheckPasswordSignInAsync in the controller.
    public async Task<IdentityResult> RevokeSessionsAsync(ApplicationUser user, string? requestId = null, string? binding = null)
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        if (!await users.HasPasswordAsync(user))
        {
            var request = await FindAsync(requestId ?? "", user, binding ?? "");
            if (request?.Purpose != SessionsPurpose || request.VerifiedAt is null) return Invalid();
            request.UsedAt = DateTimeOffset.UtcNow;
        }
        var result = await users.UpdateSecurityStampAsync(user);
        if (!result.Succeeded) return result;
        var subscriptions = await db.WebPushSubscriptions.Where(x => x.UserId == user.Id && x.DisabledAt == null).ToListAsync();
        foreach (var subscription in subscriptions) subscription.DisabledAt = DateTimeOffset.UtcNow;
        Audit(user, AdminUserAuditAction.SelfSessionsRevoked); await db.SaveChangesAsync();
        await transaction.CommitAsync(); return result;
    }

    private async Task<AccountSecurityRequest?> FindAsync(string id, ApplicationUser user, string binding)
    {
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(binding)) return null;
        var request = await db.AccountSecurityRequests.SingleOrDefaultAsync(x => x.Id == id && x.UserId == user.Id);
        if (request is null || request.UsedAt is not null || request.ExpiresAt <= DateTimeOffset.UtcNow ||
            request.BindingHash != Hash(binding) || request.StampHash != Hash(user.SecurityStamp ?? "") ||
            request.LinkVersion != user.MicrosoftLinkVersion || !await MicrosoftAllowedAsync(user)) return null;
        var revision = await db.MicrosoftIntegrationConfigurations.Where(x => x.Id == 1).Select(x => x.CredentialRevision).SingleAsync();
        return revision == request.CredentialRevision ? request : null;
    }

    private async Task<bool> MicrosoftAllowedAsync(ApplicationUser user) =>
        !MicrosoftAccountService.IsRecoveryAdmin(user) && !user.MicrosoftLinkRevoked && user.MicrosoftSubject is not null &&
        !await users.IsLockedOutAsync(user) && await db.MicrosoftLoginConfigurations.AnyAsync(x => x.Id == 1 && x.Enabled) &&
        await db.MicrosoftIntegrationConfigurations.AnyAsync(x => x.Id == 1 && x.TenantId == user.MicrosoftTenantId && x.ProtectedClientSecret != "");

    private void Audit(ApplicationUser user, AdminUserAuditAction action) => db.AdminUserAuditLogs.Add(new()
    { ActorUserId = user.Id, TargetUserId = user.Id, Action = action, SummaryJson = "{\"source\":\"account-settings\"}", CreatedAt = DateTimeOffset.UtcNow });
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
