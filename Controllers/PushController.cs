using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Controllers;

[Authorize(Roles = DatabaseSeeder.AdminRole + "," + DatabaseSeeder.ModeratorRole)]
[ApiController]
[Route("[controller]/[action]")]
public sealed class PushController(
    ApplicationDbContext db,
    UserManager<ApplicationUser> userManager,
    IWebPushKeyService keys,
    IWebPushSubscriptionService subscriptions,
    IWebPushOutboxService outbox) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> PublicKey(CancellationToken cancellationToken)
    {
        var configuration = await keys.EnsureAsync(cancellationToken);
        return Ok(new { enabled = configuration.Enabled, publicKey = configuration.PublicKey });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Subscribe([FromBody] WebPushSubscribeRequest request, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (userId is null) return Unauthorized();
        if (request.Endpoint is null || request.P256dh is null || request.Auth is null || request.InstallationId is null)
            return BadRequest(new { error = "Data subscription push belum lengkap." });

        var result = await subscriptions.UpsertAsync(userId, new WebPushSubscriptionInput(
            request.Endpoint, request.P256dh, request.Auth, request.InstallationId,
            request.Culture, request.BrowserLabel, request.ExpiresAt), cancellationToken);
        return result.Succeeded
            ? Ok(new { ok = true })
            : BadRequest(new { error = result.Error ?? "Subscription push tidak valid." });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Heartbeat([FromBody] WebPushHeartbeatRequest request, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (userId is null) return Unauthorized();
        var ok = await subscriptions.HeartbeatAsync(userId, request.InstallationId ?? string.Empty, cancellationToken);
        return Ok(new { ok });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Unsubscribe([FromBody] WebPushUnsubscribeRequest request, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (userId is null) return Unauthorized();
        var ok = await subscriptions.RemoveAsync(userId, request.Endpoint, request.InstallationId, cancellationToken);
        return Ok(new { ok });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Test([FromBody] WebPushHeartbeatRequest request, CancellationToken cancellationToken)
    {
        var userId = userManager.GetUserId(User);
        if (userId is null) return Unauthorized();
        var subscriptionId = await subscriptions.FindActiveIdAsync(userId, request.InstallationId ?? string.Empty, cancellationToken);
        if (!subscriptionId.HasValue) return BadRequest(new { error = "Aktifkan subscription push di perangkat ini terlebih dahulu." });

        var notification = new UserNotification
        {
            UserId = userId,
            Type = NotificationType.PushTest,
            IsRead = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.UserNotifications.Add(notification);
        await outbox.EnqueueAsync(notification, notification.CreatedAt, cancellationToken, subscriptionId);
        await db.SaveChangesAsync(cancellationToken);
        return Accepted(new { ok = true });
    }
}
