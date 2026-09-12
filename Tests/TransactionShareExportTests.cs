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

    private sealed class TestLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
        public IStringLocalizer WithCulture(CultureInfo culture) => this;
    }
}
