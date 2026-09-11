using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record FoodPickupCandidate(
    string UserId,
    string DisplayName,
    long? ParticipantId,
    int PriorPickupCount,
    decimal RawWeight,
    decimal Probability,
    DateTimeOffset? LastPickupAt = null,
    bool Excluded = false);

public sealed record FoodPickupPreview(
    IReadOnlyList<FoodPickupCandidate> Candidates,
    FoodPickupSelectionStrategy Strategy = FoodPickupSelectionStrategy.WeightedRandom)
{
    public bool HasCandidates => Candidates.Count > 0;
}

public sealed record FoodPickupUserStatistic(
    string UserId,
    bool IsEligible,
    bool IsDisabled,
    int ParticipationCount,
    int PickupCount,
    decimal PickupRatio,
    DateTimeOffset? LastPickupAt,
    string? LastPickupMerchant);

public sealed record FoodPickupHistoryRow(
    long Id,
    long TransactionId,
    string TransactionNumber,
    string MerchantName,
    string? PreviousSelectedUserId,
    string SelectedUserId,
    FoodPickupSelectionStrategy Strategy,
    FoodPickupDrawKind DrawKind,
    string? Reason,
    DateTimeOffset CreatedAt);

public sealed record FoodPickupRotationStatistics(
    IReadOnlyList<FoodPickupUserStatistic> Users,
    IReadOnlyList<FoodPickupHistoryRow> History);

public sealed record FoodPickupDrawOperation(
    FoodPickupAssignment? Assignment,
    FoodPickupDrawHistory? History);

public interface IFoodPickupRandomSource
{
    double NextUnitInterval();
}

public sealed class SecureFoodPickupRandomSource : IFoodPickupRandomSource
{
    public double NextUnitInterval()
        => RandomNumberGenerator.GetInt32(0, 1_000_000_000) / 1_000_000_000d;
}

public interface IFoodPickupRotationService
{
    Task<FoodPickupPreview> PreviewAsync(
        IReadOnlyCollection<string> participantUserIds,
        long? excludingTransactionId = null,
        CancellationToken cancellationToken = default);

    Task<FoodPickupDrawOperation> AssignOrReconcileAsync(
        BillTransaction transaction,
        string? actorUserId,
        string? previousSelectedUserId = null,
        FoodPickupAssignment? previousAssignmentSnapshot = null,
        CancellationToken cancellationToken = default);

    Task<FoodPickupDrawOperation?> RerollAsync(
        BillTransaction transaction,
        string adminUserId,
        string reason,
        CancellationToken cancellationToken = default);

    Task<FoodPickupRotationStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Server-owned pickup eligibility, weighted selection, and audit behavior.
/// It deliberately contains no receipt or money calculation logic.
/// </summary>
public sealed class FoodPickupRotationService(
    ApplicationDbContext db,
    IFoodPickupRandomSource? randomSource = null) : IFoodPickupRotationService
{
    private static readonly JsonSerializerOptions SnapshotJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IFoodPickupRandomSource random = randomSource ?? new SecureFoodPickupRandomSource();

    public async Task<FoodPickupPreview> PreviewAsync(
        IReadOnlyCollection<string> participantUserIds,
        long? excludingTransactionId = null,
        CancellationToken cancellationToken = default)
    {
        var ids = participantUserIds
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var configuration = await GetConfigurationAsync(cancellationToken);
        if (ids.Length == 0 || !configuration.Enabled)
            return new FoodPickupPreview([], configuration.Strategy);

        var candidates = await BuildCandidatesAsync(ids, excludingTransactionId, configuration.Strategy, null, cancellationToken);
        return new FoodPickupPreview(candidates, configuration.Strategy);
    }

    public async Task<FoodPickupDrawOperation> AssignOrReconcileAsync(
        BillTransaction transaction,
        string? actorUserId,
        string? previousSelectedUserId = null,
        FoodPickupAssignment? previousAssignmentSnapshot = null,
        CancellationToken cancellationToken = default)
    {
        var existing = await db.FoodPickupAssignments
            .SingleOrDefaultAsync(x => x.TransactionId == transaction.Id, cancellationToken);
        var participants = transaction.Participants
            .Where(x => x.AccountLink is not null && !string.IsNullOrWhiteSpace(x.AccountLink.UserId))
            .ToList();

        // Existing assignments are stable across eligibility changes. Reconcile
        // only the participant foreign key when an editable split recreated rows.
        if (existing is not null)
        {
            var currentWinner = participants.FirstOrDefault(x =>
                x.Id == existing.SelectedParticipantId &&
                string.Equals(x.AccountLink!.UserId, existing.SelectedUserId, StringComparison.Ordinal));
            if (currentWinner is null && !string.IsNullOrWhiteSpace(previousSelectedUserId))
            {
                currentWinner = participants.FirstOrDefault(x =>
                    string.Equals(x.AccountLink!.UserId, previousSelectedUserId, StringComparison.Ordinal));
            }

            if (currentWinner is not null)
            {
                existing.SelectedParticipantId = currentWinner.Id;
                existing.SelectedUserId = currentWinner.AccountLink!.UserId;
                return new FoodPickupDrawOperation(existing, null);
            }

            // The winner was removed from the editable split. Rotation may be
            // disabled now; in that case remove the stale current assignment.
            if (!await IsEnabledAsync(cancellationToken))
            {
                db.FoodPickupAssignments.Remove(existing);
                return new FoodPickupDrawOperation(null, null);
            }

            var redraw = await DrawForParticipantsAsync(participants, transaction.Id, existing.SelectedUserId, FoodPickupDrawKind.AutomaticRedraw, actorUserId, "Winner was removed from the split.", cancellationToken);
            if (redraw.Assignment is null)
                db.FoodPickupAssignments.Remove(existing);
            else
            {
                existing.SelectedUserId = redraw.Assignment.SelectedUserId;
                existing.SelectedParticipantId = redraw.Assignment.SelectedParticipantId;
                existing.RecordedProbability = redraw.Assignment.RecordedProbability;
                existing.Strategy = redraw.Assignment.Strategy;
                existing.DrawKind = redraw.Assignment.DrawKind;
                existing.SelectedAt = redraw.Assignment.SelectedAt;
                existing.SelectedByUserId = redraw.Assignment.SelectedByUserId;
                redraw = redraw with { Assignment = existing };
            }
            return redraw;
        }

        // Recreate a stable winner after an editable split has replaced its
        // participant rows. The old assignment may have been cascade-deleted
        // when the old participant rows were saved, so use the caller snapshot.
        if (existing is null && previousAssignmentSnapshot is not null)
        {
            var previousParticipant = participants.FirstOrDefault(x =>
                string.Equals(x.AccountLink!.UserId, previousAssignmentSnapshot.SelectedUserId, StringComparison.Ordinal));
            if (previousParticipant is not null)
            {
                var preserved = new FoodPickupAssignment
                {
                    TransactionId = transaction.Id,
                    SelectedUserId = previousAssignmentSnapshot.SelectedUserId,
                    SelectedParticipantId = previousParticipant.Id,
                    RecordedProbability = previousAssignmentSnapshot.RecordedProbability,
                    Strategy = previousAssignmentSnapshot.Strategy,
                    DrawKind = previousAssignmentSnapshot.DrawKind,
                    SelectedAt = previousAssignmentSnapshot.SelectedAt,
                    SelectedByUserId = previousAssignmentSnapshot.SelectedByUserId
                };
                db.FoodPickupAssignments.Add(preserved);
                return new FoodPickupDrawOperation(preserved, null);
            }
            if (!await IsEnabledAsync(cancellationToken)) return new FoodPickupDrawOperation(null, null);
            previousSelectedUserId = previousAssignmentSnapshot.SelectedUserId;
            var redraw = await DrawForParticipantsAsync(participants, transaction.Id, previousSelectedUserId,
                FoodPickupDrawKind.AutomaticRedraw, actorUserId, "Winner was removed from the split.", cancellationToken);
            if (redraw.Assignment is not null) db.FoodPickupAssignments.Add(redraw.Assignment);
            return redraw;
        }

        if (!await IsEnabledAsync(cancellationToken)) return new FoodPickupDrawOperation(null, null);
        return await DrawForParticipantsAsync(participants, transaction.Id, null, FoodPickupDrawKind.Initial, actorUserId, null, cancellationToken);
    }

    public async Task<FoodPickupDrawOperation?> RerollAsync(
        BillTransaction transaction,
        string adminUserId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > 500)
            throw new ArgumentException("Reroll reason is required and must be 500 characters or fewer.", nameof(reason));
        if (!await IsEnabledAsync(cancellationToken)) return null;

        var existing = await db.FoodPickupAssignments
            .SingleOrDefaultAsync(x => x.TransactionId == transaction.Id, cancellationToken);
        if (existing is null) return null;

        var participants = transaction.Participants
            .Where(x => x.AccountLink is not null && !string.IsNullOrWhiteSpace(x.AccountLink.UserId))
            .ToList();
        var result = await DrawForParticipantsAsync(participants, transaction.Id, existing.SelectedUserId,
            FoodPickupDrawKind.AdminReroll, adminUserId, reason.Trim(), cancellationToken);
        if (result.Assignment is null || string.Equals(result.Assignment.SelectedUserId, existing.SelectedUserId, StringComparison.Ordinal))
            return null;

        existing.SelectedUserId = result.Assignment.SelectedUserId;
        existing.SelectedParticipantId = result.Assignment.SelectedParticipantId;
        existing.RecordedProbability = result.Assignment.RecordedProbability;
        existing.Strategy = result.Assignment.Strategy;
        existing.DrawKind = result.Assignment.DrawKind;
        existing.SelectedAt = result.Assignment.SelectedAt;
        existing.SelectedByUserId = result.Assignment.SelectedByUserId;
        result = result with { Assignment = existing };
        return result;
    }

    public async Task<FoodPickupRotationStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var eligibleIds = (await db.FoodPickupEligibleUsers.AsNoTracking()
            .Select(x => x.UserId).ToListAsync(cancellationToken)).ToHashSet(StringComparer.Ordinal);
        var users = await db.Users.AsNoTracking().ToListAsync(cancellationToken);
        var active = users.Where(x => x.LockoutEnd is null || x.LockoutEnd <= now)
            .Select(x => x.Id).ToHashSet(StringComparer.Ordinal);

        // Query participants directly so SQLite can use ordinary joins. A
        // correlated SelectMany here is translated to APPLY, which SQLite does
        // not support.
        var participantRows = await db.TransactionParticipants.AsNoTracking()
            .Where(x => x.Transaction != null &&
                        x.Transaction.Status != TransactionStatus.Draft &&
                        x.AccountLink != null)
            .Select(x => new
            {
                x.TransactionId,
                UserId = x.AccountLink!.UserId
            })
            .ToListAsync(cancellationToken);
        var participation = participantRows
            .GroupBy(x => x.UserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(y => y.TransactionId).Distinct().Count(), StringComparer.Ordinal);

        var assignments = await db.FoodPickupAssignments.AsNoTracking()
            .Include(x => x.Transaction)
            .Where(x => x.Transaction != null && x.Transaction.Status != TransactionStatus.Draft)
            .ToListAsync(cancellationToken);
        var pickupGroups = assignments.GroupBy(x => x.SelectedUserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(y => y.SelectedAt).ToList(), StringComparer.Ordinal);
        var stats = users.OrderBy(x => x.DisplayName).ThenBy(x => x.UserName).ThenBy(x => x.Id)
            .Select(user =>
            {
                var pickups = pickupGroups.GetValueOrDefault(user.Id) ?? [];
                var participantCount = participation.GetValueOrDefault(user.Id);
                return new FoodPickupUserStatistic(
                    user.Id,
                    eligibleIds.Contains(user.Id),
                    !active.Contains(user.Id),
                    participantCount,
                    pickups.Count,
                    participantCount == 0 ? 0m : (decimal)pickups.Count / participantCount,
                    pickups.FirstOrDefault()?.SelectedAt,
                    pickups.FirstOrDefault()?.Transaction?.MerchantName);
            }).ToList();

        // SQLite cannot order DateTimeOffset values in SQL. Materialize first,
        // then apply the recent-history ordering in memory.
        var historyEntities = await db.FoodPickupDrawHistories.AsNoTracking()
            .Include(x => x.Transaction)
            .Where(x => x.Transaction != null)
            .ToListAsync(cancellationToken);
        var histories = historyEntities
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(100)
            .Select(x => new FoodPickupHistoryRow(
                x.Id, x.TransactionId, x.Transaction!.TransactionNumber, x.Transaction.MerchantName,
                x.PreviousSelectedUserId, x.SelectedUserId, x.Strategy, x.DrawKind, x.Reason, x.CreatedAt))
            .ToList();
        return new FoodPickupRotationStatistics(stats, histories);
    }

    private async Task<FoodPickupDrawOperation> DrawForParticipantsAsync(
        IReadOnlyList<TransactionParticipant> participants,
        long transactionId,
        string? excludedUserId,
        FoodPickupDrawKind drawKind,
        string? actorUserId,
        string? reason,
        CancellationToken cancellationToken)
    {
        var configuration = await GetConfigurationAsync(cancellationToken);
        if (!configuration.Enabled) return new FoodPickupDrawOperation(null, null);
        var ids = participants.Select(x => x.AccountLink!.UserId).Distinct(StringComparer.Ordinal).ToArray();
        var candidates = await BuildCandidatesAsync(ids, transactionId, configuration.Strategy, excludedUserId, cancellationToken);
        candidates = candidates.Select(x => x with { ParticipantId = participants.FirstOrDefault(p => string.Equals(p.AccountLink!.UserId, x.UserId, StringComparison.Ordinal))?.Id }).ToList();
        var selectable = candidates.Where(x => !x.Excluded).ToList();
        if (selectable.Count == 0) return new FoodPickupDrawOperation(null, null);

        var selected = Select(selectable, configuration.Strategy);
        var selectedParticipant = participants.Single(x => string.Equals(x.AccountLink!.UserId, selected.UserId, StringComparison.Ordinal));
        var now = DateTimeOffset.UtcNow;
        var assignment = new FoodPickupAssignment
        {
            TransactionId = transactionId,
            SelectedUserId = selected.UserId,
            SelectedParticipantId = selectedParticipant.Id,
            RecordedProbability = selected.Probability,
            Strategy = configuration.Strategy,
            DrawKind = drawKind,
            SelectedAt = now,
            SelectedByUserId = actorUserId
        };
        var sequence = await db.FoodPickupDrawHistories
            .Where(x => x.TransactionId == transactionId)
            .Select(x => (int?)x.SequenceNumber)
            .MaxAsync(cancellationToken) ?? 0;
        var history = new FoodPickupDrawHistory
        {
            TransactionId = transactionId,
            SequenceNumber = sequence + 1,
            PreviousSelectedUserId = excludedUserId,
            SelectedUserId = selected.UserId,
            SelectedParticipantId = selectedParticipant.Id,
            Strategy = configuration.Strategy,
            DrawKind = drawKind,
            Reason = reason,
            CandidateSnapshotJson = JsonSerializer.Serialize(candidates, SnapshotJsonOptions),
            CreatedAt = now,
            CreatedByUserId = actorUserId
        };
        db.FoodPickupDrawHistories.Add(history);
        if (drawKind == FoodPickupDrawKind.Initial)
            db.FoodPickupAssignments.Add(assignment);
        return new FoodPickupDrawOperation(assignment, history);
    }

    private async Task<List<FoodPickupCandidate>> BuildCandidatesAsync(
        IReadOnlyCollection<string> participantUserIds,
        long? excludingTransactionId,
        FoodPickupSelectionStrategy strategy,
        string? excludedUserId,
        CancellationToken cancellationToken)
    {
        var eligibleIds = await db.FoodPickupEligibleUsers.AsNoTracking()
            .Where(x => participantUserIds.Contains(x.UserId))
            .Select(x => x.UserId)
            .ToListAsync(cancellationToken);
        if (eligibleIds.Count == 0) return [];

        var users = await db.Users.AsNoTracking()
            .Where(x => eligibleIds.Contains(x.Id))
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow;
        users = users.Where(x => x.LockoutEnd is null || x.LockoutEnd <= now).ToList();
        if (users.Count == 0) return [];

        var assignments = await db.FoodPickupAssignments.AsNoTracking()
            .Where(x => users.Select(u => u.Id).Contains(x.SelectedUserId) &&
                        (!excludingTransactionId.HasValue || x.TransactionId != excludingTransactionId.Value))
            .Select(x => new { x.SelectedUserId, x.TransactionId, x.SelectedAt })
            .ToListAsync(cancellationToken);
        var counts = assignments.GroupBy(x => x.SelectedUserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Select(y => y.TransactionId).Distinct().Count(), StringComparer.Ordinal);
        var lastPickups = assignments.GroupBy(x => x.SelectedUserId, StringComparer.Ordinal)
            .ToDictionary(x => x.Key, x => x.Max(y => y.SelectedAt), StringComparer.Ordinal);
        var ordered = users.OrderBy(x => x.DisplayName).ThenBy(x => x.UserName).ThenBy(x => x.Id).ToList();
        var rawWeights = ordered.Select(user => 1m / (1m + counts.GetValueOrDefault(user.Id))).ToList();
        var candidates = ordered.Select((user, index) => new FoodPickupCandidate(
            user.Id,
            DisplayName(user),
            null,
            counts.GetValueOrDefault(user.Id),
            rawWeights[index],
            0m,
            lastPickups.GetValueOrDefault(user.Id),
            !string.IsNullOrWhiteSpace(excludedUserId) && string.Equals(user.Id, excludedUserId, StringComparison.Ordinal))).ToList();
        var selectable = candidates.Where(x => !x.Excluded).ToList();
        if (selectable.Count == 0) return candidates;

        if (strategy == FoodPickupSelectionStrategy.RoundRobin)
        {
            var next = selectable
                .OrderBy(x => x.LastPickupAt.HasValue ? 1 : 0)
                .ThenBy(x => x.LastPickupAt)
                .ThenBy(x => x.PriorPickupCount)
                .ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.UserId, StringComparer.Ordinal)
                .First();
            return candidates.Select(x => x with
            {
                RawWeight = string.Equals(x.UserId, next.UserId, StringComparison.Ordinal) ? 1m : 0m,
                Probability = string.Equals(x.UserId, next.UserId, StringComparison.Ordinal) ? 1m : 0m
            }).ToList();
        }

        var totalWeight = selectable.Sum(x => x.RawWeight);
        return candidates.Select(x => x with
        {
            Probability = x.Excluded || totalWeight == 0 ? 0m : x.RawWeight / totalWeight
        }).ToList();
    }

    private FoodPickupCandidate Select(
        IReadOnlyList<FoodPickupCandidate> candidates,
        FoodPickupSelectionStrategy strategy)
    {
        if (strategy == FoodPickupSelectionStrategy.RoundRobin)
            return candidates.OrderByDescending(x => x.Probability).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).First();
        var roll = (decimal)Math.Clamp(random.NextUnitInterval(), 0d, 0.999999999d);
        var total = candidates.Sum(x => x.RawWeight);
        var target = roll * total;
        var cumulative = 0m;
        foreach (var candidate in candidates)
        {
            cumulative += candidate.RawWeight;
            if (target < cumulative) return candidate;
        }
        return candidates[^1];
    }

    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken)
        => (await GetConfigurationAsync(cancellationToken)).Enabled;

    private async Task<(bool Enabled, FoodPickupSelectionStrategy Strategy)> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var configuration = await db.FoodPickupConfigurations.AsNoTracking()
            .Where(x => x.Id == 1)
            .Select(x => new { x.Enabled, x.Strategy })
            .SingleOrDefaultAsync(cancellationToken);
        return configuration is null
            ? (false, FoodPickupSelectionStrategy.WeightedRandom)
            : (configuration.Enabled, configuration.Strategy);
    }

    private static string DisplayName(ApplicationUser user)
        => string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? user.Id : user.DisplayName;
}
