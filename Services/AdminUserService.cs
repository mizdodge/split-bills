using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record AdminUserListQuery(string? Search = null, string? Role = null, string? Status = null);

public sealed record AdminUserListItem(
    string Id,
    string Username,
    string DisplayName,
    string? Email,
    string Role,
    bool IsDisabled,
    DateTimeOffset CreatedAt);

public sealed class AdminUserMutationResult
{
    private AdminUserMutationResult(bool succeeded, ApplicationUser? user, string? errorCode, IReadOnlyList<IdentityError> errors)
    {
        Succeeded = succeeded;
        User = user;
        ErrorCode = errorCode;
        Errors = errors;
    }

    public bool Succeeded { get; }
    public ApplicationUser? User { get; }
    public string? ErrorCode { get; }
    public IReadOnlyList<IdentityError> Errors { get; }

    public static AdminUserMutationResult Success(ApplicationUser user) => new(true, user, null, []);
    public static AdminUserMutationResult Failure(string errorCode, params IdentityError[] errors)
        => new(false, null, errorCode, errors);
    public static AdminUserMutationResult IdentityFailure(IEnumerable<IdentityError> errors)
        => new(false, null, "IdentityError", errors.ToArray());
}

public interface IAdminUserService
{
    Task<IReadOnlyList<AdminUserListItem>> ListAsync(AdminUserListQuery query, CancellationToken cancellationToken = default);

    Task<AdminUserMutationResult> CreateAsync(
        string actorUserId,
        string username,
        string displayName,
        string email,
        string role,
        string password,
        CancellationToken cancellationToken = default);

    Task<AdminUserMutationResult> UpdateAsync(
        string actorUserId,
        string targetUserId,
        string displayName,
        string email,
        string role,
        CancellationToken cancellationToken = default);

    Task<AdminUserMutationResult> ResetPasswordAsync(
        string actorUserId,
        string targetUserId,
        string password,
        CancellationToken cancellationToken = default);

    Task<AdminUserMutationResult> SetDisabledAsync(
        string actorUserId,
        string targetUserId,
        bool disabled,
        CancellationToken cancellationToken = default);
}

public sealed class AdminUserService(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    RoleManager<IdentityRole> roleManager) : IAdminUserService
{
    public async Task<IReadOnlyList<AdminUserListItem>> ListAsync(
        AdminUserListQuery query,
        CancellationToken cancellationToken = default)
    {
        var users = await userManager.Users.AsNoTracking().ToListAsync(cancellationToken);
        var roleRows = await db.UserRoles.AsNoTracking()
            .Join(db.Roles.AsNoTracking(), userRole => userRole.RoleId, role => role.Id,
                (userRole, role) => new { userRole.UserId, Role = role.Name! })
            .ToListAsync(cancellationToken);
        var roleByUser = roleRows.GroupBy(x => x.UserId).ToDictionary(
            x => x.Key,
            x => x.Select(y => y.Role).FirstOrDefault(IsKnownRole) ?? DatabaseSeeder.MemberRole);
        var now = DateTimeOffset.UtcNow;
        var search = query.Search?.Trim();
        var filtered = users.Where(user =>
        {
            var role = roleByUser.GetValueOrDefault(user.Id, DatabaseSeeder.MemberRole);
            var isDisabled = IsDisabled(user, now);
            var searchMatch = string.IsNullOrWhiteSpace(search) ||
                              Contains(user.UserName, search) ||
                              Contains(user.DisplayName, search) ||
                              Contains(user.Email, search);
            var roleMatch = string.IsNullOrWhiteSpace(query.Role) ||
                            string.Equals(role, query.Role.Trim(), StringComparison.OrdinalIgnoreCase);
            var statusMatch = string.IsNullOrWhiteSpace(query.Status) ||
                              (query.Status.Equals("disabled", StringComparison.OrdinalIgnoreCase) && isDisabled) ||
                              (query.Status.Equals("active", StringComparison.OrdinalIgnoreCase) && !isDisabled);
            return searchMatch && roleMatch && statusMatch;
        })
        .Select(user => new AdminUserListItem(
            user.Id,
            user.UserName ?? string.Empty,
            string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? string.Empty : user.DisplayName,
            user.Email,
            roleByUser.GetValueOrDefault(user.Id, DatabaseSeeder.MemberRole),
            IsDisabled(user, now),
            user.CreatedAt))
        .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
        .ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
        .ToArray();
        return filtered;
    }

    public async Task<AdminUserMutationResult> CreateAsync(
        string actorUserId,
        string username,
        string displayName,
        string email,
        string role,
        string password,
        CancellationToken cancellationToken = default)
    {
        if (!IsKnownRole(role)) return AdminUserMutationResult.Failure("InvalidRole");
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(email))
            return AdminUserMutationResult.Failure("InvalidInput");
        var cleanUsername = username.Trim();
        var cleanDisplayName = displayName.Trim();
        var cleanEmail = email.Trim();
        var normalizedUsername = userManager.NormalizeName(cleanUsername);
        var normalizedEmail = userManager.NormalizeEmail(cleanEmail);
        if (string.IsNullOrWhiteSpace(normalizedUsername)) return AdminUserMutationResult.Failure("InvalidInput");
        if (string.IsNullOrWhiteSpace(normalizedEmail)) return AdminUserMutationResult.Failure("InvalidInput");
        if (await db.Users.AnyAsync(x => x.NormalizedUserName == normalizedUsername, cancellationToken))
            return AdminUserMutationResult.Failure("DuplicateUsername");
        if (await db.Users.AnyAsync(x => x.NormalizedEmail == normalizedEmail, cancellationToken))
            return AdminUserMutationResult.Failure("DuplicateEmail");
        if (!await roleManager.RoleExistsAsync(role)) return AdminUserMutationResult.Failure("InvalidRole");

        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var user = new ApplicationUser
        {
            UserName = cleanUsername,
            DisplayName = cleanDisplayName,
            Email = cleanEmail,
            EmailConfirmed = true,
            LockoutEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var createResult = await userManager.CreateAsync(user, password);
        if (!createResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AdminUserMutationResult.IdentityFailure(createResult.Errors);
        }

        var roleResult = await userManager.AddToRoleAsync(user, role);
        if (!roleResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AdminUserMutationResult.IdentityFailure(roleResult.Errors);
        }

        db.AdminUserAuditLogs.Add(Audit(actorUserId, user.Id, AdminUserAuditAction.Created,
            new { role, email = user.Email }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AdminUserMutationResult.Success(user);
    }

    public async Task<AdminUserMutationResult> UpdateAsync(
        string actorUserId,
        string targetUserId,
        string displayName,
        string email,
        string role,
        CancellationToken cancellationToken = default)
    {
        if (!IsKnownRole(role)) return AdminUserMutationResult.Failure("InvalidRole");
        if (string.IsNullOrWhiteSpace(displayName) || string.IsNullOrWhiteSpace(email))
            return AdminUserMutationResult.Failure("InvalidInput");
        var cleanDisplayName = displayName.Trim();
        var cleanEmail = email.Trim();
        var user = await userManager.FindByIdAsync(targetUserId);
        if (user is null) return AdminUserMutationResult.Failure("UserNotFound");
        if (string.Equals(actorUserId, targetUserId, StringComparison.Ordinal) &&
            !string.Equals(role, DatabaseSeeder.AdminRole, StringComparison.OrdinalIgnoreCase) &&
            await userManager.IsInRoleAsync(user, DatabaseSeeder.AdminRole))
            return AdminUserMutationResult.Failure("SelfDemotion");

        var normalizedEmail = userManager.NormalizeEmail(cleanEmail);
        if (string.IsNullOrWhiteSpace(normalizedEmail)) return AdminUserMutationResult.Failure("InvalidInput");
        if (await db.Users.AnyAsync(x => x.Id != targetUserId && x.NormalizedEmail == normalizedEmail, cancellationToken))
            return AdminUserMutationResult.Failure("DuplicateEmail");
        var oldRoles = (await userManager.GetRolesAsync(user)).ToArray();
        var oldRole = oldRoles.FirstOrDefault(IsKnownRole) ?? DatabaseSeeder.MemberRole;
        var wasAdmin = oldRoles.Any(x => string.Equals(x, DatabaseSeeder.AdminRole, StringComparison.OrdinalIgnoreCase));
        var roleChanged = oldRoles.Length != 1 || !oldRoles.Any(x => string.Equals(x, role, StringComparison.OrdinalIgnoreCase));
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (roleChanged && wasAdmin &&
            !await HasAnotherEnabledAdminAsync(targetUserId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return AdminUserMutationResult.Failure("LastAdmin");
        }
        var profileChanged = !string.Equals(user.DisplayName, cleanDisplayName, StringComparison.Ordinal) ||
                             !string.Equals(user.Email, cleanEmail, StringComparison.OrdinalIgnoreCase);
        user.DisplayName = cleanDisplayName;
        user.Email = cleanEmail;
        user.EmailConfirmed = true;
        var updateResult = await userManager.UpdateAsync(user);
        if (!updateResult.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AdminUserMutationResult.IdentityFailure(updateResult.Errors);
        }

        if (roleChanged)
        {
            if (oldRoles.Length > 0)
            {
                var removeResult = await userManager.RemoveFromRolesAsync(user, oldRoles);
                if (!removeResult.Succeeded)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return AdminUserMutationResult.IdentityFailure(removeResult.Errors);
                }
            }
            var addResult = await userManager.AddToRoleAsync(user, role);
            if (!addResult.Succeeded)
            {
                await transaction.RollbackAsync(cancellationToken);
                return AdminUserMutationResult.IdentityFailure(addResult.Errors);
            }
        }

        if (profileChanged || roleChanged)
            await userManager.UpdateSecurityStampAsync(user);
        if (profileChanged)
            db.AdminUserAuditLogs.Add(Audit(actorUserId, targetUserId, AdminUserAuditAction.ProfileUpdated,
                new { email = user.Email, displayName = user.DisplayName }));
        if (roleChanged)
            db.AdminUserAuditLogs.Add(Audit(actorUserId, targetUserId, AdminUserAuditAction.RoleChanged,
                new { from = oldRole, to = role }));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AdminUserMutationResult.Success(user);
    }

    public async Task<AdminUserMutationResult> ResetPasswordAsync(
        string actorUserId,
        string targetUserId,
        string password,
        CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(targetUserId);
        if (user is null) return AdminUserMutationResult.Failure("UserNotFound");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var resetToken = await userManager.GeneratePasswordResetTokenAsync(user);
        var result = await userManager.ResetPasswordAsync(user, resetToken, password);
        if (!result.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AdminUserMutationResult.IdentityFailure(result.Errors);
        }
        await userManager.UpdateSecurityStampAsync(user);
        db.AdminUserAuditLogs.Add(Audit(actorUserId, targetUserId, AdminUserAuditAction.PasswordReset, null));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AdminUserMutationResult.Success(user);
    }

    public async Task<AdminUserMutationResult> SetDisabledAsync(
        string actorUserId,
        string targetUserId,
        bool disabled,
        CancellationToken cancellationToken = default)
    {
        var user = await userManager.FindByIdAsync(targetUserId);
        if (user is null) return AdminUserMutationResult.Failure("UserNotFound");
        if (disabled && string.Equals(actorUserId, targetUserId, StringComparison.Ordinal))
            return AdminUserMutationResult.Failure("SelfDisable");
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        if (disabled && await userManager.IsInRoleAsync(user, DatabaseSeeder.AdminRole) &&
            !await HasAnotherEnabledAdminAsync(targetUserId, cancellationToken))
        {
            await transaction.RollbackAsync(cancellationToken);
            return AdminUserMutationResult.Failure("LastAdmin");
        }
        user.LockoutEnabled = true;
        user.LockoutEnd = disabled ? DateTimeOffset.UtcNow.AddYears(100) : null;
        user.AccessFailedCount = 0;
        var result = await userManager.UpdateAsync(user);
        if (!result.Succeeded)
        {
            await transaction.RollbackAsync(cancellationToken);
            return AdminUserMutationResult.IdentityFailure(result.Errors);
        }
        await userManager.UpdateSecurityStampAsync(user);
        db.AdminUserAuditLogs.Add(Audit(actorUserId, targetUserId,
            disabled ? AdminUserAuditAction.Disabled : AdminUserAuditAction.Enabled, null));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return AdminUserMutationResult.Success(user);
    }

    private async Task<bool> HasAnotherEnabledAdminAsync(string excludedUserId, CancellationToken cancellationToken)
    {
        var admins = await userManager.GetUsersInRoleAsync(DatabaseSeeder.AdminRole);
        var now = DateTimeOffset.UtcNow;
        return admins.Any(x => !string.Equals(x.Id, excludedUserId, StringComparison.Ordinal) && !IsDisabled(x, now));
    }

    private static bool IsDisabled(ApplicationUser user, DateTimeOffset now)
        => user.LockoutEnd is not null && user.LockoutEnd > now;

    private static bool Contains(string? value, string search)
        => !string.IsNullOrWhiteSpace(value) && value.Contains(search, StringComparison.OrdinalIgnoreCase);

    private static bool IsKnownRole(string? role)
        => role is DatabaseSeeder.AdminRole or DatabaseSeeder.ModeratorRole or DatabaseSeeder.MemberRole;

    private static AdminUserAuditLog Audit(string actorUserId, string targetUserId, AdminUserAuditAction action, object? summary)
        => new()
        {
            ActorUserId = actorUserId,
            TargetUserId = targetUserId,
            Action = action,
            SummaryJson = summary is null ? null : JsonSerializer.Serialize(summary),
            CreatedAt = DateTimeOffset.UtcNow
        };
}
