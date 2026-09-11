using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.ViewModels;

namespace Splitbill.Services;

public sealed class ReportQueryResult
{
    public List<ReportTransactionProjection> Transactions { get; init; } = [];
    public IEnumerable<ReportParticipantProjection> Participants => Transactions.SelectMany(x => x.Participants);
    public int TotalTransactions => Transactions.Count;
    public decimal TotalAmount => Participants.Sum(x => x.AmountDue);
    public decimal PaidAmount => Participants.Sum(x => x.PaidAmount);
    public decimal AwaitingAmount => Participants.Sum(x => x.AwaitingAmount);
    public decimal UnpaidAmount => Participants.Sum(x => x.UnpaidAmount);
    public decimal OutstandingAmount => AwaitingAmount + UnpaidAmount;
}

public sealed class ReportTransactionProjection
{
    public long Id { get; init; }
    public string TransactionNumber { get; init; } = string.Empty;
    public string MerchantName { get; init; } = string.Empty;
    public DateOnly EffectiveDate { get; init; }
    public string? UploaderName { get; init; }
    public TransactionStatus Status { get; init; }
    public SplitMethod SplitMethod { get; init; }
    public decimal GrandTotal { get; init; }
    public decimal Subtotal { get; init; }
    public decimal Discount { get; init; }
    public decimal Tax { get; init; }
    public decimal ServiceCharge { get; init; }
    public string? PickupPersonName { get; init; }
    public decimal? PickupProbability { get; init; }
    public FoodPickupSelectionStrategy? PickupStrategy { get; init; }
    public DateTimeOffset? PickupSelectedAt { get; init; }
    public FoodPickupDrawKind? PickupDrawKind { get; init; }
    public List<TransactionItem> Items { get; init; } = [];
    public List<TransactionCharge> Charges { get; init; } = [];
    public List<ReportParticipantProjection> Participants { get; init; } = [];
}

public sealed class ReportParticipantProjection
{
    public long ParticipantId { get; init; }
    public long TransactionId { get; init; }
    public string TransactionNumber { get; init; } = string.Empty;
    public string MerchantName { get; init; } = string.Empty;
    public DateOnly EffectiveDate { get; init; }
    public string? UploaderName { get; init; }
    public string ParticipantName { get; init; } = string.Empty;
    public string? Username { get; init; }
    public string MenuDetail { get; init; } = string.Empty;
    public IReadOnlyList<ParticipantMenuFormatter.ItemDetail> ItemDetails { get; init; } = [];
    public decimal AmountDue { get; init; }
    public decimal PaidAmount { get; init; }
    public decimal AwaitingAmount { get; init; }
    public decimal UnpaidAmount { get; init; }
    public ParticipantPaymentStatus PaymentStatus { get; init; }
    public DateTimeOffset? ClaimDate { get; init; }
    public DateTimeOffset? ResolutionDate { get; init; }
    public string? ResolvedBy { get; init; }
    public PaymentActionType LastAction { get; init; }
    public bool IsPickupPerson { get; init; }
    public string? PickupPersonName { get; init; }
    public decimal? PickupProbability { get; init; }
    public DateTimeOffset? PickupSelectedAt { get; init; }
}

public interface IReportQueryService
{
    Task<ReportQueryResult> QueryAsync(ClaimsPrincipal principal, DateOnly? from, DateOnly? to, TransactionStatus? status, string? uploaderId, CancellationToken cancellationToken = default);
}

public sealed class ReportQueryService(ApplicationDbContext db, UserManager<ApplicationUser> userManager) : IReportQueryService
{
    public async Task<ReportQueryResult> QueryAsync(ClaimsPrincipal principal, DateOnly? from, DateOnly? to, TransactionStatus? status, string? uploaderId, CancellationToken cancellationToken = default)
    {
        var isAdmin = principal.IsInRole(DatabaseSeeder.AdminRole);
        var isMember = principal.IsInRole(DatabaseSeeder.MemberRole) && !principal.IsInRole(DatabaseSeeder.ModeratorRole);
        var currentUserId = userManager.GetUserId(principal);
        var query = db.Transactions.AsNoTracking().AsSplitQuery()
            .Include(x => x.UploadedByUser)
            .Include(x => x.Items).ThenInclude(x => x.ParticipantAllocations)
            .Include(x => x.Charges)
            .Include(x => x.PickupAssignment).ThenInclude(x => x!.SelectedUser)
            .Include(x => x.Participants).ThenInclude(x => x.AccountLink)
            .Include(x => x.Participants).ThenInclude(x => x.ItemAllocations).ThenInclude(x => x.Item)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentApprovals)
            .Include(x => x.Participants).ThenInclude(x => x.PaymentHistories)
            .AsQueryable();
        if (isMember) query = query.Where(x => x.Participants.Any(p => p.AccountLink != null && p.AccountLink.UserId == currentUserId));
        else if (!isAdmin) query = query.Where(x => x.UploadedByUserId == currentUserId);
        if (isAdmin && !string.IsNullOrWhiteSpace(uploaderId)) query = query.Where(x => x.UploadedByUserId == uploaderId);
        if (status.HasValue) query = query.Where(x => x.Status == status.Value);

        var users = await userManager.Users.AsNoTracking().ToDictionaryAsync(x => x.Id, cancellationToken);
        var transactions = await query.ToListAsync(cancellationToken);
        var projections = transactions
            .Select(tx => ToProjection(tx, users))
            .Where(tx => (!from.HasValue || tx.EffectiveDate >= from.Value) && (!to.HasValue || tx.EffectiveDate <= to.Value))
            .OrderByDescending(x => x.EffectiveDate).ThenByDescending(x => x.Id).ToList();
        return new ReportQueryResult { Transactions = projections };
    }

    private static ReportTransactionProjection ToProjection(BillTransaction tx, IReadOnlyDictionary<string, ApplicationUser> users)
    {
        var participants = tx.Participants
            .OrderBy(x => x.Name)
            .Select(x => ToParticipantProjection(tx, x, users))
            .ToList();
        return new ReportTransactionProjection
        {
            Id = tx.Id, TransactionNumber = tx.TransactionNumber, MerchantName = tx.MerchantName,
            EffectiveDate = tx.TransactionDate ?? DateOnly.FromDateTime(tx.UploadDate.ToLocalTime().DateTime),
            UploaderName = tx.UploadedByUser?.DisplayName, Status = tx.Status, SplitMethod = tx.SplitMethod,
            GrandTotal = tx.GrandTotal, Subtotal = tx.Subtotal, Discount = tx.Discount, Tax = tx.Tax,
            ServiceCharge = tx.ServiceCharge, Items = tx.Items.OrderBy(x => x.LineNumber).ToList(),
            Charges = tx.Charges.OrderBy(x => x.SortOrder).ToList(), Participants = participants,
            PickupPersonName = PickupName(tx), PickupProbability = tx.PickupAssignment?.RecordedProbability,
            PickupStrategy = tx.PickupAssignment?.Strategy,
            PickupSelectedAt = tx.PickupAssignment?.SelectedAt, PickupDrawKind = tx.PickupAssignment?.DrawKind
        };
    }

    private static ReportParticipantProjection ToParticipantProjection(BillTransaction tx, TransactionParticipant participant, IReadOnlyDictionary<string, ApplicationUser> users)
    {
        var latestApproval = participant.PaymentApprovals.OrderByDescending(x => x.ResolvedAt ?? x.RequestedAt).FirstOrDefault();
        var latestHistory = participant.PaymentHistories.OrderByDescending(x => x.ChangedAt).FirstOrDefault();
        var paid = participant.PaymentStatus == ParticipantPaymentStatus.Paid ? participant.Amount : 0;
        var awaiting = participant.PaymentStatus == ParticipantPaymentStatus.AwaitingConfirmation ? participant.Amount : 0;
        var unpaid = participant.PaymentStatus == ParticipantPaymentStatus.Unpaid ? participant.Amount : 0;
        return new ReportParticipantProjection
        {
            ParticipantId = participant.Id, TransactionId = tx.Id, TransactionNumber = tx.TransactionNumber,
            MerchantName = tx.MerchantName, EffectiveDate = tx.TransactionDate ?? DateOnly.FromDateTime(tx.UploadDate.ToLocalTime().DateTime),
            UploaderName = tx.UploadedByUser?.DisplayName, ParticipantName = participant.Name,
            Username = participant.AccountLink is not null && users.TryGetValue(participant.AccountLink.UserId, out var user) ? user.UserName : null,
            MenuDetail = ParticipantMenuFormatter.Format(participant, tx.SplitMethod),
            ItemDetails = ParticipantMenuFormatter.GetDetails(participant, tx.SplitMethod), AmountDue = participant.Amount,
            PaidAmount = paid, AwaitingAmount = awaiting, UnpaidAmount = unpaid, PaymentStatus = participant.PaymentStatus,
            ClaimDate = latestApproval?.RequestedAt, ResolutionDate = latestApproval?.ResolvedAt,
            ResolvedBy = latestApproval?.ResolvedByUserId is not null && users.TryGetValue(latestApproval.ResolvedByUserId, out var resolver) ? resolver.DisplayName : null,
            LastAction = latestHistory?.ActionType ?? PaymentActionType.LegacyStatusChange
            ,IsPickupPerson = tx.PickupAssignment is not null && tx.PickupAssignment.SelectedParticipantId == participant.Id,
            PickupPersonName = PickupName(tx),
            PickupProbability = tx.PickupAssignment is not null && tx.PickupAssignment.SelectedParticipantId == participant.Id ? tx.PickupAssignment.RecordedProbability : null,
            PickupSelectedAt = tx.PickupAssignment is not null && tx.PickupAssignment.SelectedParticipantId == participant.Id ? tx.PickupAssignment.SelectedAt : null
        };
    }

    private static string? PickupName(BillTransaction tx)
    {
        var assignment = tx.PickupAssignment;
        if (assignment is null) return null;
        if (!string.IsNullOrWhiteSpace(assignment.SelectedUser?.DisplayName)) return assignment.SelectedUser.DisplayName;
        return tx.Participants.FirstOrDefault(x => x.Id == assignment.SelectedParticipantId)?.Name;
    }
}
