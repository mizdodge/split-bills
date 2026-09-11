using ClosedXML.Excel;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class ExcelReportServiceTests
{
    [Fact]
    public void Build_ContainsLocalizedSheetsAndPopulatedTypedPersonSummary()
    {
        var report = new ReportQueryResult
        {
            Transactions =
            [
                new ReportTransactionProjection
                {
                    Id = 1, TransactionNumber = "TRX-1", MerchantName = "Cafe", EffectiveDate = new DateOnly(2026, 9, 4),
                    GrandTotal = 150_000, Participants =
                    [
                        new ReportParticipantProjection { ParticipantId = 1, TransactionId = 1, TransactionNumber = "TRX-1", MerchantName = "Cafe", EffectiveDate = new DateOnly(2026, 9, 4), ParticipantName = "Mizan", Username = "mizan", MenuDetail = "Nasi", AmountDue = 100_000, PaidAmount = 100_000, PaymentStatus = ParticipantPaymentStatus.Paid, IsPickupPerson = true, PickupPersonName = "Mizan", PickupProbability = 1m, PickupSelectedAt = new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero) },
                        new ReportParticipantProjection { ParticipantId = 2, TransactionId = 1, TransactionNumber = "TRX-1", MerchantName = "Cafe", EffectiveDate = new DateOnly(2026, 9, 4), ParticipantName = "Ragil", MenuDetail = "Teh", AmountDue = 50_000, UnpaidAmount = 50_000, PaymentStatus = ParticipantPaymentStatus.Unpaid }
                    ]
                }
            ]
        };

        var bytes = new ExcelReportService().Build(report, new System.Globalization.CultureInfo("id-ID"));
        using var workbook = new XLWorkbook(new MemoryStream(bytes));
        Assert.Equal(["Ringkasan", "Detail Pembayaran", "Pivot per Orang", "Rotasi Pickup"], workbook.Worksheets.Select(x => x.Name).ToArray());
        var detail = workbook.Worksheet("Detail Pembayaran");
        Assert.Equal(XLDataType.DateTime, detail.Cell("A6").DataType);
        Assert.Equal(100_000m, detail.Cell("I6").GetValue<decimal>());
        Assert.Equal("Sudah dibayar", detail.Cell("M6").GetString());
        var pivot = workbook.Worksheet("Pivot per Orang");
        Assert.Equal(1, pivot.Cell("B5").GetValue<int>());
        Assert.Equal(100_000m, pivot.Cell("C5").GetValue<decimal>());
        Assert.Equal(100_000m, pivot.Cell("D5").GetValue<decimal>());
        Assert.Equal(0m, pivot.Cell("E5").GetValue<decimal>());
        Assert.Equal(0m, pivot.Cell("F5").GetValue<decimal>());
        Assert.Equal(0m, pivot.Cell("G5").GetValue<decimal>());
        Assert.Equal(1m, pivot.Cell("H5").GetValue<decimal>());
        Assert.False(pivot.Cell("C5").HasFormula);
        Assert.Equal(2, pivot.Cell("B7").GetValue<int>());
        Assert.Equal(150_000m, pivot.Cell("C7").GetValue<decimal>());
        Assert.Equal(100_000m, pivot.Cell("D7").GetValue<decimal>());
        Assert.Equal(50_000m, pivot.Cell("G7").GetValue<decimal>());
        Assert.Equal(150_000m, workbook.Worksheet("Ringkasan").Cell("B6").GetValue<decimal>());
        var pickup = workbook.Worksheet("Rotasi Pickup");
        Assert.Equal("Mizan", pickup.Cell("A5").GetString());
        Assert.Equal(1, pickup.Cell("B5").GetValue<int>());
        Assert.Equal(1, pickup.Cell("C5").GetValue<int>());
        Assert.Equal(1m, pickup.Cell("D5").GetValue<decimal>());
        Assert.Equal("Mizan", detail.Cell("R6").GetString());
    }

    [Fact]
    public void Build_GroupsNamesCaseInsensitivelyAndHandlesZeroAmount()
    {
        var report = new ReportQueryResult
        {
            Transactions =
            [
                new ReportTransactionProjection
                {
                    Id = 1, TransactionNumber = "TRX-1", MerchantName = "Cafe", EffectiveDate = new DateOnly(2026, 9, 4),
                    Participants =
                    [
                        new ReportParticipantProjection { ParticipantId = 1, TransactionId = 1, TransactionNumber = "TRX-1", MerchantName = "Cafe", EffectiveDate = new DateOnly(2026, 9, 4), ParticipantName = "Mizan", AmountDue = 25_000, AwaitingAmount = 25_000, PaymentStatus = ParticipantPaymentStatus.AwaitingConfirmation },
                        new ReportParticipantProjection { ParticipantId = 2, TransactionId = 1, TransactionNumber = "TRX-1", MerchantName = "Cafe", EffectiveDate = new DateOnly(2026, 9, 4), ParticipantName = "mizan", AmountDue = 0, PaymentStatus = ParticipantPaymentStatus.Unpaid }
                    ]
                }
            ]
        };

        using var workbook = new XLWorkbook(new MemoryStream(new ExcelReportService().Build(report, new System.Globalization.CultureInfo("en-US"))));
        var pivot = workbook.Worksheet("Pivot by Person");
        Assert.Equal("Mizan", pivot.Cell("A5").GetString());
        Assert.Equal(2, pivot.Cell("B5").GetValue<int>());
        Assert.Equal(25_000m, pivot.Cell("C5").GetValue<decimal>());
        Assert.Equal(25_000m, pivot.Cell("E5").GetValue<decimal>());
        Assert.Equal(25_000m, pivot.Cell("G5").GetValue<decimal>());
        Assert.Equal(0m, pivot.Cell("H5").GetValue<decimal>());
        Assert.Equal(0m, pivot.Cell("H6").GetValue<decimal>());
    }
}
