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

[Authorize(Roles = $"{DatabaseSeeder.AdminRole},{DatabaseSeeder.ModeratorRole}")]
public sealed class PaymentsController(ApplicationDbContext db, UserManager<ApplicationUser> userManager,
    IPaymentWorkflowService paymentWorkflowService, IPaymentProofStorageService proofStorage,
    IStringLocalizer<SharedResource> localizer) : Controller
{
    public async Task<IActionResult> Approvals(CancellationToken cancellationToken)
    {
        var isAdmin = User.IsInRole(DatabaseSeeder.AdminRole);
        var userId = userManager.GetUserId(User)!;
        var query = db.PaymentApprovals.AsNoTracking()
            .Where(x => x.Status == PaymentApprovalStatus.Pending)
            .Include(x => x.Participant).ThenInclude(x => x!.Transaction)
            .AsQueryable();
        if (!isAdmin) query = query.Where(x => x.Participant!.Transaction!.UploadedByUserId == userId);
        // SQLite cannot translate DateTimeOffset ORDER BY; filter/load first, then sort in memory.
        var approvals = (await query.ToListAsync(cancellationToken)).OrderBy(x => x.RequestedAt).ToList();
        return View(new PaymentApprovalListViewModel
        {
            Approvals = approvals.Select(x => new PaymentApprovalRowViewModel
            {
                ParticipantId = x.ParticipantId,
                TransactionId = x.Participant!.TransactionId,
                TransactionNumber = x.Participant.Transaction!.TransactionNumber,
                MerchantName = x.Participant.Transaction.MerchantName,
                ParticipantName = x.Participant.Name,
                Amount = x.Participant.Amount,
                RequestedAt = x.RequestedAt, ProofOriginalFileName = x.ProofOriginalFileName,
                HasProof = !string.IsNullOrWhiteSpace(x.ProofFileName), ApprovalId = x.Id
            }).ToList()
        });
    }

    [HttpGet]
    public async Task<IActionResult> Proof(long id, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User)!;
        var isAdmin = User.IsInRole(DatabaseSeeder.AdminRole);
        var approval = await db.PaymentApprovals.AsNoTracking().Include(x => x.Participant).ThenInclude(x => x!.Transaction)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (approval is null || string.IsNullOrWhiteSpace(approval.ProofFileName) ||
            (!isAdmin && approval.Participant?.Transaction?.UploadedByUserId != userId)) return NotFound();
        var path = proofStorage.GetPath(approval.ProofFileName);
        if (!System.IO.File.Exists(path)) return NotFound();
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, approval.ProofContentType ?? "application/octet-stream", enableRangeProcessing: true);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Confirm(long id, CancellationToken cancellationToken)
    {
        var result = await paymentWorkflowService.ConfirmAsync(id, userManager.GetUserId(User)!, User.IsInRole(DatabaseSeeder.AdminRole), cancellationToken);
        if (!result.Succeeded && result.TransactionId is null) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.MessageKey is { } key ? localizer[key].Value : result.Message;
        return RedirectToAction(nameof(Approvals));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(long id, string? note, CancellationToken cancellationToken)
    {
        var result = await paymentWorkflowService.RejectAsync(id, userManager.GetUserId(User)!, User.IsInRole(DatabaseSeeder.AdminRole), note, cancellationToken);
        if (!result.Succeeded && result.TransactionId is null) return NotFound();
        TempData[result.Succeeded ? "Success" : "Error"] = result.MessageKey is { } key ? localizer[key].Value : result.Message;
        return RedirectToAction(nameof(Approvals));
    }
}
