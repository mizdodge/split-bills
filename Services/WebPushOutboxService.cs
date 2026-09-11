using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public interface IWebPushOutboxService
{
    Task EnqueueAsync(UserNotification notification, DateTimeOffset now, CancellationToken cancellationToken = default, long? onlySubscriptionId = null);
}

public sealed class WebPushOutboxService(ApplicationDbContext db) : IWebPushOutboxService
{
    public async Task EnqueueAsync(
        UserNotification notification,
        DateTimeOffset now,
        CancellationToken cancellationToken = default,
        long? onlySubscriptionId = null)
    {
        if (notification.Type is not (NotificationType.PaymentSubmitted or NotificationType.PushTest) || string.IsNullOrWhiteSpace(notification.UserId)) return;

        // DateTimeOffset filtering is applied after the indexed user slice is
        // materialized because SQLite cannot translate it reliably.
        var subscriptions = await db.WebPushSubscriptions
            .Where(x => x.UserId == notification.UserId && x.DisabledAt == null &&
                        (!onlySubscriptionId.HasValue || x.Id == onlySubscriptionId.Value))
            .ToListAsync(cancellationToken);
        foreach (var subscription in subscriptions.Where(x => x.ExpiresAt > now))
        {
            notification.WebPushDeliveries.Add(new WebPushDelivery
            {
                UserNotification = notification,
                WebPushSubscriptionId = subscription.Id,
                WebPushSubscription = subscription,
                Status = WebPushDeliveryStatus.Pending,
                AttemptCount = 0,
                NextAttemptAt = now,
                CreatedAt = now
            });
        }
    }
}
