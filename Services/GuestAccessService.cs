using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record GuestAccessContext(GuestAccessLink Link, TransactionParticipant Participant, BillTransaction Transaction);

public interface IGuestAccessTokenService
{
    (string Token, string Hash, string Protected) Create();
    string? Unprotect(string protectedToken);
}

public sealed class GuestAccessTokenService(IDataProtectionProvider protection) : IGuestAccessTokenService
{
    private readonly IDataProtector _protector = protection.CreateProtector("SplitBill.GuestAccessToken.v1");
    public (string Token, string Hash, string Protected) Create()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        var token = Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        return (token, hash, _protector.Protect(token));
    }
    public string? Unprotect(string protectedToken) { try { return _protector.Unprotect(protectedToken); } catch { return null; } }
}

public interface IGuestAccessService
{
    Task EnsureLinksAsync(BillTransaction transaction, string createdByUserId, CancellationToken cancellationToken = default);
    Task<GuestAccessContext?> ResolveAsync(string token, bool touch = true, CancellationToken cancellationToken = default);
    Task<string?> GetCopyTokenAsync(long participantId, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(long transactionId, long participantId, CancellationToken cancellationToken = default);
    Task<string?> RegenerateAsync(long transactionId, long participantId, string userId, CancellationToken cancellationToken = default);
}

public sealed class GuestAccessService(ApplicationDbContext db, IGuestAccessTokenService tokens) : IGuestAccessService
{
    public async Task EnsureLinksAsync(BillTransaction transaction, string createdByUserId, CancellationToken cancellationToken = default)
    {
        foreach (var participant in transaction.Participants.Where(x => x.AccountLink is null))
        {
            var active = await db.GuestAccessLinks.AnyAsync(x => x.ParticipantId == participant.Id && x.Status == GuestAccessLinkStatus.Active, cancellationToken);
            if (active) continue;
            var token = tokens.Create();
            db.GuestAccessLinks.Add(new GuestAccessLink { ParticipantId = participant.Id, TokenHash = token.Hash, ProtectedToken = token.Protected, CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = createdByUserId, Status = GuestAccessLinkStatus.Active });
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<GuestAccessContext?> ResolveAsync(string token, bool touch = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var link = await db.GuestAccessLinks.AsSplitQuery().Include(x => x.Participant).ThenInclude(x => x!.Transaction).ThenInclude(x => x!.Items)
            .Include(x => x.Participant).ThenInclude(x => x!.Transaction).ThenInclude(x => x!.Charges)
            .Include(x => x.Participant).ThenInclude(x => x!.Transaction).ThenInclude(x => x!.ReceiptImages)
            .Include(x => x.Participant).ThenInclude(x => x!.Transaction).ThenInclude(x => x!.Participants).ThenInclude(x => x.ItemAllocations).ThenInclude(x => x.Item)
            .Include(x => x.Participant).ThenInclude(x => x!.Transaction).ThenInclude(x => x!.PickupAssignment).ThenInclude(x => x!.SelectedUser)
            .SingleOrDefaultAsync(x => x.TokenHash == hash && x.Status == GuestAccessLinkStatus.Active, cancellationToken);
        if (link?.Participant?.Transaction is null) return null;
        if (touch)
        {
            link.LastAccessedAt = DateTimeOffset.UtcNow; link.AccessCount = Math.Min(int.MaxValue, link.AccessCount + 1); await db.SaveChangesAsync(cancellationToken);
        }
        return new GuestAccessContext(link, link.Participant, link.Participant.Transaction);
    }

    public async Task<string?> GetCopyTokenAsync(long participantId, CancellationToken cancellationToken = default)
    {
        var link = await db.GuestAccessLinks.SingleOrDefaultAsync(x => x.ParticipantId == participantId && x.Status == GuestAccessLinkStatus.Active, cancellationToken);
        return link is null ? null : tokens.Unprotect(link.ProtectedToken);
    }

    public async Task<bool> RevokeAsync(long transactionId, long participantId, CancellationToken cancellationToken = default)
    {
        var link = await db.GuestAccessLinks.Include(x => x.Participant).ThenInclude(x => x!.Transaction).SingleOrDefaultAsync(x => x.ParticipantId == participantId && x.Participant!.TransactionId == transactionId && x.Status == GuestAccessLinkStatus.Active, cancellationToken);
        if (link is null) return false; link.Status = GuestAccessLinkStatus.Revoked; link.RevokedAt = DateTimeOffset.UtcNow; await db.SaveChangesAsync(cancellationToken); return true;
    }

    public async Task<string?> RegenerateAsync(long transactionId, long participantId, string userId, CancellationToken cancellationToken = default)
    {
        var participant = await db.TransactionParticipants.Include(x => x.Transaction).SingleOrDefaultAsync(x => x.Id == participantId && x.TransactionId == transactionId && x.AccountLink == null, cancellationToken);
        if (participant is null) return null;
        var active = await db.GuestAccessLinks.Where(x => x.ParticipantId == participantId && x.Status == GuestAccessLinkStatus.Active).ToListAsync(cancellationToken);
        foreach (var old in active) { old.Status = GuestAccessLinkStatus.Revoked; old.RevokedAt = DateTimeOffset.UtcNow; }
        var token = tokens.Create(); db.GuestAccessLinks.Add(new GuestAccessLink { ParticipantId = participantId, TokenHash = token.Hash, ProtectedToken = token.Protected, CreatedAt = DateTimeOffset.UtcNow, CreatedByUserId = userId }); await db.SaveChangesAsync(cancellationToken); return token.Token;
    }
}

public sealed record GuestTransactionAccessContext(GuestTransactionAccessLink Link, BillTransaction Transaction);

public interface IGuestTransactionAccessService
{
    Task<string?> GetCopyTokenAsync(long transactionId, string createdByUserId, CancellationToken cancellationToken = default);
    Task<GuestTransactionAccessContext?> ResolveAsync(string token, bool touch = true, CancellationToken cancellationToken = default);
    Task<string?> RegenerateAsync(long transactionId, string userId, CancellationToken cancellationToken = default);
    Task<bool> RevokeAsync(long transactionId, CancellationToken cancellationToken = default);
}

/// <summary>Manages the independent whole-transaction guest token.</summary>
public sealed class GuestTransactionAccessService(ApplicationDbContext db, IGuestAccessTokenService tokens) : IGuestTransactionAccessService
{
    public async Task<string?> GetCopyTokenAsync(long transactionId, string createdByUserId, CancellationToken cancellationToken = default)
    {
        var existing = await db.GuestTransactionAccessLinks
            .SingleOrDefaultAsync(x => x.TransactionId == transactionId && x.Status == GuestAccessLinkStatus.Active, cancellationToken);
        if (existing is not null) return tokens.Unprotect(existing.ProtectedToken);

        var transaction = await db.Transactions
            .Where(x => x.Id == transactionId && x.Status != TransactionStatus.Draft && x.Participants.Any())
            .Select(x => new { x.Id })
            .SingleOrDefaultAsync(cancellationToken);
        if (transaction is null) return null;

        var token = tokens.Create();
        var link = new GuestTransactionAccessLink
        {
            TransactionId = transactionId,
            TokenHash = token.Hash,
            ProtectedToken = token.Protected,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = createdByUserId,
            Status = GuestAccessLinkStatus.Active
        };
        db.GuestTransactionAccessLinks.Add(link);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return token.Token;
        }
        catch (DbUpdateException)
        {
            db.Entry(link).State = EntityState.Detached;
            var winner = await db.GuestTransactionAccessLinks
                .SingleOrDefaultAsync(x => x.TransactionId == transactionId && x.Status == GuestAccessLinkStatus.Active, cancellationToken);
            return winner is null ? null : tokens.Unprotect(winner.ProtectedToken);
        }
    }

    public async Task<GuestTransactionAccessContext?> ResolveAsync(string token, bool touch = true, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 256) return null;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
        var link = await db.GuestTransactionAccessLinks.AsSplitQuery()
            .Include(x => x.Transaction).ThenInclude(x => x!.Items)
            .Include(x => x.Transaction).ThenInclude(x => x!.Charges)
            .Include(x => x.Transaction).ThenInclude(x => x!.ReceiptImages)
            .Include(x => x.Transaction).ThenInclude(x => x!.Participants).ThenInclude(x => x.ItemAllocations).ThenInclude(x => x.Item)
            .Include(x => x.Transaction).ThenInclude(x => x!.PickupAssignment).ThenInclude(x => x!.SelectedUser)
            .SingleOrDefaultAsync(x => x.TokenHash == hash && x.Status == GuestAccessLinkStatus.Active, cancellationToken);
        if (link?.Transaction is null) return null;
        if (touch)
        {
            link.LastAccessedAt = DateTimeOffset.UtcNow;
            link.AccessCount = Math.Min(int.MaxValue, link.AccessCount + 1);
            await db.SaveChangesAsync(cancellationToken);
        }
        return new GuestTransactionAccessContext(link, link.Transaction);
    }

    public async Task<string?> RegenerateAsync(long transactionId, string userId, CancellationToken cancellationToken = default)
    {
        var transactionExists = await db.Transactions.AnyAsync(x => x.Id == transactionId && x.Status != TransactionStatus.Draft && x.Participants.Any(), cancellationToken);
        if (!transactionExists) return null;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var active = await db.GuestTransactionAccessLinks
            .Where(x => x.TransactionId == transactionId && x.Status == GuestAccessLinkStatus.Active)
            .ToListAsync(cancellationToken);
        foreach (var old in active)
        {
            old.Status = GuestAccessLinkStatus.Revoked;
            old.RevokedAt = DateTimeOffset.UtcNow;
        }
        var token = tokens.Create();
        db.GuestTransactionAccessLinks.Add(new GuestTransactionAccessLink
        {
            TransactionId = transactionId,
            TokenHash = token.Hash,
            ProtectedToken = token.Protected,
            CreatedAt = DateTimeOffset.UtcNow,
            CreatedByUserId = userId,
            Status = GuestAccessLinkStatus.Active
        });
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return token.Token;
    }

    public async Task<bool> RevokeAsync(long transactionId, CancellationToken cancellationToken = default)
    {
        var active = await db.GuestTransactionAccessLinks
            .Where(x => x.TransactionId == transactionId && x.Status == GuestAccessLinkStatus.Active)
            .ToListAsync(cancellationToken);
        if (active.Count == 0) return false;
        var now = DateTimeOffset.UtcNow;
        foreach (var link in active)
        {
            link.Status = GuestAccessLinkStatus.Revoked;
            link.RevokedAt = now;
        }
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}
