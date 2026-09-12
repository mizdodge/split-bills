using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.ViewModels;

public sealed class DashboardViewModel
{
    public bool IsMemberDashboard { get; set; }
    public int TotalTransactions { get; set; }
    public int UnpaidTransactions { get; set; }
    public int PartialTransactions { get; set; }
    public int PaidTransactions { get; set; }
    public decimal OutstandingAmount { get; set; }
    public List<BillTransaction> RecentTransactions { get; set; } = [];
    public List<DashboardOutstandingPersonViewModel> TopOutstandingPeople { get; set; } = [];
    public DashboardOldestOutstandingViewModel? OldestOutstanding { get; set; }
    public List<DashboardMonthlySpendViewModel> MonthlySpending { get; set; } = [];
    public List<DashboardMerchantViewModel> TopMerchants { get; set; } = [];
    public decimal? AverageSettlementHours { get; set; }
    public int SettledTransactionCount { get; set; }
    public decimal MemberCurrentMonthAmount { get; set; }
    public decimal MemberPreviousMonthAmount { get; set; }
    public decimal MemberPaidAmount { get; set; }
    public List<DashboardMemberMonthViewModel> MemberMonthlySpending { get; set; } = [];
    public List<DashboardMemberBillViewModel> RecentMemberBills { get; set; } = [];
    public List<DashboardRateHistoryViewModel> RateHistory { get; set; } = [];
    public List<DashboardCurrencyTrendViewModel> CurrencyTrends { get; set; } = [];
    public DashboardCurrentCurrencySectionViewModel? CurrentCurrencyRates { get; set; }
}

public sealed record DashboardOutstandingPersonViewModel(string Name, decimal Amount, int BillCount);

public sealed record DashboardOldestOutstandingViewModel(
    long TransactionId,
    string MerchantName,
    string TransactionNumber,
    DateOnly Date,
    int AgeDays,
    decimal Amount);

public sealed record DashboardMonthlySpendViewModel(DateOnly Month, decimal Amount, decimal BarPercent);

public sealed record DashboardMerchantViewModel(string Name, int TransactionCount, decimal TotalAmount);

public sealed record DashboardMemberMonthViewModel(
    DateOnly Month,
    decimal TotalAmount,
    decimal PaidAmount,
    decimal OutstandingAmount,
    decimal BarPercent);

public sealed record DashboardMemberBillViewModel(
    long ParticipantId,
    string MerchantName,
    string TransactionNumber,
    DateOnly Date,
    decimal Amount,
    ParticipantPaymentStatus PaymentStatus);

public sealed record DashboardRateHistoryViewModel(string BaseCurrency, string ReportingCurrency, decimal Rate, DateOnly EffectiveDate, bool IsStale);

public sealed record DashboardCurrencyTrendViewModel(
    string BaseCurrency,
    string ReportingCurrency,
    decimal LatestRate,
    DateOnly StartDate,
    DateOnly LatestDate,
    decimal? ChangeFromPreviousPercent,
    bool IsStale,
    int PointCount,
    string SvgPoints);

public sealed class DashboardCurrentCurrencySectionViewModel
{
    public string ReportingCurrency { get; init; } = "IDR";
    public string PrimaryCurrencyCode { get; init; } = "USD";
    public List<string> SelectedCurrencyCodes { get; init; } = [];
    public List<CurrencyInfo> AvailableCurrencies { get; init; } = [];
    public List<DashboardCurrentCurrencyRateViewModel> Rates { get; init; } = [];
    public DateTimeOffset? LastUpdatedAt { get; init; }
}

public sealed record DashboardCurrentCurrencyRateViewModel(
    string BaseCurrency,
    string ReportingCurrency,
    string CurrencyName,
    string FormattedRate,
    DateOnly StartDate,
    DateOnly LatestDate,
    decimal? ChangeFromPreviousPercent,
    bool IsStale,
    int PointCount,
    string SvgPoints,
    bool IsPrimary);

public sealed class DashboardCurrencyPreferencesInput
{
    public List<string> CurrencyCodes { get; set; } = [];
    public string PrimaryCurrencyCode { get; set; } = string.Empty;
}

public sealed class DashboardRateHistoryPageViewModel
{
    public string ReportingCurrency { get; init; } = "IDR";
    public List<DashboardRateHistoryViewModel> Rows { get; init; } = [];
    public int Page { get; init; }
    public int PageSize { get; init; }
    public int TotalCount { get; init; }
}

public sealed class ReportViewModel
{
    public DateTime? From { get; set; }
    public DateTime? To { get; set; }
    public TransactionStatus? Status { get; set; }
    public string? UploaderId { get; set; }
    public bool IsAdmin { get; set; }
    public int TotalTransactions { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal AwaitingAmount { get; set; }
    public decimal UnpaidAmount { get; set; }
    public decimal OutstandingAmount { get; set; }
    public List<ReportTransactionRowViewModel> Transactions { get; set; } = [];
    public List<ReportPickupPersonRowViewModel> PickupPeople { get; set; } = [];
    public List<ApplicationUser> Uploaders { get; set; } = [];
    public bool IsMember { get; set; }
}

public sealed class ReportTransactionRowViewModel
{
    public long Id { get; set; }
    public string TransactionNumber { get; set; } = string.Empty;
    public string MerchantName { get; set; } = string.Empty;
    public DateOnly EffectiveDate { get; set; }
    public string? UploaderName { get; set; }
    public decimal GrandTotal { get; set; }
    public decimal PaidAmount { get; set; }
    public decimal AwaitingAmount { get; set; }
    public decimal UnpaidAmount { get; set; }
    public TransactionStatus Status { get; set; }
    public int ParticipantCount { get; set; }
    public string? PickupPersonName { get; set; }
}

public sealed class ReportPickupPersonRowViewModel
{
    public string ParticipantName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public int ParticipationCount { get; set; }
    public int PickupCount { get; set; }
    public decimal PickupRatio { get; set; }
    public DateTimeOffset? LastPickupAt { get; set; }
    public string? LastPickupMerchant { get; set; }
    public bool? IsEligible { get; set; }
}

public sealed class ReportDetailsViewModel
{
    public ReportTransactionProjection Transaction { get; set; } = null!;
}
