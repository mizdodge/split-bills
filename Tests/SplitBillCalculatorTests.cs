using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class SplitBillCalculatorTests
{
    private readonly SplitBillCalculator _calculator = new();

    [Fact]
    public void SplitEqual_AssignsRemainderAndPreservesExactTotal()
    {
        var result = _calculator.SplitEqual(100, ["Andi", "Budi", "Citra"]);

        Assert.Equal([33m, 33m, 34m], result.Select(x => x.Amount).ToArray());
        Assert.Equal(100m, result.Sum(x => x.Amount));
    }

    [Fact]
    public void SplitByItem_SharesItemsAndDistributesAdjustmentsProportionally()
    {
        var items = new[]
        {
            new TransactionItem { Id = 1, Name = "Makanan", TotalPrice = 70_000 },
            new TransactionItem { Id = 2, Name = "Minuman", TotalPrice = 30_000 }
        };
        var assignments = new[]
        {
            new ItemAssignment(1, [0]),
            new ItemAssignment(2, [1, 2])
        };

        var result = _calculator.SplitByItem(115_000, ["Andi", "Budi", "Citra"], items, assignments);

        Assert.Equal([80_500m, 17_250m, 17_250m], result.Select(x => x.Amount).ToArray());
        Assert.Equal(115_000m, result.Sum(x => x.Amount));
    }

    [Fact]
    public void SplitByItem_RejectsAnUnassignedItem()
    {
        var items = new[] { new TransactionItem { Id = 1, Name = "Nasi Goreng", TotalPrice = 35_000 } };

        var exception = Assert.Throws<ArgumentException>(() =>
            _calculator.SplitByItem(38_500, ["Andi"], items, []));

        Assert.Contains("must be assigned", exception.Message);
    }

    [Fact]
    public void SplitEqual_RejectsDuplicateNamesIgnoringCase()
    {
        Assert.Throws<ArgumentException>(() => _calculator.SplitEqual(10_000, ["Fifi", "fifi"]));
    }

    [Fact]
    public void SplitByItemGroups_SupportsOwnedAndSharedQuantitiesTogether()
    {
        var items = new[] { new TransactionItem { Id = 1, Name = "Promo B1G1", Quantity = 3, TotalPrice = 72_000 } };
        var result = _calculator.SplitByItemGroups(72_000, ["Mizan", "Ragil", "Agi"], items,
            [new ItemGroupAssignment(1, [new AllocationGroup(2, [0]), new AllocationGroup(1, [1, 2])])]);

        Assert.Equal(72_000m, result.ParticipantShares.Sum(x => x.Amount));
        Assert.Equal([48_000m, 12_000m, 12_000m], result.ParticipantShares.Select(x => x.Amount).ToArray());
        var shares = Assert.Single(result.ItemAllocations).Shares.OrderBy(x => x.ParticipantIndex).ToArray();
        Assert.Equal([2m, .5m, .5m], shares.Select(x => x.QuantityShare).ToArray());
        Assert.Equal(72_000m, shares.Sum(x => x.Amount));
    }

    [Fact]
    public void SplitByItemGroups_RejectsGroupsThatDoNotConsumeWholeQuantity()
    {
        var items = new[] { new TransactionItem { Id = 1, Name = "Nasi", Quantity = 3, TotalPrice = 15_000 } };
        Assert.Throws<ArgumentException>(() => _calculator.SplitByItemGroups(15_000, ["Mizan"], items,
            [new ItemGroupAssignment(1, [new AllocationGroup(2, [0])]) ]));
    }

    [Fact]
    public void BuildParticipantBreakdowns_ReconcilesCanonicalChargesAndFinalAmount()
    {
        var transaction = new BillTransaction
        {
            Id = 10,
            SplitMethod = SplitMethod.ByItem,
            GrandTotal = 79_700,
            Items =
            [
                new TransactionItem { Id = 1, LineNumber = 1, Name = "Paket ayam", Quantity = 1, TotalPrice = 17_500 },
                new TransactionItem { Id = 2, LineNumber = 2, Name = "Menu lainnya", Quantity = 1, TotalPrice = 102_000 }
            ],
            Charges =
            [
                new TransactionCharge { Id = 1, Label = "Voucher Diskon", Amount = 47_800, Operation = ChargeOperation.Subtract, SortOrder = 1 },
                new TransactionCharge { Id = 2, Label = "Biaya Pengiriman", Amount = 4_000, Operation = ChargeOperation.Add, SortOrder = 2 },
                new TransactionCharge { Id = 3, Label = "Biaya Layanan", Amount = 4_000, Operation = ChargeOperation.Add, SortOrder = 3 }
            ],
            Participants =
            [
                new TransactionParticipant { Id = 101, Name = "Mizan", Amount = 11_672, ItemAllocations = [new ParticipantItemAllocation { TransactionItemId = 1, QuantityShare = 1, Amount = 17_500 }] },
                new TransactionParticipant { Id = 102, Name = "Ragil", Amount = 68_028, ItemAllocations = [new ParticipantItemAllocation { TransactionItemId = 2, QuantityShare = 1, Amount = 102_000 }] }
            ]
        };

        var result = _calculator.BuildParticipantBreakdowns(transaction);
        var mizan = Assert.Single(result, x => x.ParticipantId == 101);

        Assert.Equal(17_500m, mizan.ItemSubtotal);
        Assert.Equal(-7_000m, Signed(mizan.Adjustments.Single(x => x.Label == "Voucher Diskon")));
        Assert.Equal(586m, Signed(mizan.Adjustments.Single(x => x.Label == "Biaya Pengiriman")));
        Assert.Equal(586m, Signed(mizan.Adjustments.Single(x => x.Label == "Biaya Layanan")));
        Assert.Equal(40m, mizan.Adjustments.Single(x => x.Label == "Voucher Diskon").ReceiptPercentage);
        Assert.Equal(3.35m, mizan.Adjustments.Single(x => x.Label == "Biaya Pengiriman").ReceiptPercentage);
        Assert.Equal(11_672m, mizan.ItemSubtotal + mizan.AdjustmentTotal);
        Assert.Equal(11_672m, mizan.FinalAmount);

        foreach (var charge in transaction.Charges)
        {
            var participantTotal = result.Sum(x => x.Adjustments.Where(a => a.Label == charge.Label).Sum(Signed));
            Assert.Equal(Signed(charge.Operation, charge.Amount), participantTotal);
        }
        Assert.Equal(transaction.GrandTotal, result.Sum(x => x.FinalAmount));
    }

    [Fact]
    public void BuildParticipantBreakdowns_EqualSplitCreatesItemShares()
    {
        var transaction = new BillTransaction
        {
            SplitMethod = SplitMethod.Equal,
            GrandTotal = 11_000,
            Items = [new TransactionItem { Id = 1, LineNumber = 1, Name = "Nasi", Quantity = 2, TotalPrice = 10_000 }],
            Participants =
            [new TransactionParticipant { Id = 2, Amount = 5_500 }, new TransactionParticipant { Id = 1, Amount = 5_500 }]
        };

        var result = _calculator.BuildParticipantBreakdowns(transaction);

        Assert.Equal([1m, 1m], result.Select(x => Assert.Single(x.Items).QuantityShare).ToArray());
        Assert.Equal([5_000m, 5_000m], result.Select(x => Assert.Single(x.Items).ParticipantAmount).ToArray());
        Assert.All(result, x => Assert.Equal(x.FinalAmount, x.ItemSubtotal + x.AdjustmentTotal));
    }

    [Fact]
    public void BuildParticipantBreakdowns_ZeroItemBaseUsesReconciliationWithoutDivisionByZero()
    {
        var transaction = new BillTransaction
        {
            SplitMethod = SplitMethod.ByItem,
            GrandTotal = 10_000,
            Items = [new TransactionItem { Id = 1, Name = "Legacy", TotalPrice = 10_000 }],
            Participants = [new TransactionParticipant { Id = 1, Name = "A", Amount = 4_000 }, new TransactionParticipant { Id = 2, Name = "B", Amount = 6_000 }]
        };

        var result = _calculator.BuildParticipantBreakdowns(transaction);

        Assert.Equal([4_000m, 6_000m], result.Select(x => x.FinalAmount).ToArray());
        Assert.All(result, x => Assert.Single(x.Items));
        Assert.All(result, x => Assert.Contains(x.Adjustments, line => line.Kind == ParticipantAdjustmentKind.RoundingReconciliation));
    }

    private static decimal Signed(ParticipantBillAdjustmentLine line) =>
        Signed(line.Operation, line.ParticipantAmount);

    private static decimal Signed(ChargeOperation operation, decimal amount) =>
        operation == ChargeOperation.Subtract ? -Math.Abs(amount) : Math.Abs(amount);
}
