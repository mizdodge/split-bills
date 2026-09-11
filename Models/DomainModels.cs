using System.ComponentModel.DataAnnotations;

namespace Splitbill.Models;

public enum TransactionStatus { Draft, Unpaid, Partial, Paid }
// Keep the existing SQLite values stable: Unpaid=0 and Paid=1 already exist in live data.
public enum ParticipantPaymentStatus { Unpaid = 0, Paid = 1, AwaitingConfirmation = 2 }
public enum SplitMethod { Equal, ByItem }
public enum AiProvider { AzureOpenAi, OpenAi }
public enum AiApiMode { Responses, ChatCompletions }
public enum AiProcessingStatus { Processing, Succeeded, Failed }
public enum ChargeOperation { Add, Subtract }
public enum PaymentActionType { LegacyStatusChange = 0, MemberSubmitted = 1, ModeratorConfirmed = 2, ModeratorRejected = 3, ModeratorMarkedPaid = 4, ModeratorReopened = 5 }
public enum PaymentApprovalStatus { Pending = 0, Approved = 1, Rejected = 2 }
public enum NotificationType { BillAssigned = 0, PaymentSubmitted = 1, PaymentApproved = 2, PaymentRejected = 3, PaymentReopened = 4, PushTest = 5 }
public enum WebPushDeliveryStatus { Pending = 0, Processing = 1, Sent = 2, DeadLetter = 3, Cancelled = 4 }
public enum SharePointNotificationEventType { BillAssigned = 0, PaymentApprovalRequested = 1, PaymentRejected = 2, FoodPickupSelected = 3 }
public enum SharePointNotificationStatus { Pending = 0, Processing = 1, Sent = 2, DeadLetter = 3 }
public enum FoodPickupDrawKind { Initial = 0, AutomaticRedraw = 1, AdminReroll = 2 }
public enum FoodPickupSelectionStrategy { WeightedRandom = 0, RoundRobin = 1 }

public enum AdminUserAuditAction
{
    Created = 0,
    ProfileUpdated = 1,
    RoleChanged = 2,
    PasswordReset = 3,
    Disabled = 4,
    Enabled = 5
}

/// <summary>Singleton installation metadata used by the one-time first-admin setup.</summary>
public sealed class InstallationState
{
    public int Id { get; set; } = 1;
    [MaxLength(64)] public string InstallationId { get; set; } = string.Empty;
    [MaxLength(128)] public string? BootstrapCodeHash { get; set; }
    [MaxLength(1000)] public string? ProtectedBootstrapCode { get; set; }
    public DateTimeOffset? BootstrapCodeExpiresAt { get; set; }
    public DateTimeOffset? SetupCompletedAt { get; set; }
}

public sealed class AiConfiguration
{
    public int Id { get; set; }
    public AiProvider Provider { get; set; } = AiProvider.OpenAi;
    [MaxLength(500)] public string? Endpoint { get; set; }
    public string? ProtectedApiKey { get; set; }
    [MaxLength(100)] public string Model { get; set; } = "gpt-5.6-luna";
    [MaxLength(100)] public string? DeploymentName { get; set; }
    [MaxLength(50)] public string? ApiVersion { get; set; }
    public AiApiMode ApiMode { get; set; } = AiApiMode.Responses;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset? LastTestAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }
}

/// <summary>
/// Singleton configuration for discovering a SharePoint site and list.
/// Transaction synchronization is intentionally implemented in a later phase.
/// </summary>
public sealed class SharePointConfiguration
{
    public int Id { get; set; }
    public bool Enabled { get; set; }
    [MaxLength(36)] public string TenantId { get; set; } = string.Empty;
    [MaxLength(36)] public string ClientId { get; set; } = string.Empty;
    public string ProtectedClientSecret { get; set; } = string.Empty;
    [MaxLength(500)] public string SiteUrl { get; set; } = string.Empty;
    [MaxLength(500)] public string SiteId { get; set; } = string.Empty;
    [MaxLength(200)] public string SiteDisplayName { get; set; } = string.Empty;
    [MaxLength(100)] public string ListId { get; set; } = string.Empty;
    [MaxLength(200)] public string ListDisplayName { get; set; } = string.Empty;
    [MaxLength(500)] public string? ListWebUrl { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    [MaxLength(1000)] public string? LastError { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }
}

/// <summary>
/// Durable delivery queue for notification rows consumed by Power Automate.
/// Business transactions commit this row locally; a background worker retries Graph independently.
/// </summary>
public sealed class SharePointNotificationOutbox
{
    public long Id { get; set; }
    [MaxLength(100)] public string EventId { get; set; } = string.Empty;
    public SharePointNotificationEventType EventType { get; set; }
    [MaxLength(320)] public string RecipientEmail { get; set; } = string.Empty;
    [MaxLength(100)] public string RecipientName { get; set; } = string.Empty;
    [MaxLength(100)] public string ActorName { get; set; } = string.Empty;
    [MaxLength(200)] public string Title { get; set; } = string.Empty;
    [MaxLength(4000)] public string Description { get; set; } = string.Empty;
    public long TransactionId { get; set; }
    [MaxLength(30)] public string TransactionNumber { get; set; } = string.Empty;
    [MaxLength(200)] public string MerchantName { get; set; } = string.Empty;
    public long ParticipantId { get; set; }
    public long? ApprovalId { get; set; }
    public decimal Amount { get; set; }
    public SharePointNotificationStatus Status { get; set; } = SharePointNotificationStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    [MaxLength(160)] public string? LastErrorCode { get; set; }
}

/// <summary>Singleton switch and strategy for the optional pickup rotation.</summary>
public sealed class FoodPickupConfiguration
{
    public int Id { get; set; } = 1;
    public bool Enabled { get; set; }
    public FoodPickupSelectionStrategy Strategy { get; set; } = FoodPickupSelectionStrategy.WeightedRandom;
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedByUserId { get; set; }
}

/// <summary>Accounts currently eligible for new pickup draws.</summary>
public sealed class FoodPickupEligibleUser
{
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public DateTimeOffset EnabledAt { get; set; }
    public string EnabledByUserId { get; set; } = string.Empty;
}

/// <summary>The one current pickup winner for a transaction.</summary>
public sealed class FoodPickupAssignment
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    public string SelectedUserId { get; set; } = string.Empty;
    public ApplicationUser? SelectedUser { get; set; }
    public long SelectedParticipantId { get; set; }
    public TransactionParticipant? SelectedParticipant { get; set; }
    public decimal RecordedProbability { get; set; }
    public FoodPickupSelectionStrategy Strategy { get; set; } = FoodPickupSelectionStrategy.WeightedRandom;
    public FoodPickupDrawKind DrawKind { get; set; }
    public DateTimeOffset SelectedAt { get; set; }
    public string? SelectedByUserId { get; set; }
}

/// <summary>Immutable record of every initial draw, redraw, and reroll.</summary>
public sealed class FoodPickupDrawHistory
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    public int SequenceNumber { get; set; }
    public string? PreviousSelectedUserId { get; set; }
    public string SelectedUserId { get; set; } = string.Empty;
    public long SelectedParticipantId { get; set; }
    public FoodPickupSelectionStrategy Strategy { get; set; } = FoodPickupSelectionStrategy.WeightedRandom;
    public FoodPickupDrawKind DrawKind { get; set; }
    [MaxLength(500)] public string? Reason { get; set; }
    [MaxLength(12000)] public string CandidateSnapshotJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public string? CreatedByUserId { get; set; }
}

public sealed class AdminUserAuditLog
{
    public long Id { get; set; }
    public string ActorUserId { get; set; } = string.Empty;
    public string TargetUserId { get; set; } = string.Empty;
    public AdminUserAuditAction Action { get; set; }
    [MaxLength(2000)] public string? SummaryJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class BillTransaction
{
    public long Id { get; set; }
    [MaxLength(30)] public string TransactionNumber { get; set; } = string.Empty;
    [MaxLength(200)] public string MerchantName { get; set; } = string.Empty;
    public DateOnly? TransactionDate { get; set; }
    public DateTimeOffset UploadDate { get; set; }
    public string UploadedByUserId { get; set; } = string.Empty;
    public ApplicationUser? UploadedByUser { get; set; }
    public decimal Subtotal { get; set; }
    public decimal Discount { get; set; }
    public decimal Tax { get; set; }
    public decimal ServiceCharge { get; set; }
    public decimal GrandTotal { get; set; }
    [MaxLength(500)] public string ReceiptImagePath { get; set; } = string.Empty;
    public TransactionStatus Status { get; set; } = TransactionStatus.Draft;
    public SplitMethod SplitMethod { get; set; } = SplitMethod.Equal;
    /// <summary>Whether this transaction asks the configured pickup rotation to choose a collector.</summary>
    public bool RequiresFoodPickup { get; set; }
    public decimal AiConfidence { get; set; }
    public bool AiNeedsReview { get; set; }
    public string? AiWarningsJson { get; set; }
    public string? AiRawResponseJson { get; set; }
    /// <summary>Canonical item allocation groups for reopening/editing a saved split.</summary>
    public string? AllocationPlanJson { get; set; }
    /// <summary>Safe scheme/host/path-base captured when the uploader saved the split.</summary>
    [MaxLength(500)] public string? NotificationBaseUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<TransactionItem> Items { get; set; } = [];
    public List<TransactionReceiptImage> ReceiptImages { get; set; } = [];
    public List<TransactionCharge> Charges { get; set; } = [];
    public List<TransactionParticipant> Participants { get; set; } = [];
    public FoodPickupAssignment? PickupAssignment { get; set; }
    public List<FoodPickupDrawHistory> PickupDrawHistories { get; set; } = [];
    public List<ReceiptProcessingLog> ProcessingLogs { get; set; } = [];
}

public sealed class TransactionReceiptImage
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    [MaxLength(500)] public string FileName { get; set; } = string.Empty;
    [MaxLength(100)] public string ContentType { get; set; } = string.Empty;
    public int SortOrder { get; set; }
}

public sealed class TransactionCharge
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    [MaxLength(100)] public string Label { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public ChargeOperation Operation { get; set; } = ChargeOperation.Add;
    public int SortOrder { get; set; }
}

public sealed class TransactionItem
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    public int LineNumber { get; set; }
    [MaxLength(200)] public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal TotalPrice { get; set; }
    public decimal Confidence { get; set; }
    public List<ParticipantItemAllocation> ParticipantAllocations { get; set; } = [];
}

public sealed class TransactionParticipant
{
    public long Id { get; set; }
    public long TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    [MaxLength(100)] public string Name { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public ParticipantPaymentStatus PaymentStatus { get; set; } = ParticipantPaymentStatus.Unpaid;
    public DateTimeOffset? PaidAt { get; set; }
    public string? MarkedPaidByUserId { get; set; }
    public ParticipantAccountLink? AccountLink { get; set; }
    public List<ParticipantItemAllocation> ItemAllocations { get; set; } = [];
    public List<PaymentHistory> PaymentHistories { get; set; } = [];
    public List<PaymentApproval> PaymentApprovals { get; set; } = [];
}

public sealed class ParticipantAccountLink
{
    public long ParticipantId { get; set; }
    public TransactionParticipant Participant { get; set; } = null!;
    public string UserId { get; set; } = string.Empty;
    public DateTimeOffset LinkedAt { get; set; }
}

public sealed class PaymentApproval
{
    public long Id { get; set; }
    public long ParticipantId { get; set; }
    public TransactionParticipant? Participant { get; set; }
    public string RequestedByUserId { get; set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; set; }
    public PaymentApprovalStatus Status { get; set; } = PaymentApprovalStatus.Pending;
    public string? ResolvedByUserId { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
    [MaxLength(500)] public string? Note { get; set; }
    [MaxLength(500)] public string? ProofFileName { get; set; }
    [MaxLength(255)] public string? ProofOriginalFileName { get; set; }
    [MaxLength(100)] public string? ProofContentType { get; set; }
}

public sealed class UserNotification
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public long? ParticipantId { get; set; }
    public TransactionParticipant? Participant { get; set; }
    public long? PaymentApprovalId { get; set; }
    public PaymentApproval? PaymentApproval { get; set; }
    public NotificationType Type { get; set; }
    public bool IsRead { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<WebPushDelivery> WebPushDeliveries { get; set; } = [];
}

public sealed class WebPushConfiguration
{
    public int Id { get; set; }
    public bool Enabled { get; set; } = true;
    [MaxLength(500)] public string Subject { get; set; } = "https://splitbill.local";
    [MaxLength(200)] public string PublicKey { get; set; } = string.Empty;
    [MaxLength(1000)] public string ProtectedPrivateKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class WebPushSubscription
{
    public long Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    [MaxLength(64)] public string EndpointHash { get; set; } = string.Empty;
    [MaxLength(5000)] public string ProtectedEndpoint { get; set; } = string.Empty;
    [MaxLength(500)] public string ProtectedP256dh { get; set; } = string.Empty;
    [MaxLength(500)] public string ProtectedAuth { get; set; } = string.Empty;
    [MaxLength(64)] public string InstallationIdHash { get; set; } = string.Empty;
    [MaxLength(10)] public string Culture { get; set; } = "id-ID";
    [MaxLength(160)] public string? BrowserLabel { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? DisabledAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }
    public List<WebPushDelivery> Deliveries { get; set; } = [];
}

public sealed class WebPushDelivery
{
    public long Id { get; set; }
    public long UserNotificationId { get; set; }
    public UserNotification? UserNotification { get; set; }
    public long WebPushSubscriptionId { get; set; }
    public WebPushSubscription? WebPushSubscription { get; set; }
    public WebPushDeliveryStatus Status { get; set; } = WebPushDeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset? LockedUntil { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? SentAt { get; set; }
    [MaxLength(120)] public string? LastErrorCode { get; set; }
}

public sealed class ParticipantItemAllocation
{
    public long Id { get; set; }
    public long ParticipantId { get; set; }
    public TransactionParticipant? Participant { get; set; }
    public long TransactionItemId { get; set; }
    public TransactionItem? Item { get; set; }
    public decimal QuantityShare { get; set; }
    public decimal Amount { get; set; }
}

public sealed class PaymentHistory
{
    public long Id { get; set; }
    public long ParticipantId { get; set; }
    public TransactionParticipant? Participant { get; set; }
    public ParticipantPaymentStatus PreviousStatus { get; set; }
    public ParticipantPaymentStatus NewStatus { get; set; }
    public string ChangedByUserId { get; set; } = string.Empty;
    public DateTimeOffset ChangedAt { get; set; }
    public PaymentActionType ActionType { get; set; } = PaymentActionType.LegacyStatusChange;
    [MaxLength(500)] public string? Note { get; set; }
}

public sealed class ReceiptProcessingLog
{
    public long Id { get; set; }
    public long? TransactionId { get; set; }
    public BillTransaction? Transaction { get; set; }
    public AiProvider Provider { get; set; }
    [MaxLength(100)] public string Model { get; set; } = string.Empty;
    public AiApiMode ApiMode { get; set; }
    public AiProcessingStatus Status { get; set; }
    [MaxLength(1000)] public string? ErrorMessage { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}
