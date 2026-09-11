using Splitbill.Models;

namespace Splitbill.Services;

public sealed record ParticipantShare(string Name, decimal Amount);
public sealed record ItemAssignment(long ItemId, IReadOnlyList<int> ParticipantIndexes);
public sealed record AllocationGroup(int Quantity, IReadOnlyList<int> ParticipantIndexes);
public sealed record ItemGroupAssignment(long ItemId, IReadOnlyList<AllocationGroup> Groups);
public sealed record ParticipantItemShare(int ParticipantIndex, decimal QuantityShare, decimal Amount);
public sealed record ItemAllocationResult(long ItemId, IReadOnlyList<ParticipantItemShare> Shares);
public sealed record SplitCalculation(IReadOnlyList<ParticipantShare> ParticipantShares, IReadOnlyList<ItemAllocationResult> ItemAllocations);

public enum ParticipantAdjustmentKind
{
    ReceiptCharge,
    UnitemizedAdjustment,
    RoundingReconciliation
}

public sealed record ParticipantBillItemLine(
    long ItemId,
    string Name,
    decimal QuantityShare,
    decimal ReceiptAmount,
    decimal ParticipantAmount);

public sealed record ParticipantBillAdjustmentLine(
    string? Label,
    ChargeOperation Operation,
    decimal ReceiptAmount,
    decimal ParticipantAmount,
    ParticipantAdjustmentKind Kind,
    decimal? ReceiptPercentage = null,
    decimal? ParticipantPercentage = null);

public sealed record ParticipantBillBreakdown(
    long ParticipantId,
    IReadOnlyList<ParticipantBillItemLine> Items,
    decimal ItemSubtotal,
    IReadOnlyList<ParticipantBillAdjustmentLine> Adjustments,
    decimal AdjustmentTotal,
    decimal FinalAmount);

public interface ISplitBillCalculator
{
    IReadOnlyList<ParticipantShare> SplitEqual(decimal total, IReadOnlyList<string> names);
    IReadOnlyList<ParticipantShare> SplitByItem(
        decimal grandTotal,
        IReadOnlyList<string> names,
        IReadOnlyList<TransactionItem> items,
        IReadOnlyList<ItemAssignment> assignments);
    SplitCalculation SplitByItemGroups(
        decimal grandTotal,
        IReadOnlyList<string> names,
        IReadOnlyList<TransactionItem> items,
        IReadOnlyList<ItemGroupAssignment> assignments);
    IReadOnlyList<ParticipantBillBreakdown> BuildParticipantBreakdowns(BillTransaction transaction);
}

public sealed class SplitBillCalculator : ISplitBillCalculator
{
    public IReadOnlyList<ParticipantBillBreakdown> BuildParticipantBreakdowns(BillTransaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        var participants = transaction.Participants
            .Select((participant, index) => (participant, index))
            .OrderBy(x => x.participant.Id == 0 ? 1 : 0)
            .ThenBy(x => x.participant.Id)
            .ThenBy(x => x.index)
            .Select(x => x.participant)
            .ToList();
        if (participants.Count == 0) return [];

        var items = transaction.Items.OrderBy(x => x.LineNumber).ThenBy(x => x.Id).ToList();
        var itemAmounts = new decimal[participants.Count, items.Count];
        var itemQuantities = new decimal[participants.Count, items.Count];

        if (transaction.SplitMethod == SplitMethod.Equal)
        {
            for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                var amounts = AllocateComponent(items[itemIndex].TotalPrice,
                    Enumerable.Repeat(1m, participants.Count).ToArray());
                for (var participantIndex = 0; participantIndex < participants.Count; participantIndex++)
                {
                    itemAmounts[participantIndex, itemIndex] = amounts[participantIndex];
                    itemQuantities[participantIndex, itemIndex] = items[itemIndex].Quantity / participants.Count;
                }
            }
        }
        else
        {
            var itemIndexes = items.Select((item, index) => (item.Id, index)).ToDictionary(x => x.Id, x => x.index);
            for (var participantIndex = 0; participantIndex < participants.Count; participantIndex++)
            {
                foreach (var allocation in participants[participantIndex].ItemAllocations)
                {
                    if (!itemIndexes.TryGetValue(allocation.TransactionItemId, out var itemIndex)) continue;
                    itemAmounts[participantIndex, itemIndex] += allocation.Amount;
                    itemQuantities[participantIndex, itemIndex] += allocation.QuantityShare;
                }
            }
        }

        var itemSubtotals = new decimal[participants.Count];
        var itemLines = new List<ParticipantBillItemLine>[participants.Count];
        for (var participantIndex = 0; participantIndex < participants.Count; participantIndex++)
        {
            itemLines[participantIndex] = [];
            for (var itemIndex = 0; itemIndex < items.Count; itemIndex++)
            {
                var quantity = itemQuantities[participantIndex, itemIndex];
                var amount = itemAmounts[participantIndex, itemIndex];
                if (quantity == 0 && amount == 0) continue;
                itemLines[participantIndex].Add(new ParticipantBillItemLine(
                    items[itemIndex].Id,
                    items[itemIndex].Name,
                    quantity,
                    items[itemIndex].TotalPrice,
                    amount));
                itemSubtotals[participantIndex] += amount;
            }
        }

        // Preserve the available receipt lines for legacy rows that have no persisted
        // item allocation at all. Their amounts remain zero and reconciliation below
        // still bridges to the persisted participant total.
        if (transaction.SplitMethod == SplitMethod.ByItem && itemSubtotals.All(x => x == 0) && items.Count > 0)
        {
            for (var participantIndex = 0; participantIndex < participants.Count; participantIndex++)
                itemLines[participantIndex] = items.Select(item => new ParticipantBillItemLine(item.Id, item.Name, 0, item.TotalPrice, 0)).ToList();
        }

        var receiptItemTotal = items.Sum(x => x.TotalPrice);
        var explicitSignedCharges = transaction.Charges.Sum(x => Signed(x.Operation, x.Amount));
        var unitemizedAdjustment = transaction.GrandTotal - receiptItemTotal - explicitSignedCharges;
        var percentageBase = transaction.Subtotal > 0 ? transaction.Subtotal : receiptItemTotal;
        var adjustmentLines = new List<ParticipantBillAdjustmentLine>[participants.Count];
        for (var i = 0; i < participants.Count; i++) adjustmentLines[i] = [];
        var weights = itemSubtotals.Select(x => Math.Max(0m, x)).ToArray();

        foreach (var charge in transaction.Charges.OrderBy(x => x.SortOrder).ThenBy(x => x.Id))
        {
            var signedAmount = Signed(charge.Operation, charge.Amount);
            var allocations = AllocateComponent(signedAmount, weights);
            for (var participantIndex = 0; participantIndex < participants.Count; participantIndex++)
            {
                var allocation = allocations[participantIndex];
                if (charge.Amount == 0 && allocation == 0) continue;
                adjustmentLines[participantIndex].Add(new ParticipantBillAdjustmentLine(
                    string.IsNullOrWhiteSpace(charge.Label) ? null : charge.Label.Trim(),
                    charge.Operation,
                    Math.Abs(charge.Amount),
                    Math.Abs(allocation),
                    ParticipantAdjustmentKind.ReceiptCharge,
                    EffectivePercentage(charge.Amount, percentageBase),
                    EffectivePercentage(Math.Abs(allocation), itemSubtotals[participantIndex])));
            }
        }

        if (unitemizedAdjustment != 0)
        {
            var allocations = AllocateComponent(unitemizedAdjustment, weights);
            for (var participantIndex = 0; participantIndex < participants.Count; participantIndex++)
            {
                var allocation = allocations[participantIndex];
                if (allocation == 0 && unitemizedAdjustment == 0) continue;
                adjustmentLines[participantIndex].Add(new ParticipantBillAdjustmentLine(
                    null,
                    OperationFor(unitemizedAdjustment),
                    Math.Abs(unitemizedAdjustment),
                    Math.Abs(allocation),
                    ParticipantAdjustmentKind.UnitemizedAdjustment,
                    EffectivePercentage(Math.Abs(unitemizedAdjustment), percentageBase),
                    EffectivePercentage(Math.Abs(allocation), itemSubtotals[participantIndex])));
            }
        }

        var results = new List<ParticipantBillBreakdown>(participants.Count);
        for (var participantIndex = 0; participantIndex < participants.Count; participantIndex++)
        {
            var targetAdjustment = participants[participantIndex].Amount - itemSubtotals[participantIndex];
            var allocatedAdjustment = adjustmentLines[participantIndex].Sum(x => Signed(x.Operation, x.ParticipantAmount));
            var reconciliation = targetAdjustment - allocatedAdjustment;
            if (reconciliation != 0)
            {
                adjustmentLines[participantIndex].Add(new ParticipantBillAdjustmentLine(
                    null,
                    OperationFor(reconciliation),
                    0,
                    Math.Abs(reconciliation),
                    ParticipantAdjustmentKind.RoundingReconciliation));
            }

            results.Add(new ParticipantBillBreakdown(
                participants[participantIndex].Id,
                itemLines[participantIndex],
                itemSubtotals[participantIndex],
                adjustmentLines[participantIndex],
                targetAdjustment,
                participants[participantIndex].Amount));
        }

        return results;
    }

    public IReadOnlyList<ParticipantShare> SplitEqual(decimal total, IReadOnlyList<string> names)
    {
        ValidateNames(names);
        if (total < 0) throw new ArgumentOutOfRangeException(nameof(total));

        var baseAmount = Math.Floor(total / names.Count);
        var result = names.Select(x => new ParticipantShare(x, baseAmount)).ToArray();
        result[^1] = result[^1] with { Amount = baseAmount + (total - baseAmount * names.Count) };
        return result;
    }

    // Legacy checkbox contract: selected people share the complete item row.
    public IReadOnlyList<ParticipantShare> SplitByItem(
        decimal grandTotal,
        IReadOnlyList<string> names,
        IReadOnlyList<TransactionItem> items,
        IReadOnlyList<ItemAssignment> assignments)
    {
        ValidateNames(names);
        if (items.Count == 0) throw new ArgumentException("At least one receipt item is required.", nameof(items));

        var assignmentByItem = assignments.ToDictionary(x => x.ItemId);
        var baseAmounts = new decimal[names.Count];
        foreach (var item in items)
        {
            if (!assignmentByItem.TryGetValue(item.Id, out var assignment) || assignment.ParticipantIndexes.Count == 0)
                throw new ArgumentException($"Item '{item.Name}' must be assigned to at least one participant.", nameof(assignments));
            var indexes = ValidateIndexes(assignment.ParticipantIndexes, names.Count);
            var each = item.TotalPrice / indexes.Length;
            foreach (var index in indexes) baseAmounts[index] += each;
        }

        return ApplyAdjustment(grandTotal, names, baseAmounts);
    }

    public SplitCalculation SplitByItemGroups(
        decimal grandTotal,
        IReadOnlyList<string> names,
        IReadOnlyList<TransactionItem> items,
        IReadOnlyList<ItemGroupAssignment> assignments)
    {
        ValidateNames(names);
        if (grandTotal < 0) throw new ArgumentOutOfRangeException(nameof(grandTotal));
        if (items.Count == 0) throw new ArgumentException("At least one receipt item is required.", nameof(items));

        var itemById = items.ToDictionary(x => x.Id);
        var assignmentByItem = assignments.ToDictionary(x => x.ItemId);
        if (assignmentByItem.Count != assignments.Count || assignmentByItem.Keys.Any(id => !itemById.ContainsKey(id)))
            throw new ArgumentException("Assignments contain an unknown or duplicate item.", nameof(assignments));

        var baseAmounts = new decimal[names.Count];
        var itemResults = new List<ItemAllocationResult>(items.Count);
        foreach (var item in items)
        {
            if (!assignmentByItem.TryGetValue(item.Id, out var assignment) || assignment.Groups.Count == 0)
                throw new ArgumentException($"Item '{item.Name}' must be assigned to at least one group.", nameof(assignments));
            if (item.Quantity <= 0 || item.Quantity != decimal.Truncate(item.Quantity))
                throw new ArgumentException($"Item '{item.Name}' has an invalid quantity.", nameof(items));

            var itemQuantity = checked((int)item.Quantity);
            var groupQuantity = 0;
            var calculatedShares = new List<ParticipantItemShare>();
            for (var groupIndex = 0; groupIndex < assignment.Groups.Count; groupIndex++)
            {
                var group = assignment.Groups[groupIndex];
                if (group.Quantity <= 0 || group.Quantity > itemQuantity)
                    throw new ArgumentException($"Item '{item.Name}' has an invalid group quantity.", nameof(assignments));
                var participantIndexes = ValidateIndexes(group.ParticipantIndexes, names.Count);
                groupQuantity = checked(groupQuantity + group.Quantity);

                var rawGroupAmount = item.TotalPrice * group.Quantity / itemQuantity;
                var groupAmount = groupIndex == assignment.Groups.Count - 1
                    ? item.TotalPrice - calculatedShares.Sum(x => x.Amount)
                    : Math.Round(rawGroupAmount, 0, MidpointRounding.AwayFromZero);
                if (groupIndex == assignment.Groups.Count - 1 && groupAmount < 0)
                    throw new ArgumentException($"Item '{item.Name}' group amounts are invalid.", nameof(assignments));

                var memberBase = Math.Floor(groupAmount / participantIndexes.Length);
                var memberAllocated = 0m;
                for (var memberIndex = 0; memberIndex < participantIndexes.Length; memberIndex++)
                {
                    var amount = memberIndex == participantIndexes.Length - 1
                        ? groupAmount - memberAllocated
                        : memberBase;
                    memberAllocated += amount;
                    calculatedShares.Add(new ParticipantItemShare(
                        participantIndexes[memberIndex],
                        (decimal)group.Quantity / participantIndexes.Length,
                        amount));
                    baseAmounts[participantIndexes[memberIndex]] += amount;
                }
            }

            if (groupQuantity != itemQuantity)
                throw new ArgumentException($"Item '{item.Name}' must allocate exactly {itemQuantity} quantity.", nameof(assignments));

            itemResults.Add(new ItemAllocationResult(item.Id, calculatedShares
                .GroupBy(x => x.ParticipantIndex)
                .Select(x => new ParticipantItemShare(x.Key, x.Sum(v => v.QuantityShare), x.Sum(v => v.Amount)))
                .ToList()));
        }

        return new SplitCalculation(ApplyAdjustment(grandTotal, names, baseAmounts), itemResults);
    }

    private static IReadOnlyList<ParticipantShare> ApplyAdjustment(decimal grandTotal, IReadOnlyList<string> names, decimal[] baseAmounts)
    {
        var itemTotal = baseAmounts.Sum();
        if (itemTotal <= 0) return new SplitBillCalculator().SplitEqual(grandTotal, names);

        var adjustment = grandTotal - itemTotal;
        var final = new decimal[names.Count];
        decimal allocated = 0;
        for (var i = 0; i < names.Count - 1; i++)
        {
            var proportionalAdjustment = Math.Round(adjustment * (baseAmounts[i] / itemTotal), 0, MidpointRounding.AwayFromZero);
            final[i] = Math.Round(baseAmounts[i] + proportionalAdjustment, 0, MidpointRounding.AwayFromZero);
            allocated += final[i];
        }
        final[^1] = grandTotal - allocated;
        return names.Select((name, index) => new ParticipantShare(name, final[index])).ToArray();
    }

    private static decimal Signed(ChargeOperation operation, decimal amount) =>
        operation == ChargeOperation.Subtract ? -Math.Abs(amount) : Math.Abs(amount);

    private static ChargeOperation OperationFor(decimal amount) =>
        amount < 0 ? ChargeOperation.Subtract : ChargeOperation.Add;

    private static decimal? EffectivePercentage(decimal amount, decimal baseAmount) =>
        baseAmount > 0 ? Math.Round(Math.Abs(amount) / baseAmount * 100m, 2, MidpointRounding.AwayFromZero) : null;

    private static decimal[] AllocateComponent(decimal signedAmount, IReadOnlyList<decimal> weights)
    {
        var result = new decimal[weights.Count];
        if (weights.Count == 0 || signedAmount == 0) return result;
        var totalWeight = weights.Sum();
        if (totalWeight <= 0) return result;

        var precision = decimal.Truncate(signedAmount) == signedAmount ? 0 : 2;
        var magnitude = Math.Abs(signedAmount);
        var allocated = 0m;
        for (var index = 0; index < weights.Count - 1; index++)
        {
            var raw = magnitude * weights[index] / totalWeight;
            var share = Math.Round(raw, precision, MidpointRounding.AwayFromZero);
            result[index] = share;
            allocated += share;
        }

        result[^1] = magnitude - allocated;
        if (result[^1] < 0)
        {
            // A two-decimal component can round above its total. Recompute using floors
            // so the stable final participant always receives a non-negative remainder.
            allocated = 0;
            for (var index = 0; index < weights.Count - 1; index++)
            {
                var raw = magnitude * weights[index] / totalWeight;
                result[index] = Math.Floor(raw * 100m) / 100m;
                allocated += result[index];
            }
            result[^1] = magnitude - allocated;
        }

        if (signedAmount < 0)
            for (var index = 0; index < result.Length; index++) result[index] = -result[index];
        return result;
    }

    private static int[] ValidateIndexes(IReadOnlyList<int> indexes, int nameCount)
    {
        if (indexes.Count == 0) throw new ArgumentException("At least one participant is required.");
        if (indexes.Any(x => x < 0 || x >= nameCount)) throw new ArgumentException("An item assignment contains an invalid participant index.");
        var unique = indexes.Distinct().ToArray();
        if (unique.Length != indexes.Count) throw new ArgumentException("A group cannot contain duplicate participants.");
        return unique;
    }

    private static void ValidateNames(IReadOnlyList<string> names)
    {
        if (names.Count == 0) throw new ArgumentException("At least one participant is required.", nameof(names));
        if (names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Participant names cannot be empty.", nameof(names));
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new ArgumentException("Participant names must be unique.", nameof(names));
    }
}
