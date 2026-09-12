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
