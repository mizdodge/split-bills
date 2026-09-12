using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Http;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.ViewModels;

public sealed class PaymentClaimViewModel
{
    public long ParticipantId { get; set; }
    [Required] public IFormFile? ProofImage { get; set; }
}

public sealed class PaymentAttemptViewModel
{
    public long ApprovalId { get; set; }
    public PaymentApprovalStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    public string? Note { get; set; }
    public string? ProofOriginalFileName { get; set; }
    public bool HasProof { get; set; }
}

public sealed class MyBillItemDetailViewModel
{
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal Amount { get; set; }
}

public sealed class MyBillBreakdownItemViewModel
{
    public long ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal ReceiptAmount { get; set; }
    public decimal Amount { get; set; }
}

public sealed class MyBillBreakdownAdjustmentViewModel
{
    public string? Label { get; set; }
    public ChargeOperation Operation { get; set; }
    public decimal ReceiptAmount { get; set; }
    public decimal Amount { get; set; }
    public ParticipantAdjustmentKind Kind { get; set; }
    public decimal? ReceiptPercentage { get; set; }
    public decimal? ParticipantPercentage { get; set; }
}

public sealed class MyBillsViewModel
{
    public decimal UnpaidAmount { get; set; }
    public decimal AwaitingAmount { get; set; }
    public decimal PaidAmount { get; set; }
    public List<MyBillRowViewModel> Bills { get; set; } = [];
}

public sealed class MyBillRowViewModel
{
    public long ParticipantId { get; set; }
    public long TransactionId { get; set; }
    public string TransactionNumber { get; set; } = string.Empty;
    public string MerchantName { get; set; } = string.Empty;
    public DateOnly? TransactionDate { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "IDR";
    public ParticipantPaymentStatus PaymentStatus { get; set; }
    public string MenuDetail { get; set; } = string.Empty;
    public List<MyBillItemDetailViewModel> ItemDetails { get; set; } = [];
    public int AdditionalItemCount { get; set; }
    public List<PaymentAttemptViewModel> PaymentAttempts { get; set; } = [];
}

public sealed class MyBillDetailsViewModel
{
    public long ParticipantId { get; set; }
    public long TransactionId { get; set; }
    public string TransactionNumber { get; set; } = string.Empty;
    public string MerchantName { get; set; } = string.Empty;
    public DateOnly? TransactionDate { get; set; }
    public decimal Amount { get; set; }
    public string CurrencyCode { get; set; } = "IDR";
    public string ReportingCurrencyCode { get; set; } = "IDR";
    public decimal ExchangeRateToReporting { get; set; } = 1m;
    public DateOnly? ExchangeRateEffectiveDate { get; set; }
    public string ExchangeRateSource { get; set; } = "LegacyIdentity";
    public ParticipantPaymentStatus PaymentStatus { get; set; }
    public string MenuDetail { get; set; } = string.Empty;
    public decimal ItemSubtotal { get; set; }
    public decimal AdjustmentTotal { get; set; }
    public string? PickupPersonName { get; set; }
    public bool IsPickupPerson { get; set; }
    public decimal? PickupProbability { get; set; }
    public FoodPickupSelectionStrategy? PickupStrategy { get; set; }
    public DateTimeOffset? PickupSelectedAt { get; set; }
    public FoodPickupDrawKind? PickupDrawKind { get; set; }
    public List<MyBillBreakdownItemViewModel> Items { get; set; } = [];
    public List<MyBillBreakdownAdjustmentViewModel> Adjustments { get; set; } = [];
    public List<string> ReceiptImageUrls { get; set; } = [];
    public List<PaymentAttemptViewModel> PaymentAttempts { get; set; } = [];
}

public sealed class PaymentApprovalListViewModel
{
    public List<PaymentApprovalRowViewModel> Approvals { get; set; } = [];
}

public sealed class PaymentApprovalRowViewModel
{
    public long ApprovalId { get; set; }
    public long ParticipantId { get; set; }
    public long TransactionId { get; set; }
    public string TransactionNumber { get; set; } = string.Empty;
    public string MerchantName { get; set; } = string.Empty;
    public string ParticipantName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public string? ProofOriginalFileName { get; set; }
    public bool HasProof { get; set; }
}

public sealed class NotificationListViewModel
{
    public List<NotificationRowViewModel> Notifications { get; set; } = [];
    public WebPushStatusViewModel? WebPush { get; set; }
}

public sealed class NotificationRowViewModel
{
    public long Id { get; set; }
    public NotificationType Type { get; set; }
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string MerchantName { get; set; } = string.Empty;
    public string TransactionNumber { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public long? ParticipantId { get; set; }
    public long? PaymentApprovalId { get; set; }
    public string? RejectionReason { get; set; }
}
