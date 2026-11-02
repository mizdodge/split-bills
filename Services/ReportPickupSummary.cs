using Splitbill.Models;

namespace Splitbill.Services;

public sealed record ReportPickupSummaryRow(string Name, string Username, int ParticipationCount,
    int PickupCount, decimal PickupRatio, DateTimeOffset? LastPickupAt, string? LastMerchant, int CurrentPoints = 0);

public static class ReportPickupSummary
{
    public static IReadOnlyList<ReportPickupSummaryRow> Build(IEnumerable<ReportParticipantProjection> participants,
        FoodPickupSelectionStrategy strategy) => participants
        .Where(x => x.PickupStrategy == strategy && !string.IsNullOrWhiteSpace(x.Username))
        .GroupBy(x => x.Username!, StringComparer.OrdinalIgnoreCase)
        .Select(group =>
        {
            var rows = group.ToList();
            var participationCount = rows.Select(x => x.TransactionId).Distinct().Count();
            var pickups = rows.Where(x => x.IsPickupPerson && x.PickupSelectedAt.HasValue)
                .OrderByDescending(x => x.PickupSelectedAt).ThenByDescending(x => x.TransactionId).ToList();
            return new ReportPickupSummaryRow(rows.First().ParticipantName, group.Key, participationCount,
                pickups.Count, participationCount == 0 ? 0m : (decimal)pickups.Count / participationCount,
                pickups.FirstOrDefault()?.PickupSelectedAt, pickups.FirstOrDefault()?.MerchantName, rows.Max(x => x.CurrentPickupPoints));
        }).OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToList();
}
