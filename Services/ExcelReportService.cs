using System.Globalization;
using ClosedXML.Excel;

namespace Splitbill.Services;

public interface IExcelReportService
{
    byte[] Build(ReportQueryResult report, CultureInfo culture);
}

public sealed class ExcelReportService : IExcelReportService
{
    public byte[] Build(ReportQueryResult report, CultureInfo culture)
    {
        var english = culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        var labels = english ? EnglishLabels : IndonesianLabels;
        using var workbook = new XLWorkbook();
        var summary = workbook.Worksheets.Add(labels.Summary);
        var detail = workbook.Worksheets.Add(labels.Details);
        var pivot = workbook.Worksheets.Add(labels.Pivot);
        var pickup = workbook.Worksheets.Add(labels.PickupRotation);
        var rows = report.Participants.OrderBy(x => x.EffectiveDate).ThenBy(x => x.TransactionNumber).ThenBy(x => x.ParticipantName).ToList();

        BuildSummary(summary, report, labels, detail.Name);
        var detailEnd = BuildDetails(detail, rows, labels);
        BuildPersonSummary(pivot, rows, labels, detail.Name, detailEnd);
        BuildPickupSummary(pickup, rows, labels, report.PickupStrategy);
        if (report.PickupStrategy == Models.FoodPickupSelectionStrategy.Points)
            BuildPointHistory(workbook.Worksheets.Add(labels.PointHistory), report.PointHistory, labels, english);
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static void BuildSummary(IXLWorksheet sheet, ReportQueryResult report, Labels labels, string detailName)
    {
        sheet.ShowGridLines = false;
        sheet.Cell("A1").Value = labels.Summary;
        sheet.Cell("A2").Value = labels.Generated;
        sheet.Cell("B2").Value = DateTime.Now;
        sheet.Cell("B2").Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        sheet.Cell("A4").Value = labels.Metric;
        sheet.Cell("B4").Value = labels.Value;
        var metrics = new[] { labels.Transactions, labels.TotalBilled, labels.Paid, labels.Awaiting, labels.Unpaid, labels.Outstanding };
        for (var index = 0; index < metrics.Length; index++) sheet.Cell(5 + index, 1).Value = metrics[index];
        sheet.Cell("B5").Value = report.TotalTransactions;
        sheet.Cell("B6").Value = report.TotalAmount;
        sheet.Cell("B7").Value = report.PaidAmount;
        sheet.Cell("B8").Value = report.AwaitingAmount;
        sheet.Cell("B9").Value = report.UnpaidAmount;
        sheet.Cell("B10").Value = report.OutstandingAmount;
        sheet.Range("A1:B1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 16;
        ApplyHeaderStyle(sheet.Range("A4:B4"));
        sheet.Range("B6:B10").Style.NumberFormat.Format = "\"Rp \"#,##0.00";
        sheet.Columns("A:B").AdjustToContents();
        sheet.Column(1).Width = 28;
        sheet.Column(2).Width = 22;
    }

    private static int BuildDetails(IXLWorksheet sheet, IReadOnlyList<ReportParticipantProjection> rows, Labels labels)
    {
        sheet.ShowGridLines = false;
        var headers = new[] { labels.Date, labels.TransactionNumber, labels.Merchant, labels.Uploader, labels.Participant, labels.Username, labels.SplitMethod, labels.Menu, labels.AmountDue, labels.PaidAmount, labels.AwaitingAmount, labels.UnpaidAmount, labels.Status, labels.ClaimDate, labels.ResolutionDate, labels.ResolvedBy, labels.LastAction, labels.PickupPerson, labels.Currency, labels.ReportingCurrency, labels.ReportingAmount, labels.PickupStrategy };
        sheet.Cell(5, 1).InsertData(new[] { headers });
        if (rows.Count > 0)
        {
            var data = rows.Select(x => new object?[]
            {
                x.EffectiveDate.ToDateTime(TimeOnly.MinValue), x.TransactionNumber, x.MerchantName, x.UploaderName ?? string.Empty,
                x.ParticipantName, x.Username ?? string.Empty, x.MenuDetail.Contains("bagi rata", StringComparison.OrdinalIgnoreCase) ? labels.Equal : labels.ByItem,
                x.MenuDetail, x.AmountDue, x.PaidAmount, x.AwaitingAmount, x.UnpaidAmount, StatusLabel(x.PaymentStatus, labels),
                x.ClaimDate?.ToLocalTime().DateTime, x.ResolutionDate?.ToLocalTime().DateTime, x.ResolvedBy ?? string.Empty, ActionLabel(x.LastAction, labels), x.PickupPersonName ?? string.Empty,
                x.CurrencyCode, x.ReportingCurrencyCode, ReportValue(x.ReportingAmountDue, x.AmountDue), x.PickupStrategy.HasValue ? StrategyLabel(x.PickupStrategy.Value, labels) : string.Empty
            }).ToList();
            sheet.Cell(6, 1).InsertData(data);
        }
        var end = Math.Max(6, rows.Count + 5);
        var table = sheet.Range(5, 1, end, headers.Length).CreateTable("PaymentDetails");
        table.Theme = XLTableTheme.TableStyleMedium2;
        sheet.SheetView.FreezeRows(5);
        sheet.Column(1).Style.DateFormat.Format = "yyyy-mm-dd";
        sheet.Columns(14, 15).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        sheet.Columns(9, 12).Style.NumberFormat.Format = "#,##0.00";
        sheet.Column(21).Style.NumberFormat.Format = "#,##0.00";
        sheet.Columns().AdjustToContents();
        sheet.Column(8).Width = 48;
        sheet.Column(8).Style.Alignment.WrapText = true;
        sheet.Column(1).Width = 14;
        sheet.Column(2).Width = 18;
        return end;
    }

    private static void BuildPickupSummary(IXLWorksheet sheet, IReadOnlyList<ReportParticipantProjection> rows, Labels labels, Models.FoodPickupSelectionStrategy strategy)
    {
        sheet.ShowGridLines = false;
        sheet.Cell("A1").Value = labels.PickupRotation;
        sheet.Cell("A2").Value = labels.PickupStrategy;
        sheet.Cell("B2").Value = StrategyLabel(strategy, labels);
        sheet.Range("A1:F1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 16;
        var headers = new[] { labels.Participant, labels.ParticipatedTransactions, labels.PickupCount, labels.PickupRatio, labels.LastPickupDate, labels.LastMerchant };
        if (strategy == Models.FoodPickupSelectionStrategy.Points) headers = [.. headers, labels.CurrentPoints];
        sheet.Cell(4, 1).InsertData(new[] { headers });
        var people = ReportPickupSummary.Build(rows, strategy);
        for (var index = 0; index < people.Count; index++)
        {
            var row = 5 + index;
            var person = people[index];
            sheet.Cell(row, 1).Value = person.Name;
            sheet.Cell(row, 2).Value = person.ParticipationCount;
            sheet.Cell(row, 3).Value = person.PickupCount;
            sheet.Cell(row, 4).Value = person.ParticipationCount == 0 ? 0m : (decimal)person.PickupCount / person.ParticipationCount;
            if (person.LastPickupAt.HasValue) sheet.Cell(row, 5).Value = person.LastPickupAt.Value.ToLocalTime().DateTime;
            sheet.Cell(row, 6).Value = person.LastMerchant ?? string.Empty;
            if (strategy == Models.FoodPickupSelectionStrategy.Points) sheet.Cell(row, 7).Value = person.CurrentPoints;
        }
        var end = Math.Max(5, people.Count + 4);
        var table = sheet.Range(4, 1, end, headers.Length).CreateTable("PickupRotation");
        table.Theme = XLTableTheme.TableStyleMedium2;
        ApplyHeaderStyle(sheet.Range(4, 1, 4, headers.Length));
        sheet.Column(4).Style.NumberFormat.Format = "0.0%";
        sheet.Column(5).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        sheet.SheetView.FreezeRows(4);
        sheet.Columns().AdjustToContents();
        sheet.Column(1).Width = 24;
        sheet.Column(6).Width = 30;
    }

    private static void BuildPersonSummary(IXLWorksheet sheet, IReadOnlyList<ReportParticipantProjection> rows, Labels labels, string detailName, int detailEnd)
    {
        sheet.ShowGridLines = false;
        sheet.Cell("A1").Value = labels.Pivot;
        sheet.Range("A1:H1").Merge();
        sheet.Cell("A1").Style.Font.Bold = true;
        sheet.Cell("A1").Style.Font.FontSize = 16;
        var headers = new[] { labels.Participant, labels.TransactionCount, labels.TotalBilled, labels.PaidAmount, labels.AwaitingAmount, labels.UnpaidAmount, labels.Outstanding, labels.PaidPercentage };
        sheet.Cell(4, 1).InsertData(new[] { headers });
        var people = rows
            .GroupBy(x => x.ParticipantName, StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
            .Select(group => new PersonSummary(
                group.Select(x => x.ParticipantName).OrderBy(x => x, StringComparer.Ordinal).First(),
                group.Count(),
                group.Sum(x => ReportValue(x.ReportingAmountDue, x.AmountDue)),
                group.Sum(x => ReportValue(x.ReportingPaidAmount, x.PaidAmount)),
                group.Sum(x => ReportValue(x.ReportingAwaitingAmount, x.AwaitingAmount)),
                group.Sum(x => ReportValue(x.ReportingUnpaidAmount, x.UnpaidAmount))))
            .ToList();
        for (var index = 0; index < people.Count; index++)
        {
            var row = 5 + index;
            var person = people[index];
            sheet.Cell(row, 1).Value = person.Name;
            sheet.Cell(row, 2).Value = person.TransactionCount;
            sheet.Cell(row, 3).Value = person.TotalBilled;
            sheet.Cell(row, 4).Value = person.PaidAmount;
            sheet.Cell(row, 5).Value = person.AwaitingAmount;
            sheet.Cell(row, 6).Value = person.UnpaidAmount;
            sheet.Cell(row, 7).Value = person.Outstanding;
            sheet.Cell(row, 8).Value = person.TotalBilled == 0 ? 0m : person.PaidAmount / person.TotalBilled;
        }
        var totalRow = Math.Max(5, people.Count + 5);
        sheet.Cell(totalRow, 1).Value = labels.Total;
        var total = new PersonSummary(
            labels.Total,
            people.Sum(x => x.TransactionCount),
            people.Sum(x => x.TotalBilled),
            people.Sum(x => x.PaidAmount),
            people.Sum(x => x.AwaitingAmount),
            people.Sum(x => x.UnpaidAmount));
        sheet.Cell(totalRow, 2).Value = total.TransactionCount;
        sheet.Cell(totalRow, 3).Value = total.TotalBilled;
        sheet.Cell(totalRow, 4).Value = total.PaidAmount;
        sheet.Cell(totalRow, 5).Value = total.AwaitingAmount;
        sheet.Cell(totalRow, 6).Value = total.UnpaidAmount;
        sheet.Cell(totalRow, 7).Value = total.Outstanding;
        sheet.Cell(totalRow, 8).Value = total.TotalBilled == 0 ? 0m : total.PaidAmount / total.TotalBilled;
        var last = Math.Max(totalRow, 5);
        ApplyHeaderStyle(sheet.Range(4, 1, 4, headers.Length));
        sheet.Range(totalRow, 1, totalRow, headers.Length).Style.Font.Bold = true;
        sheet.Range(5, 3, last, 7).Style.NumberFormat.Format = "\"Rp \"#,##0.00";
        sheet.Range(5, 8, last, 8).Style.NumberFormat.Format = "0.0%";
        sheet.SheetView.FreezeRows(4);
        sheet.Columns().AdjustToContents();
        sheet.Column(1).Width = 24;
    }

    private sealed record PersonSummary(string Name, int TransactionCount, decimal TotalBilled, decimal PaidAmount, decimal AwaitingAmount, decimal UnpaidAmount)
    {
        public decimal Outstanding => AwaitingAmount + UnpaidAmount;
    }


    private static void ApplyHeaderStyle(IXLRange range)
    {
        range.Style.Fill.BackgroundColor = XLColor.FromHtml("#003A40");
        range.Style.Font.FontColor = XLColor.White;
        range.Style.Font.Bold = true;
    }

    private static string StatusLabel(Models.ParticipantPaymentStatus status, Labels labels) => status switch
    {
        Models.ParticipantPaymentStatus.Paid => labels.Paid,
        Models.ParticipantPaymentStatus.AwaitingConfirmation => labels.Awaiting,
        _ => labels.Unpaid
    };

    private static string ActionLabel(Models.PaymentActionType action, Labels labels) => action switch
    {
        Models.PaymentActionType.MemberSubmitted => labels.MemberSubmitted,
        Models.PaymentActionType.ModeratorConfirmed => labels.ModeratorConfirmed,
        Models.PaymentActionType.ModeratorRejected => labels.ModeratorRejected,
        Models.PaymentActionType.ModeratorMarkedPaid => labels.ModeratorMarkedPaid,
        Models.PaymentActionType.ModeratorReopened => labels.ModeratorReopened,
        _ => labels.Legacy
    };

    private static decimal ReportValue(decimal reporting, decimal original) => reporting == 0m && original != 0m ? original : reporting;

    private static string StrategyLabel(Models.FoodPickupSelectionStrategy strategy, Labels labels) => strategy switch
    {
        Models.FoodPickupSelectionStrategy.Points => labels.Points,
        Models.FoodPickupSelectionStrategy.RoundRobin => labels.RoundRobin,
        _ => labels.WeightedRandom
    };

    private static void BuildPointHistory(IXLWorksheet sheet, IReadOnlyList<PickupPointHistory> history, Labels labels, bool english)
    {
        sheet.Cell("A1").Value = labels.PointHistory;
        string[] headers = english ? ["Date", "Transaction", "Merchant", "Event", "Participant", "Before", "After", "Pickup person", "Highest points", "Priority candidates", "Reason"]
            : ["Tanggal", "Transaksi", "Merchant", "Peristiwa", "Peserta", "Sebelum", "Sesudah", "Pengambil", "Poin tertinggi", "Kandidat prioritas", "Alasan"];
        sheet.Cell(4, 1).InsertData(new[] { headers });
        var row = 5;
        foreach (var entry in history)
            foreach (var change in entry.Snapshot.Changes.Count > 0 ? entry.Snapshot.Changes : [new PickupPointChange("", "", 0, 0, false)])
            {
                sheet.Cell(row, 1).Value = entry.CreatedAt.ToLocalTime().DateTime;
                sheet.Cell(row, 2).Value = entry.TransactionNumber; sheet.Cell(row, 3).Value = entry.MerchantName;
                sheet.Cell(row, 4).Value = PointEventLabel(entry.Kind, english); sheet.Cell(row, 5).Value = change.Name;
                sheet.Cell(row, 6).Value = change.Before; sheet.Cell(row, 7).Value = change.After;
                sheet.Cell(row, 8).Value = change.IsSelected ? (english ? "Yes" : "Ya") : string.Empty;
                sheet.Cell(row, 9).Value = entry.Snapshot.HighestPoints; sheet.Cell(row, 10).Value = entry.Snapshot.TiedCandidates;
                sheet.Cell(row, 11).Value = entry.Reason ?? (entry.Snapshot.SelectedUserId is not null && entry.Kind != "ParticipantsChanged"
                    ? (english ? $"Random among {entry.Snapshot.TiedCandidates} priority candidates; highest points: {entry.Snapshot.HighestPoints}."
                        : $"Acak di antara {entry.Snapshot.TiedCandidates} kandidat prioritas; poin tertinggi: {entry.Snapshot.HighestPoints}.")
                    : (english ? "Contribution reconciled; other saved winners preserved." : "Kontribusi direkonsiliasi; pengambil transaksi lain tetap."));
                row++;
            }
        var table = sheet.Range(4, 1, Math.Max(5, row - 1), headers.Length).CreateTable("PickupPointHistory");
        table.Theme = XLTableTheme.TableStyleMedium2;
        sheet.Column(1).Style.DateFormat.Format = "yyyy-mm-dd hh:mm";
        sheet.SheetView.FreezeRows(4); sheet.Columns().AdjustToContents(); sheet.Column(11).Width = 45;
        sheet.Column(11).Style.Alignment.WrapText = true;
    }

    private static string PointEventLabel(string kind, bool english) => kind switch
    {
        "Initial" => english ? "Initial selection" : "Pemilihan awal",
        "AutomaticRedraw" => english ? "Participant redraw" : "Pemilihan ulang peserta",
        "AdminReroll" => english ? "Admin reroll" : "Reroll Admin",
        "ParticipantsChanged" => english ? "Participant reconciliation" : "Rekonsiliasi peserta",
        "TransactionDeleted" => english ? "Transaction deleted" : "Transaksi dihapus",
        "Review" => english ? "Returned to review" : "Kembali ke review",
        _ => english ? "Pickup removed / method changed" : "Pickup dibatalkan / metode diganti"
    };

    private sealed record Labels(string Summary, string Details, string Pivot, string PickupRotation, string Generated, string Metric, string Value, string Transactions, string TotalBilled, string Paid, string Awaiting, string Unpaid, string Outstanding, string Date, string TransactionNumber, string Merchant, string Uploader, string Participant, string Username, string SplitMethod, string Menu, string Currency, string AmountDue, string ReportingCurrency, string ReportingAmount, string PaidAmount, string AwaitingAmount, string UnpaidAmount, string Status, string ClaimDate, string ResolutionDate, string ResolvedBy, string LastAction, string PickupPerson, string TransactionCount, string PaidPercentage, string Total, string Equal, string ByItem, string MemberSubmitted, string ModeratorConfirmed, string ModeratorRejected, string ModeratorMarkedPaid, string ModeratorReopened, string Legacy, string ParticipatedTransactions, string PickupCount, string PickupRatio, string LastPickupDate, string LastMerchant, string PickupStrategy, string WeightedRandom, string RoundRobin, string Points, string CurrentPoints, string PointHistory);

    private static readonly Labels IndonesianLabels = new("Ringkasan", "Detail Pembayaran", "Pivot per Orang", "Rotasi Pickup", "Dibuat", "Metrik", "Nilai", "Jumlah transaksi", "Total tagihan", "Sudah dibayar", "Menunggu konfirmasi", "Belum dibayar", "Outstanding", "Tanggal", "Nomor transaksi", "Merchant", "Uploader", "Nama", "Username", "Metode split", "Detail menu", "Mata uang", "Tagihan", "Mata uang laporan", "Nilai laporan", "Sudah dibayar", "Menunggu konfirmasi", "Belum dibayar", "Status", "Diajukan", "Diselesaikan", "Dikonfirmasi oleh", "Aksi terakhir", "Pengambil", "Jumlah transaksi", "% lunas", "Total", "Bagi rata", "Berdasarkan item", "Diajukan user", "Dikonfirmasi moderator", "Ditolak moderator", "Ditandai moderator", "Dibuka kembali", "Perubahan lama", "Transaksi diikuti", "Jumlah pickup", "Rasio pickup", "Tanggal pickup terakhir", "Merchant terakhir", "Metode pengambil", "Acak berbobot", "Round Robin", "Sistem Poin", "Poin saat ini (seluruh riwayat)", "Riwayat Poin");
    private static readonly Labels EnglishLabels = new("Summary", "Payment Details", "Pivot by Person", "Pickup Rotation", "Generated", "Metric", "Value", "Transactions", "Total billed", "Paid", "Awaiting confirmation", "Unpaid", "Outstanding", "Date", "Transaction number", "Merchant", "Uploader", "Participant", "Username", "Split method", "Menu detail", "Currency", "Amount due", "Reporting currency", "Reporting amount", "Paid amount", "Awaiting amount", "Unpaid amount", "Status", "Claim date", "Resolution date", "Resolved by", "Last action", "Pickup person", "Transaction count", "Paid percentage", "Total", "Equal split", "By item", "Submitted by member", "Confirmed by moderator", "Rejected by moderator", "Marked paid by moderator", "Reopened by moderator", "Legacy change", "Participated transactions", "Pickup count", "Pickup ratio", "Last pickup date", "Last merchant", "Pickup method", "Weighted Random", "Round Robin", "Points System", "Current points (all history)", "Points History");
}
