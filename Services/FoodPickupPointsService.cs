using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record PickupPointMember(string UserId, string Name);
public sealed record PickupPointChange(string UserId, string Name, int Before, int After, bool IsSelected);
public sealed record PickupPointSnapshot(string? SelectedUserId, int HighestPoints, int TiedCandidates,
    IReadOnlyList<PickupPointChange> Changes);
public sealed record PickupPointHistory(long Id, long? TransactionId, string TransactionNumber, string MerchantName,
    string Kind, string? Reason, DateTimeOffset CreatedAt, PickupPointSnapshot Snapshot);

public interface IFoodPickupPointsService
{
    Task<Dictionary<string, int>> GetBalancesAsync(long? excludingTransactionId = null, CancellationToken cancellationToken = default);
    Task ReconcileAsync(BillTransaction transaction, FoodPickupAssignment? assignment, string? actorUserId,
        FoodPickupDrawHistory? draw = null, string? removalKind = null, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PickupPointHistory>> HistoryAsync(IReadOnlyCollection<long>? transactionIds = null, CancellationToken cancellationToken = default);
}

public sealed class FoodPickupPointsService(ApplicationDbContext db) : IFoodPickupPointsService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<Dictionary<string, int>> GetBalancesAsync(long? excludingTransactionId = null, CancellationToken cancellationToken = default)
    {
        var rounds = (await db.FoodPickupPointRounds.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(x => x.TransactionId);
        // Include pending changes in the caller's atomic split/reroll transaction.
        foreach (var entry in db.ChangeTracker.Entries<FoodPickupPointRound>().ToList())
            if (entry.State == EntityState.Deleted) rounds.Remove(entry.Entity.TransactionId);
            else if (entry.State is EntityState.Added or EntityState.Modified) rounds[entry.Entity.TransactionId] = entry.Entity;
        var balances = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var round in rounds.Values.Where(x => x.TransactionId != excludingTransactionId)
                     .OrderBy(x => x.EffectiveAt).ThenBy(x => x.Id).ThenBy(x => x.TransactionId))
            foreach (var member in ReadMembers(round.MembersJson))
                balances[member.UserId] = member.UserId == round.SelectedUserId ? 0 : checked(balances.GetValueOrDefault(member.UserId) + 1);
        return balances;
    }

    public async Task ReconcileAsync(BillTransaction transaction, FoodPickupAssignment? assignment, string? actorUserId,
        FoodPickupDrawHistory? draw = null, string? removalKind = null, CancellationToken cancellationToken = default)
    {
        var existing = await db.FoodPickupPointRounds.SingleOrDefaultAsync(x => x.TransactionId == transaction.Id, cancellationToken);
        if (assignment?.Strategy != FoodPickupSelectionStrategy.Points)
        {
            if (existing is null) return;
            var before = await GetBalancesAsync(cancellationToken: cancellationToken);
            db.FoodPickupPointRounds.Remove(existing);
            var after = await GetBalancesAsync(cancellationToken: cancellationToken);
            var users = await db.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken);
            var changes = before.Keys.Union(after.Keys).Where(id => before.GetValueOrDefault(id) != after.GetValueOrDefault(id))
                .Select(id => new PickupPointChange(id, users.GetValueOrDefault(id, id), before.GetValueOrDefault(id), after.GetValueOrDefault(id), false)).ToList();
            AddAudit(transaction, removalKind ?? "Removed", actorUserId, null, new(null, 0, 0, changes));
            return;
        }
        var roster = transaction.Participants.Where(x => x.AccountLink != null).Select(x => x.AccountLink!.UserId)
            .Distinct(StringComparer.Ordinal).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        var rosterJson = JsonSerializer.Serialize(roster, Json);
        if (existing is not null && existing.SelectedUserId == assignment.SelectedUserId && existing.RosterJson == rosterJson && draw is null)
            return;
        var eligible = await db.FoodPickupEligibleUsers.AsNoTracking().Select(x => x.UserId).ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        var usersActive = (await db.Users.AsNoTracking().Where(x => roster.Contains(x.Id)).ToListAsync(cancellationToken))
            .Where(x => x.LockoutEnd is null || x.LockoutEnd <= now).ToDictionary(x => x.Id);
        var members = roster.Where(id => (eligible.Contains(id) && usersActive.ContainsKey(id)) || id == assignment.SelectedUserId)
            .Select(id => new PickupPointMember(id, usersActive.TryGetValue(id, out var user) && !string.IsNullOrWhiteSpace(user.DisplayName)
                ? user.DisplayName : transaction.Participants.First(x => x.AccountLink?.UserId == id).Name)).ToList();
        var balances = await GetBalancesAsync(transaction.Id, cancellationToken);
        var candidates = draw is null ? [] : JsonSerializer.Deserialize<List<FoodPickupCandidate>>(draw.CandidateSnapshotJson, Json) ?? [];
        var selectable = candidates.Where(x => !x.Excluded && x.RawWeight > 0).ToList();
        var highest = selectable.Count > 0 ? selectable.Max(x => x.PriorityPoints) : members.Max(x => balances.GetValueOrDefault(x.UserId));
        var tied = selectable.Count > 0 ? selectable.Count : members.Count(x => balances.GetValueOrDefault(x.UserId) == highest);
        var changesForRound = members.Select(x => new PickupPointChange(x.UserId, x.Name, balances.GetValueOrDefault(x.UserId),
            x.UserId == assignment.SelectedUserId ? 0 : checked(balances.GetValueOrDefault(x.UserId) + 1), x.UserId == assignment.SelectedUserId)).ToList();
        var round = existing ?? new FoodPickupPointRound { TransactionId = transaction.Id };
        round.SelectedUserId = assignment.SelectedUserId; round.RosterJson = rosterJson;
        round.MembersJson = JsonSerializer.Serialize(members, Json); round.EffectiveAt = now;
        if (existing is null) db.FoodPickupPointRounds.Add(round);
        AddAudit(transaction, draw?.DrawKind.ToString() ?? (existing is null ? "Initial" : "ParticipantsChanged"), actorUserId, draw?.Reason,
            new(assignment.SelectedUserId, highest, tied, changesForRound));
    }

    public async Task<IReadOnlyList<PickupPointHistory>> HistoryAsync(IReadOnlyCollection<long>? transactionIds = null, CancellationToken cancellationToken = default)
    {
        var query = db.FoodPickupPointAudits.AsNoTracking().AsQueryable();
        if (transactionIds is not null) query = query.Where(x => x.TransactionId != null && transactionIds.Contains(x.TransactionId.Value));
        return (await query.ToListAsync(cancellationToken)).OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Select(x => new PickupPointHistory(x.Id, x.TransactionId, x.TransactionNumber, x.MerchantName, x.Kind, x.Reason, x.CreatedAt,
                JsonSerializer.Deserialize<PickupPointSnapshot>(x.SnapshotJson, Json) ?? new(null, 0, 0, []))).ToList();
    }

    private void AddAudit(BillTransaction transaction, string kind, string? actorUserId, string? reason, PickupPointSnapshot snapshot)
        => db.FoodPickupPointAudits.Add(new FoodPickupPointAudit
        {
            TransactionId = kind == "TransactionDeleted" ? null : transaction.Id, TransactionNumber = transaction.TransactionNumber, MerchantName = transaction.MerchantName,
            Kind = kind, ActorUserId = actorUserId, Reason = reason, CreatedAt = DateTimeOffset.UtcNow,
            SnapshotJson = JsonSerializer.Serialize(snapshot, Json)
        });

    private static List<PickupPointMember> ReadMembers(string json) => JsonSerializer.Deserialize<List<PickupPointMember>>(json, Json) ?? [];
}
