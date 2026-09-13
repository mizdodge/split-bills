using System.Globalization;
using Microsoft.Extensions.Localization;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class TransactionShareExportTests
{
    [Fact]
    public void BuildFormatsEffectiveRatesAsPercentagePointsForJpg()
    {
        var previousCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("id-ID");
            var transaction = new BillTransaction
            {
                Id = 1,
                TransactionNumber = "TRX-SHARE",
                MerchantName = "Cafe",
                Status = TransactionStatus.Unpaid,
                Subtotal = 100_000,
                GrandTotal = 131_250,
                Participants = [new TransactionParticipant { Id = 10, Name = "Mizan", Amount = 131_250, PaymentStatus = ParticipantPaymentStatus.Unpaid }],
                Items = [new TransactionItem { Id = 20, Name = "Nasi", Quantity = 1, UnitPrice = 100_000, TotalPrice = 100_000 }],
                Charges = [new TransactionCharge { Label = "Pajak", Amount = 31_250, Operation = ChargeOperation.Add }]
            };
            var breakdown = new ParticipantBillBreakdown(
                10,
                [new ParticipantBillItemLine(20, "Nasi", 1, 100_000, 100_000)],
                100_000,
                [new ParticipantBillAdjustmentLine("Pajak", ChargeOperation.Add, 31_250, 31_250, ParticipantAdjustmentKind.ReceiptCharge, 31.25m, 31.25m)],
                31_250,
                131_250);

            var export = new TransactionShareExportService(new TestLocalizer()).Build(transaction, [breakdown]);

            Assert.Equal("31,25%", export.Participants[0].Adjustments[0].PercentageText);
            Assert.Equal("31,25%", export.ReceiptSummary.Adjustments[0].PercentageText);
        }
        finally
        {
            CultureInfo.CurrentUICulture = previousCulture;
        }
    }

    [Fact]
    public void SharedWithOnlyNamesOtherFractionalOwnersOfTheSameItem()
    {
        static TransactionParticipant Person(long id, string name, long itemId, decimal quantity, decimal amount) => new()
        {
            Id = id,
            Name = name,
            Amount = amount,
            ItemAllocations = [new ParticipantItemAllocation
            {
                TransactionItemId = itemId,
                QuantityShare = quantity,
                Amount = amount
            }]
        };

        var participants = new List<TransactionParticipant>
        {
            Person(1, "Mizan", 10, 2m, 48_000m),
            Person(2, "Ragil", 10, .5m, 12_000m),
            Person(3, "Agi", 10, .5m, 12_000m),
            Person(4, "Rendi", 20, 1m, 5_000m),
            Person(5, "Iyan", 20, 1m, 5_000m)
        };
        var transaction = new BillTransaction
        {
            Id = 1,
            TransactionNumber = "TRX-SHARED",
            MerchantName = "Cafe",
            Status = TransactionStatus.Unpaid,
            Subtotal = 82_000m,
            GrandTotal = 82_000m,
            Participants = participants,
            Items =
            [
                new TransactionItem { Id = 10, Name = "Promo", Quantity = 3, UnitPrice = 24_000m, TotalPrice = 72_000m },
                new TransactionItem { Id = 20, Name = "Nasi", Quantity = 2, UnitPrice = 5_000m, TotalPrice = 10_000m }
            ]
        };
        var breakdowns = participants.Select(person =>
        {
            var allocation = Assert.Single(person.ItemAllocations);
            var name = allocation.TransactionItemId == 10 ? "Promo" : "Nasi";
            return new ParticipantBillBreakdown(person.Id,
                [new ParticipantBillItemLine(allocation.TransactionItemId, name, allocation.QuantityShare, allocation.Amount, allocation.Amount)],
                allocation.Amount, [], 0, allocation.Amount);
        }).ToList();

        var export = new TransactionShareExportService(new TestLocalizer()).Build(transaction, breakdowns);

        Assert.Null(export.Participants[0].Items[0].SharedWithText); // ×2, owned outright
        Assert.Equal("Shared with Agi", export.Participants[1].Items[0].SharedWithText); // ×0.5
        Assert.Equal("Shared with Ragil", export.Participants[2].Items[0].SharedWithText); // ×0.5
        Assert.Null(export.Participants[3].Items[0].SharedWithText); // ×1 of two independent units
        Assert.Null(export.Participants[4].Items[0].SharedWithText);
    }

    private sealed class TestLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name,
            name == "SharedWith" ? $"Shared with {arguments[0]}" : string.Format(CultureInfo.InvariantCulture, name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
        public IStringLocalizer WithCulture(CultureInfo culture) => this;
    }
}
