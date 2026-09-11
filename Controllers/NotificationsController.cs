using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize]
public sealed class NotificationsController(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IWebPushKeyService pushKeys,
    IWebPushSubscriptionService pushSubscriptions) : Controller
{
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User)!;
        var notifications = await db.UserNotifications.AsNoTracking().Where(x => x.UserId == userId)
            .Include(x => x.Participant).ThenInclude(x => x!.Transaction)
            .Include(x => x.PaymentApproval)
            .ToListAsync(cancellationToken);
        notifications = notifications.OrderByDescending(x => x.CreatedAt).Take(100).ToList();
        WebPushStatusViewModel? pushStatus = null;
        if (User.IsInRole(DatabaseSeeder.AdminRole) || User.IsInRole(DatabaseSeeder.ModeratorRole))
        {
            var configuration = await pushKeys.EnsureAsync(cancellationToken);
            pushStatus = new WebPushStatusViewModel
            {
                Enabled = configuration.Enabled,
                PublicKey = configuration.PublicKey,
                ActiveSubscriptions = await pushSubscriptions.CountActiveAsync(userId, cancellationToken)
            };
        }

        return View(new NotificationListViewModel
        {
            WebPush = pushStatus,
            Notifications = notifications.Select(x => new NotificationRowViewModel
            {
                Id = x.Id, Type = x.Type, IsRead = x.IsRead, CreatedAt = x.CreatedAt,
                MerchantName = x.Participant?.Transaction?.MerchantName ?? string.Empty,
                TransactionNumber = x.Participant?.Transaction?.TransactionNumber ?? string.Empty,
                Amount = x.Participant?.Amount ?? 0,
                ParticipantId = x.ParticipantId, PaymentApprovalId = x.PaymentApprovalId,
                RejectionReason = x.PaymentApproval?.Note
            }).ToList()
        });
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Read(long id, string? returnUrl, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User)!;
        var notification = await db.UserNotifications.SingleOrDefaultAsync(x => x.Id == id && x.UserId == userId, cancellationToken);
        if (notification is not null) { notification.IsRead = true; await db.SaveChangesAsync(cancellationToken); }
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action(nameof(Index))!);
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ReadAll(string? returnUrl, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User)!;
        await db.UserNotifications.Where(x => x.UserId == userId && !x.IsRead).ExecuteUpdateAsync(setters => setters.SetProperty(x => x.IsRead, true), cancellationToken);
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action(nameof(Index))!);
    }
}
