using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed class WebPushDeliveryProcessor(
    ApplicationDbContext db,
    IWebPushTransport transport,
    ILogger<WebPushDeliveryProcessor> logger)
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan LockDuration = TimeSpan.FromMinutes(2);

    public async Task<int> ProcessDueAsync(int maximum, CancellationToken cancellationToken = default)
    {
        var configuration = await db.WebPushConfigurations.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (configuration is null || !configuration.Enabled || string.IsNullOrWhiteSpace(configuration.PublicKey)) return 0;

        var candidates = await db.WebPushDeliveries
            .Include(x => x.WebPushSubscription)
            .Include(x => x.UserNotification).ThenInclude(x => x!.Participant).ThenInclude(x => x!.Transaction)
            .Include(x => x.UserNotification).ThenInclude(x => x!.PaymentApproval)
            .Where(x => x.Status == WebPushDeliveryStatus.Pending || x.Status == WebPushDeliveryStatus.Processing)
            .OrderBy(x => x.Id)
            .Take(Math.Max(maximum * 3, maximum))
            .ToListAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var due = candidates
            .Where(x => x.Status == WebPushDeliveryStatus.Pending && x.NextAttemptAt <= now ||
                        x.Status == WebPushDeliveryStatus.Processing && (x.LockedUntil is null || x.LockedUntil <= now))
            .Take(maximum)
            .ToList();
        var processed = 0;
        foreach (var delivery in due)
        {
            cancellationToken.ThrowIfCancellationRequested();
            processed++;
            await ProcessOneAsync(delivery, configuration, now, cancellationToken);
        }
        return processed;
    }

    private async Task ProcessOneAsync(
        WebPushDelivery delivery,
        WebPushConfiguration configuration,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var subscription = delivery.WebPushSubscription;
        var notification = delivery.UserNotification;
        if (subscription is null || notification is null ||
            subscription.DisabledAt is not null || subscription.ExpiresAt <= now ||
            notification.Type is not (NotificationType.PaymentSubmitted or NotificationType.PushTest))
        {
            delivery.Status = WebPushDeliveryStatus.Cancelled;
            delivery.LockedUntil = null;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        if (!await IsEligibleRecipientAsync(notification.UserId, cancellationToken))
        {
            delivery.Status = WebPushDeliveryStatus.Cancelled;
            delivery.LockedUntil = null;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        delivery.Status = WebPushDeliveryStatus.Processing;
        delivery.AttemptCount++;
        delivery.LockedUntil = now.Add(LockDuration);
        await db.SaveChangesAsync(cancellationToken);

        WebPushSendResult result;
        try
        {
            var payload = BuildPayload(notification, subscription.Culture);
            result = await transport.SendAsync(configuration, subscription, payload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Web Push delivery {DeliveryId} failed without protocol status.", delivery.Id);
            result = new WebPushSendResult(false, false, "processor-error");
        }

        if (result.Succeeded)
        {
            delivery.Status = WebPushDeliveryStatus.Sent;
            delivery.SentAt = DateTimeOffset.UtcNow;
            delivery.LockedUntil = null;
            delivery.LastErrorCode = null;
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        delivery.LastErrorCode = result.ErrorCode[..Math.Min(result.ErrorCode.Length, 120)];
        delivery.LockedUntil = null;
        subscription.LastFailureAt = DateTimeOffset.UtcNow;
        if (result.ErrorCode is "http-404" or "http-410")
        {
            subscription.DisabledAt = DateTimeOffset.UtcNow;
            await db.WebPushDeliveries
                .Where(x => x.WebPushSubscriptionId == subscription.Id && x.Status == WebPushDeliveryStatus.Pending)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(x => x.Status, WebPushDeliveryStatus.Cancelled)
                    .SetProperty(x => x.LockedUntil, (DateTimeOffset?)null), cancellationToken);
        }

        if (result.PermanentFailure || delivery.AttemptCount >= MaxAttempts)
        {
            delivery.Status = WebPushDeliveryStatus.DeadLetter;
        }
        else
        {
            delivery.Status = WebPushDeliveryStatus.Pending;
            delivery.NextAttemptAt = DateTimeOffset.UtcNow.Add(Backoff(delivery.AttemptCount));
        }
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> IsEligibleRecipientAsync(string userId, CancellationToken cancellationToken)
    {
        var roleNames = new[] { DatabaseSeeder.AdminRole, DatabaseSeeder.ModeratorRole };
        return await db.UserRoles.Join(db.Roles, userRole => userRole.RoleId, role => role.Id,
                (userRole, role) => new { userRole.UserId, role.Name })
            .AnyAsync(x => x.UserId == userId && x.Name != null && roleNames.Contains(x.Name), cancellationToken);
    }

    private static TimeSpan Backoff(int attempt) => attempt switch
    {
        1 => TimeSpan.FromSeconds(15),
        2 => TimeSpan.FromMinutes(1),
        3 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(15)
    };

    private static string BuildPayload(UserNotification notification, string culture)
    {
        var isEnglish = culture.StartsWith("en", StringComparison.OrdinalIgnoreCase);
        if (notification.Type == NotificationType.PushTest)
        {
            return JsonSerializer.Serialize(new
            {
                title = isEnglish ? "SplitBill test notification" : "Notifikasi uji SplitBill",
                body = isEnglish ? "Browser push is connected on this device." : "Browser push terhubung di perangkat ini.",
                url = "/Notifications",
                tag = "splitbill-push-test"
            });
        }

        var participant = notification.Participant?.Name ?? (isEnglish ? "A member" : "Member");
        var merchant = notification.Participant?.Transaction?.MerchantName ?? "SplitBill";
        var amount = notification.Participant?.Amount ?? 0;
        var amountText = $"Rp {amount.ToString("N0", CultureInfo.GetCultureInfo("id-ID"))}";
        var title = isEnglish ? "New payment claim" : "Klaim pembayaran baru";
        var body = isEnglish
            ? $"{participant} submitted a payment claim for {merchant} ({amountText})."
            : $"{participant} mengirim klaim pembayaran untuk {merchant} ({amountText}).";
        var approval = notification.PaymentApprovalId;
        var url = approval.HasValue ? $"/Payments/Approvals?focus={approval.Value}" : "/Payments/Approvals";
        return JsonSerializer.Serialize(new
        {
            title,
            body,
            url,
            tag = approval.HasValue ? $"payment-submitted-{approval.Value}" : "payment-submitted"
        });
    }
}
