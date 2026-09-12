using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[AllowAnonymous]
[EnableRateLimiting("GuestAccess")]
public sealed class GuestController(IGuestAccessService guestAccess, ISplitBillCalculator calculator, IWebHostEnvironment environment) : Controller
{
    [HttpGet("/g/my-bill/{token}", Name = "guest-my-bill")]
    public async Task<IActionResult> MyBill(string token, CancellationToken cancellationToken)
    {
        var context = await guestAccess.ResolveAsync(token, true, cancellationToken);
        if (context is null) return GuestNotFound();
        SetPrivateHeaders();
        return View("MyBill", BuildModel(context, token, false));
    }

    [HttpGet("/g/transaction/{token}", Name = "guest-transaction")]
    public async Task<IActionResult> Transaction(string token, CancellationToken cancellationToken)
    {
        var context = await guestAccess.ResolveAsync(token, true, cancellationToken);
        if (context is null) return GuestNotFound();
        SetPrivateHeaders();
        return View("Transaction", BuildModel(context, token, true));
    }

    [HttpGet("/g/receipt/{imageId:long}/{token}")]
    public async Task<IActionResult> Receipt(long imageId, string token, CancellationToken cancellationToken)
    {
        var context = await guestAccess.ResolveAsync(token, false, cancellationToken);
        if (context is null) return GuestNotFound();
        var image = context.Transaction.ReceiptImages.FirstOrDefault(x => x.Id == imageId);
        if (image is null) return GuestNotFound();
        var fileName = Path.GetFileName(image.FileName);
        var path = Path.Combine(environment.ContentRootPath, "App_Data", "receipts", fileName);
        if (!System.IO.File.Exists(path)) return GuestNotFound();
        SetPrivateHeaders(); Response.Headers["X-Content-Type-Options"] = "nosniff";
        return PhysicalFile(path, string.IsNullOrWhiteSpace(image.ContentType) ? "image/jpeg" : image.ContentType, enableRangeProcessing: true);
    }

    private GuestBillViewModel BuildModel(GuestAccessContext context, string token, bool whole)
    {
        var tx = context.Transaction;
        var breakdowns = calculator.BuildParticipantBreakdowns(tx).ToDictionary(x => x.ParticipantId);
        var items = tx.Items.OrderBy(x => x.LineNumber).Select(x => new GuestItemViewModel(x.Name, x.Quantity, x.TotalPrice)).ToList();
        var participants = (whole ? tx.Participants : [context.Participant]).OrderBy(x => x.Name).Select(p =>
        {
            breakdowns.TryGetValue(p.Id, out var b);
            var participantItems = b?.Items.Select(i => new GuestItemViewModel(i.Name, i.QuantityShare, i.ParticipantAmount)).ToList() ?? [];
            return new GuestParticipantViewModel(p.Name, p.Amount, p.PaymentStatus, participantItems);
        }).ToList();
        var receiptUrls = tx.ReceiptImages.OrderBy(x => x.SortOrder).ThenBy(x => x.Id).Select(x => Url.Action(nameof(Receipt), new { imageId = x.Id, token }) ?? string.Empty).ToList();
        if (receiptUrls.Count == 0 && !string.IsNullOrWhiteSpace(tx.ReceiptImagePath)) receiptUrls.Add($"/g/receipt/0/{Uri.EscapeDataString(token)}");
        return new GuestBillViewModel
        {
            Token = token, WholeTransaction = whole, MerchantName = tx.MerchantName, TransactionNumber = tx.TransactionNumber, TransactionDate = tx.TransactionDate,
            Status = tx.Status, CurrencyCode = tx.CurrencyCode, ReportingCurrencyCode = tx.ReportingCurrencyCode, ExchangeRateToReporting = tx.ExchangeRateToReporting,
            ExchangeRateEffectiveDate = tx.ExchangeRateEffectiveDate, ExchangeRateSource = tx.ExchangeRateSource, Subtotal = tx.Subtotal, Discount = tx.Discount,
            Tax = tx.Tax, ServiceCharge = tx.ServiceCharge, GrandTotal = tx.GrandTotal, Items = items, Participants = participants, ReceiptImageUrls = receiptUrls,
            PickupPerson = tx.PickupAssignment?.SelectedUser?.DisplayName ?? tx.Participants.FirstOrDefault(x => x.Id == tx.PickupAssignment?.SelectedParticipantId)?.Name,
            GuestParticipantName = context.Participant.Name
        };
    }

    private IActionResult GuestNotFound() => NotFound();
    private void SetPrivateHeaders()
    {
        Response.Headers["Cache-Control"] = "no-store"; Response.Headers["Pragma"] = "no-cache"; Response.Headers["Referrer-Policy"] = "no-referrer";
    }
}
