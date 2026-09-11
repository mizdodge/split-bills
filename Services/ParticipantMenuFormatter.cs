using System.Globalization;
using Splitbill.Models;

namespace Splitbill.Services;

public static class ParticipantMenuFormatter
{
    public sealed record ItemDetail(string Name, decimal Quantity, decimal Amount);

    public static IReadOnlyList<ItemDetail> GetDetails(TransactionParticipant participant, SplitMethod splitMethod)
    {
        var transaction = participant.Transaction;
        if (transaction is null) return [];
        if (splitMethod == SplitMethod.ByItem)
        {
            return participant.ItemAllocations.OrderBy(x => x.Item?.LineNumber)
                .Select(x => new ItemDetail(x.Item?.Name ?? "Item", x.QuantityShare, x.Amount)).ToList();
        }

        var count = Math.Max(1, transaction.Participants.Count);
        return transaction.Items.OrderBy(x => x.LineNumber)
            .Select(x => new ItemDetail(x.Name, x.Quantity / count, x.TotalPrice / count)).ToList();
    }

    public static string Format(TransactionParticipant participant, SplitMethod splitMethod)
    {
        var details = GetDetails(participant, splitMethod);
        if (details.Count == 0) return string.Empty;
        var all = details.Select(x => $"{x.Name} × {x.Quantity.ToString("0.##", CultureInfo.InvariantCulture)}");
        return splitMethod == SplitMethod.Equal ? $"Semua item (bagi rata): {string.Join(", ", all)}" : string.Join(", ", all);
    }
}
