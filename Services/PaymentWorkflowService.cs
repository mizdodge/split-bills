using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record PaymentWorkflowResult(bool Succeeded, string Message, long? TransactionId = null, string? MessageKey = null);

public interface IPaymentWorkflowService
{
    Task<PaymentWorkflowResult> SubmitClaimAsync(long participantId, string currentUserId, CancellationToken cancellationToken = default);
    Task<PaymentWorkflowResult> SubmitClaimAsync(long participantId, string currentUserId, StoredPaymentProof proof, CancellationToken cancellationToken = default);
    Task<PaymentWorkflowResult> ConfirmAsync(long participantId, string currentUserId, bool isAdmin, CancellationToken cancellationToken = default);
    Task<PaymentWorkflowResult> RejectAsync(long participantId, string currentUserId, bool isAdmin, string? note, CancellationToken cancellationToken = default);
    Task<PaymentWorkflowResult> MarkPaidAsync(long participantId, string currentUserId, bool isAdmin, CancellationToken cancellationToken = default);
    Task<PaymentWorkflowResult> ReopenAsync(long participantId, string currentUserId, bool isAdmin, string? note, CancellationToken cancellationToken = default);
}

public sealed class PaymentWorkflowService(
    ApplicationDbContext db,
    IWebPushOutboxService? webPushOutbox = null,
    ISharePointNotificationOutboxService? sharePointNotifications = null) : IPaymentWorkflowService
{
    public async Task<PaymentWorkflowResult> SubmitClaimAsync(long participantId, string currentUserId, CancellationToken cancellationToken = default)
        => await SubmitClaimCoreAsync(participantId, currentUserId, null, requireProof: false, cancellationToken);

    public async Task<PaymentWorkflowResult> SubmitClaimAsync(long participantId, string currentUserId, StoredPaymentProof proof, CancellationToken cancellationToken = default)
        => await SubmitClaimCoreAsync(participantId, currentUserId, proof, requireProof: true, cancellationToken);

    private async Task<PaymentWorkflowResult> SubmitClaimCoreAsync(long participantId, string currentUserId, StoredPaymentProof? proof, bool requireProof, CancellationToken cancellationToken)
    {
        await using var scope = await db.Database.BeginTransactionAsync(cancellationToken);
        var participant = await LoadParticipantAsync(participantId, cancellationToken);
        if (participant is null || participant.AccountLink?.UserId != currentUserId)
            return Failure("Tagihan tidak ditemukan.", "BillNotFound");
        if (participant.PaymentStatus != ParticipantPaymentStatus.Unpaid)
            return Failure("Tagihan ini tidak sedang menunggu pembayaran.", "PaymentNotWaiting");
        if (requireProof && proof is null)
            return Failure("Bukti pembayaran wajib diunggah.", "InvalidPaymentProof");
        if (participant.PaymentApprovals.Any(x => x.Status == PaymentApprovalStatus.Pending))
            return Failure("Pembayaran ini sudah menunggu konfirmasi.", "PaymentAlreadyAwaiting");

        var now = DateTimeOffset.UtcNow;
        participant.PaymentStatus = ParticipantPaymentStatus.AwaitingConfirmation;
        participant.PaymentHistories.Add(new PaymentHistory
        {
            PreviousStatus = ParticipantPaymentStatus.Unpaid,
            NewStatus = ParticipantPaymentStatus.AwaitingConfirmation,
            ChangedByUserId = currentUserId,
            ChangedAt = now,
            ActionType = PaymentActionType.MemberSubmitted
        });
        var approval = new PaymentApproval
        {
            RequestedByUserId = currentUserId,
            RequestedAt = now,
            Status = PaymentApprovalStatus.Pending,
            ProofFileName = proof?.FileName,
            ProofOriginalFileName = proof?.OriginalFileName,
            ProofContentType = proof?.ContentType
        };
        participant.PaymentApprovals.Add(approval);
        await AddOwnerNotificationAsync(participant, NotificationType.PaymentSubmitted, now, cancellationToken, approval);
        if (sharePointNotifications is not null)
            await sharePointNotifications.EnqueuePaymentApprovalRequestedAsync(participant, approval, currentUserId, cancellationToken);
        await SaveAndCommitAsync(participant, scope, cancellationToken);
        return Success("Pembayaran dikirim dan menunggu konfirmasi.", participant.TransactionId, "PaymentSubmittedSuccess");
    }

    public Task<PaymentWorkflowResult> ConfirmAsync(long participantId, string currentUserId, bool isAdmin, CancellationToken cancellationToken = default) =>
        ResolveClaimAsync(participantId, currentUserId, isAdmin, approve: true, null, cancellationToken);

    public Task<PaymentWorkflowResult> RejectAsync(long participantId, string currentUserId, bool isAdmin, string? note, CancellationToken cancellationToken = default) =>
        ResolveClaimAsync(participantId, currentUserId, isAdmin, approve: false, note, cancellationToken);

    public async Task<PaymentWorkflowResult> MarkPaidAsync(long participantId, string currentUserId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        await using var scope = await db.Database.BeginTransactionAsync(cancellationToken);
        var participant = await LoadParticipantAsync(participantId, cancellationToken);
        if (!CanManage(participant, currentUserId, isAdmin)) return Failure("Tagihan tidak ditemukan.", "BillNotFound");
        if (participant!.PaymentStatus == ParticipantPaymentStatus.Paid) return Success("Pembayaran sudah ditandai Paid.", participant.TransactionId, "PaymentAlreadyPaid");
        if (participant.PaymentStatus == ParticipantPaymentStatus.AwaitingConfirmation)
            return await ResolveClaimCoreAsync(participant, currentUserId, approve: true, note: null, scope, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        ApplyPaid(participant, currentUserId, now, PaymentActionType.ModeratorMarkedPaid);
        await NotifyLinkedUserAsync(participant, NotificationType.PaymentApproved, now);
        await SaveAndCommitAsync(participant, scope, cancellationToken);
        return Success("Pembayaran ditandai Paid.", participant.TransactionId, "PaymentMarkedPaid");
    }

    public async Task<PaymentWorkflowResult> ReopenAsync(long participantId, string currentUserId, bool isAdmin, string? note, CancellationToken cancellationToken = default)
    {
        await using var scope = await db.Database.BeginTransactionAsync(cancellationToken);
        var participant = await LoadParticipantAsync(participantId, cancellationToken);
        if (!CanManage(participant, currentUserId, isAdmin)) return Failure("Tagihan tidak ditemukan.", "BillNotFound");
        if (participant!.PaymentStatus != ParticipantPaymentStatus.Paid) return Failure("Pembayaran ini belum berstatus Paid.", "PaymentNotPaid");

        var now = DateTimeOffset.UtcNow;
        participant.PaymentHistories.Add(new PaymentHistory
        {
            PreviousStatus = ParticipantPaymentStatus.Paid,
            NewStatus = ParticipantPaymentStatus.Unpaid,
            ChangedByUserId = currentUserId,
            ChangedAt = now,
            ActionType = PaymentActionType.ModeratorReopened,
            Note = NormalizeNote(note)
        });
        participant.PaymentStatus = ParticipantPaymentStatus.Unpaid;
        participant.PaidAt = null;
        participant.MarkedPaidByUserId = null;
        await NotifyLinkedUserAsync(participant, NotificationType.PaymentReopened, now);
        await SaveAndCommitAsync(participant, scope, cancellationToken);
        return Success("Pembayaran dikembalikan ke status Unpaid.", participant.TransactionId, "PaymentReopenedSuccess");
    }

    private async Task<PaymentWorkflowResult> ResolveClaimAsync(long participantId, string currentUserId, bool isAdmin, bool approve, string? note, CancellationToken cancellationToken)
    {
        await using var scope = await db.Database.BeginTransactionAsync(cancellationToken);
        var participant = await LoadParticipantAsync(participantId, cancellationToken);
        if (!CanManage(participant, currentUserId, isAdmin)) return Failure("Tagihan tidak ditemukan.", "BillNotFound");
        if (participant!.PaymentStatus != ParticipantPaymentStatus.AwaitingConfirmation)
            return Failure("Tidak ada pembayaran yang menunggu konfirmasi.", "NoPaymentAwaiting");
        return await ResolveClaimCoreAsync(participant, currentUserId, approve, note, scope, cancellationToken);
    }

    private async Task<PaymentWorkflowResult> ResolveClaimCoreAsync(TransactionParticipant participant, string currentUserId, bool approve, string? note, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction scope, CancellationToken cancellationToken)
    {
        var pending = participant.PaymentApprovals.FirstOrDefault(x => x.Status == PaymentApprovalStatus.Pending);
        if (pending is null) return Failure("Permintaan pembayaran sudah diproses.", "PaymentRequestProcessed");
        if (!approve && NormalizeNote(note) is null) return Failure("Alasan penolakan wajib diisi.", "RejectReasonRequired");
        var now = DateTimeOffset.UtcNow;
        pending.Status = approve ? PaymentApprovalStatus.Approved : PaymentApprovalStatus.Rejected;
        pending.ResolvedByUserId = currentUserId;
        pending.ResolvedAt = now;
        pending.Note = NormalizeNote(note);

        var previous = participant.PaymentStatus;
        var action = approve ? PaymentActionType.ModeratorConfirmed : PaymentActionType.ModeratorRejected;
        participant.PaymentHistories.Add(new PaymentHistory
        {
            PreviousStatus = previous,
            NewStatus = approve ? ParticipantPaymentStatus.Paid : ParticipantPaymentStatus.Unpaid,
            ChangedByUserId = currentUserId,
            ChangedAt = now,
            ActionType = action,
            Note = NormalizeNote(note)
        });
        if (approve) ApplyPaid(participant, currentUserId, now, null);
        else
        {
            participant.PaymentStatus = ParticipantPaymentStatus.Unpaid;
            participant.PaidAt = null;
            participant.MarkedPaidByUserId = null;
        }
        await NotifyLinkedUserAsync(participant, approve ? NotificationType.PaymentApproved : NotificationType.PaymentRejected, now, pending);
        if (!approve && sharePointNotifications is not null)
            await sharePointNotifications.EnqueuePaymentRejectedAsync(participant, pending, currentUserId, cancellationToken);
        await SaveAndCommitAsync(participant, scope, cancellationToken);
        return Success(approve ? "Pembayaran dikonfirmasi." : "Pembayaran ditolak dan dikembalikan ke Unpaid.", participant.TransactionId, approve ? "PaymentConfirmedSuccess" : "PaymentRejectedSuccess");
    }

    private async Task<TransactionParticipant?> LoadParticipantAsync(long participantId, CancellationToken cancellationToken) =>
        await db.TransactionParticipants
            .Include(x => x.Transaction).ThenInclude(x => x!.Participants)
            .Include(x => x.AccountLink)
            .Include(x => x.PaymentApprovals)
            .Include(x => x.PaymentHistories)
            .SingleOrDefaultAsync(x => x.Id == participantId, cancellationToken);

    private static bool CanManage(TransactionParticipant? participant, string currentUserId, bool isAdmin) =>
        participant?.Transaction is not null && TransactionAccessService.CanManage(isAdmin, currentUserId, participant.Transaction.UploadedByUserId);

    private async Task AddOwnerNotificationAsync(TransactionParticipant participant, NotificationType type, DateTimeOffset now, CancellationToken cancellationToken, PaymentApproval? approval = null)
    {
        var ownerId = participant.Transaction?.UploadedByUserId;
        if (string.IsNullOrWhiteSpace(ownerId)) return;
        var notification = new UserNotification { UserId = ownerId, ParticipantId = participant.Id, PaymentApproval = approval, Type = type, CreatedAt = now, IsRead = false };
        db.UserNotifications.Add(notification);
        if (webPushOutbox is not null)
            await webPushOutbox.EnqueueAsync(notification, now, cancellationToken);
    }

    private Task NotifyLinkedUserAsync(TransactionParticipant participant, NotificationType type, DateTimeOffset now, PaymentApproval? approval = null)
    {
        var userId = participant.AccountLink?.UserId;
        if (!string.IsNullOrWhiteSpace(userId))
            db.UserNotifications.Add(new UserNotification { UserId = userId, ParticipantId = participant.Id, PaymentApproval = approval, Type = type, CreatedAt = now, IsRead = false });
        return Task.CompletedTask;
    }

    private static void ApplyPaid(TransactionParticipant participant, string currentUserId, DateTimeOffset now, PaymentActionType? action)
    {
        if (action.HasValue)
            participant.PaymentHistories.Add(new PaymentHistory
            {
                PreviousStatus = participant.PaymentStatus,
                NewStatus = ParticipantPaymentStatus.Paid,
                ChangedByUserId = currentUserId,
                ChangedAt = now,
                ActionType = action.Value
            });
        participant.PaymentStatus = ParticipantPaymentStatus.Paid;
        participant.PaidAt = now;
        participant.MarkedPaidByUserId = currentUserId;
        if (participant.Transaction is not null)
            participant.Transaction.Status = TransactionStatusService.Calculate(participant.Transaction.Participants.Select(x => x.Id == participant.Id ? ParticipantPaymentStatus.Paid : x.PaymentStatus));
        participant.Transaction!.UpdatedAt = now;
    }

    private async Task SaveAndCommitAsync(TransactionParticipant participant, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction scope, CancellationToken cancellationToken)
    {
        if (participant.Transaction is not null)
            participant.Transaction.Status = TransactionStatusService.Calculate(participant.Transaction.Participants.Select(x => x.PaymentStatus));
        if (participant.Transaction is not null) participant.Transaction.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await scope.CommitAsync(cancellationToken);
    }

    private static string? NormalizeNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim()[..Math.Min(note.Trim().Length, 500)];
    private static PaymentWorkflowResult Success(string message, long transactionId, string? messageKey = null) => new(true, message, transactionId, messageKey);
    private static PaymentWorkflowResult Failure(string message, string? messageKey = null) => new(false, message, null, messageKey);
}
