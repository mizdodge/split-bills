using System.ComponentModel.DataAnnotations;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.ViewModels;

public sealed class AdminCurrencyViewModel
{
    public string DefaultCurrencyCode { get; set; } = "IDR";
    public CurrencyProviderKind ProviderKind { get; set; } = CurrencyProviderKind.FrankfurterV2;
    [Required, Url] public string BaseUrl { get; set; } = "https://api.frankfurter.dev";
    public CurrencyAuthenticationMode AuthenticationMode { get; set; }
    [DataType(DataType.Password)] public string? ApiKey { get; set; }
    public bool HasStoredApiKey { get; set; }
    public bool AllowPrivateNetworkEndpoint { get; set; }
    public bool AutoRefreshEnabled { get; set; } = true;
    public bool DefaultCurrencyLocked { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public string? LastError { get; set; }
    public DateTimeOffset? LastRefreshAt { get; set; }
    public bool? LastRefreshSucceeded { get; set; }
    public string? LastRefreshError { get; set; }
    public IReadOnlyList<CurrencyInfo> Currencies { get; set; } = [];
}

public sealed class GuestBillViewModel
{
    public string Token { get; set; } = string.Empty;
    public bool WholeTransaction { get; set; }
    public bool IsTransactionToken { get; set; }
    public bool HasPersonalView { get; set; }
    public string MerchantName { get; set; } = string.Empty;
    public string TransactionNumber { get; set; } = string.Empty;
    public DateOnly? TransactionDate { get; set; }
    public TransactionStatus Status { get; set; }
    public string CurrencyCode { get; set; } = "IDR";
    public string ReportingCurrencyCode { get; set; } = "IDR";
    public decimal ExchangeRateToReporting { get; set; } = 1m;
    public DateOnly? ExchangeRateEffectiveDate { get; set; }
    public string ExchangeRateSource { get; set; } = "LegacyIdentity";
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal ServiceCharge { get; set; }
    public decimal GrandTotal { get; set; }
    public List<GuestItemViewModel> Items { get; set; } = [];
    public List<GuestParticipantViewModel> Participants { get; set; } = [];
    public List<string> ReceiptImageUrls { get; set; } = [];
    public string? PickupPerson { get; set; }
    public string? GuestParticipantName { get; set; }
}

public sealed record GuestItemViewModel(string Name, decimal Quantity, decimal Amount);
public sealed record GuestParticipantViewModel(string Name, decimal Amount, ParticipantPaymentStatus PaymentStatus, List<GuestItemViewModel> Items);
