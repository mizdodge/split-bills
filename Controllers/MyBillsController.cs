using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize]
public sealed class MyBillsController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    IPaymentWorkflowService paymentWorkflowService, IPaymentProofStorageService proofStorage,
    IStringLocalizer<SharedResource> localizer, ISplitBillCalculator calculator,
    IWebHostEnvironment environment) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User)!;
        var participants = await db.TransactionParticipants.AsSplitQuery()
            .Where(x => x.AccountLink != null && x.AccountLink.UserId == userId)
            .Include(x => x.Transaction).ThenInclude(x => x!.Items)
            .Include(x => x.Transaction).ThenInclude(x => x!.Participants)
            .Include(x => x.Transaction).ThenInclude(x => x!.PickupAssignment).ThenInclude(x => x!.SelectedUser)
            .Include(x => x.ItemAllocations).ThenInclude(x => x.Item)
            .Include(x => x.PaymentApprovals)
            .ToListAsync(cancellationToken);
        return View(new MyBillsViewModel
        {
            UnpaidAmount = participants.Where(x => x.PaymentStatus == ParticipantPaymentStatus.Unpaid).Sum(x => x.Amount),
            AwaitingAmount = participants.Where(x => x.PaymentStatus == ParticipantPaymentStatus.AwaitingConfirmation).Sum(x => x.Amount),
            PaidAmount = participants.Where(x => x.PaymentStatus == ParticipantPaymentStatus.Paid).Sum(x => x.Amount),
            Bills = participants.OrderByDescending(x => x.Transaction?.TransactionDate ?? DateOnly.FromDateTime(x.Transaction?.UploadDate.LocalDateTime ?? DateTime.MinValue)).Select(ToRow).ToList()
        });
    }

    public async Task<IActionResult> Details(long id, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User)!;
        var participant = await db.TransactionParticipants.AsSplitQuery()
            .Where(x => x.Id == id && x.AccountLink != null && x.AccountLink.UserId == userId)
            .Include(x => x.Transaction).ThenInclude(x => x!.Items)
            .Include(x => x.Transaction).ThenInclude(x => x!.Charges)
            .Include(x => x.Transaction).ThenInclude(x => x!.ReceiptImages)
            .Include(x => x.Transaction).ThenInclude(x => x!.PickupAssignment).ThenInclude(x => x!.SelectedUser)
            .Include(x => x.Transaction).ThenInclude(x => x!.Participants).ThenInclude(x => x.ItemAllocations).ThenInclude(x => x.Item)
            .Include(x => x.ItemAllocations).ThenInclude(x => x.Item)
            .Include(x => x.PaymentApprovals)
            .SingleOrDefaultAsync(cancellationToken);
        if (participant is null || participant.Transaction is null) return NotFound();
        var breakdown = calculator.BuildParticipantBreakdowns(participant.Transaction)
            .SingleOrDefault(x => x.ParticipantId == participant.Id);
        return breakdown is null ? NotFound() : View(ToDetails(participant, breakdown));
    }

    [HttpGet]
    public async Task<IActionResult> ReceiptImage(long id, int index = 0, CancellationToken cancellationToken = default)
    {
        if (index < 0) return NotFound();
        var userId = userManager.GetUserId(User)!;
        var participant = await db.TransactionParticipants.AsNoTracking()
            .Where(x => x.Id == id && x.AccountLink != null && x.AccountLink.UserId == userId)
            .Include(x => x.Transaction).ThenInclude(x => x!.ReceiptImages)
            .SingleOrDefaultAsync(cancellationToken);
        if (participant?.Transaction is null) return NotFound();

        var image = StoredReceiptImages(participant.Transaction).ElementAtOrDefault(index);
        if (image is null) return NotFound();
        var fileName = Path.GetFileName(image.FileName);
        if (string.IsNullOrWhiteSpace(fileName)) return NotFound();
        var path = Path.Combine(environment.ContentRootPath, "App_Data", "receipts", fileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        var contentType = image.ContentType?.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/png" or "image/webp" => image.ContentType.ToLowerInvariant(),
            _ => ContentTypeFor(fileName)
        };
        return PhysicalFile(path, contentType, enableRangeProcessing: true);
    }

    [HttpPost, ValidateAntiForgeryToken]
    [RequestSizeLimit(11 * 1024 * 1024)]
    public async Task<IActionResult> SubmitPayment(PaymentClaimViewModel model, CancellationToken cancellationToken)
    {
        var id = model.ParticipantId;
        if (!ModelState.IsValid || model.ProofImage is null)
        {
            TempData["Error"] = localizer["InvalidPaymentProof"].Value;
            return RedirectToAction(nameof(Details), new { id });
        }

        StoredPaymentProof? stored = null;
        try { stored = await proofStorage.StoreAsync(model.ProofImage, cancellationToken); }
        catch (PaymentProofException ex) { TempData["Error"] = localizer[ex.MessageKey].Value; return RedirectToAction(nameof(Details), new { id }); }

        var result = await paymentWorkflowService.SubmitClaimAsync(id, userManager.GetUserId(User)!, stored, cancellationToken);
        if (!result.Succeeded) proofStorage.Delete(stored.FileName);
        if (!result.Succeeded && result.TransactionId is null) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.MessageKey is { } key ? localizer[key].Value : result.Message;
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpGet]
    public async Task<IActionResult> Proof(long id, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User)!;
        var approval = await db.PaymentApprovals.AsNoTracking().Include(x => x.Participant)
            .SingleOrDefaultAsync(x => x.Id == id && x.Participant!.AccountLink!.UserId == userId, cancellationToken);
        if (approval is null || string.IsNullOrWhiteSpace(approval.ProofFileName)) return NotFound();
        var path = proofStorage.GetPath(approval.ProofFileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, approval.ProofContentType ?? "application/octet-stream", enableRangeProcessing: true);
    }

    private static MyBillRowViewModel ToRow(TransactionParticipant participant) => new()
    {
        ParticipantId = participant.Id,
        TransactionId = participant.TransactionId,
        TransactionNumber = participant.Transaction?.TransactionNumber ?? string.Empty,
        MerchantName = participant.Transaction?.MerchantName ?? string.Empty,
        TransactionDate = participant.Transaction?.TransactionDate,
        Amount = participant.Amount,
        PaymentStatus = participant.PaymentStatus,
        MenuDetail = ParticipantMenuFormatter.Format(participant, participant.Transaction?.SplitMethod ?? SplitMethod.Equal),
        ItemDetails = ParticipantMenuFormatter.GetDetails(participant, participant.Transaction?.SplitMethod ?? SplitMethod.Equal)
            .Select(x => new MyBillItemDetailViewModel { Name = x.Name, Quantity = x.Quantity, Amount = x.Amount }).ToList(),
        AdditionalItemCount = Math.Max(0, ParticipantMenuFormatter.GetDetails(participant, participant.Transaction?.SplitMethod ?? SplitMethod.Equal).Count - 2),
        PaymentAttempts = participant.PaymentApprovals.OrderByDescending(x => x.RequestedAt).Select(x => new PaymentAttemptViewModel
        {
            ApprovalId = x.Id, Status = x.Status, RequestedAt = x.RequestedAt, ResolvedAt = x.ResolvedAt,
            Note = x.Note, ProofOriginalFileName = x.ProofOriginalFileName, HasProof = !string.IsNullOrWhiteSpace(x.ProofFileName)
        }).ToList()
    };

    private MyBillDetailsViewModel ToDetails(TransactionParticipant participant, ParticipantBillBreakdown breakdown)
    {
        var transaction = participant.Transaction!;
        var receiptImages = StoredReceiptImages(transaction);
        return new MyBillDetailsViewModel
        {
            ParticipantId = participant.Id,
            TransactionId = transaction.Id,
            TransactionNumber = transaction.TransactionNumber,
            MerchantName = transaction.MerchantName,
            TransactionDate = transaction.TransactionDate,
            Amount = breakdown.FinalAmount,
            PaymentStatus = participant.PaymentStatus,
            PickupPersonName = !string.IsNullOrWhiteSpace(transaction.PickupAssignment?.SelectedUser?.DisplayName)
                ? transaction.PickupAssignment!.SelectedUser!.DisplayName
                : transaction.Participants.FirstOrDefault(x => x.Id == transaction.PickupAssignment?.SelectedParticipantId)?.Name,
            IsPickupPerson = transaction.PickupAssignment is not null &&
                             string.Equals(transaction.PickupAssignment.SelectedUserId, participant.AccountLink?.UserId, StringComparison.Ordinal),
            PickupProbability = transaction.PickupAssignment?.RecordedProbability,
            PickupStrategy = transaction.PickupAssignment?.Strategy,
            PickupSelectedAt = transaction.PickupAssignment?.SelectedAt,
            PickupDrawKind = transaction.PickupAssignment?.DrawKind,
            MenuDetail = ParticipantMenuFormatter.Format(participant, transaction.SplitMethod),
            ItemSubtotal = breakdown.ItemSubtotal,
            AdjustmentTotal = breakdown.AdjustmentTotal,
            Items = breakdown.Items.Select(x => new MyBillBreakdownItemViewModel
            {
                ItemId = x.ItemId, Name = x.Name, Quantity = x.QuantityShare,
                ReceiptAmount = x.ReceiptAmount, Amount = x.ParticipantAmount
            }).ToList(),
            Adjustments = breakdown.Adjustments.Select(x => new MyBillBreakdownAdjustmentViewModel
            {
                Label = x.Label, Operation = x.Operation, ReceiptAmount = x.ReceiptAmount,
                Amount = x.ParticipantAmount, Kind = x.Kind,
                ReceiptPercentage = x.ReceiptPercentage, ParticipantPercentage = x.ParticipantPercentage
            }).ToList(),
            ReceiptImageUrls = receiptImages.Select((_, index) => Url.Action(nameof(ReceiptImage), "MyBills", new { id = participant.Id, index }) ?? string.Empty).ToList(),
            PaymentAttempts = participant.PaymentApprovals.OrderByDescending(x => x.RequestedAt).Select(x => new PaymentAttemptViewModel
            {
                ApprovalId = x.Id, Status = x.Status, RequestedAt = x.RequestedAt, ResolvedAt = x.ResolvedAt,
                Note = x.Note, ProofOriginalFileName = x.ProofOriginalFileName, HasProof = !string.IsNullOrWhiteSpace(x.ProofFileName)
            }).ToList()
        };
    }

    private static IReadOnlyList<TransactionReceiptImage> StoredReceiptImages(BillTransaction transaction)
    {
        if (transaction.ReceiptImages.Count > 0)
            return transaction.ReceiptImages.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).ToList();
        if (string.IsNullOrWhiteSpace(transaction.ReceiptImagePath)) return [];
        return [new TransactionReceiptImage
        {
            FileName = transaction.ReceiptImagePath,
            ContentType = ContentTypeFor(transaction.ReceiptImagePath),
            SortOrder = 0
        }];
    }

    private static string ContentTypeFor(string fileName) =>
        Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
}
