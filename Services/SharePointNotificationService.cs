using System.Globalization;
using System.Net;
using System.Resources;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public interface ISharePointNotificationOutboxService
{
    Task EnqueueBillAssignmentsAsync(BillTransaction transaction, string actorUserId, CancellationToken cancellationToken = default);
    Task EnqueuePaymentApprovalRequestedAsync(TransactionParticipant participant, PaymentApproval approval, string actorUserId, CancellationToken cancellationToken = default);
    Task EnqueuePaymentRejectedAsync(TransactionParticipant participant, PaymentApproval approval, string actorUserId, CancellationToken cancellationToken = default);
    Task EnqueueFoodPickupSelectedAsync(BillTransaction transaction, FoodPickupAssignment assignment, FoodPickupDrawHistory history, string actorUserId, CancellationToken cancellationToken = default);
}

public sealed class SharePointNotificationOutboxService(ApplicationDbContext db, IHttpContextAccessor? httpContextAccessor = null) : ISharePointNotificationOutboxService
{
    private static readonly ResourceManager Resources = new("Splitbill.SharedResource", typeof(SharedResource).Assembly);

    public async Task EnqueueBillAssignmentsAsync(BillTransaction transaction, string actorUserId, CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken)) return;
        var requestBaseUrl = RequestUrlBuilder.GetBaseUrl(httpContextAccessor?.HttpContext);
        if (requestBaseUrl is not null) transaction.NotificationBaseUrl = requestBaseUrl;
        var actor = await DisplayNameAsync(actorUserId, cancellationToken);
        var linked = transaction.Participants.Where(x => x.AccountLink is not null).ToList();
        var users = await UsersAsync(linked.Select(x => x.AccountLink!.UserId), cancellationToken);
        var now = DateTimeOffset.UtcNow;
        foreach (var participant in linked)
        {
            if (!users.TryGetValue(participant.AccountLink!.UserId, out var user) || string.IsNullOrWhiteSpace(user.Email)) continue;
            Add(SharePointNotificationEventType.BillAssigned, user.Email, user.DisplayName, actor,
                Text("SharePointBillAssignedTitle", actor),
                TeamsHtml(Text("SharePointBillAssignedBody", user.DisplayName, transaction.MerchantName, Money(participant.Amount), actor),
                    BuildParticipantUrl(transaction, participant.Id), Text("SharePointOpenBillDetails")),
                transaction, participant, null, now);
        }
    }

    public async Task EnqueuePaymentApprovalRequestedAsync(TransactionParticipant participant, PaymentApproval approval, string actorUserId, CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken) || participant.Transaction is null) return;
        EnsureTransactionOrigin(participant.Transaction);
        var actor = await DisplayNameAsync(actorUserId, cancellationToken);
        var owner = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == participant.Transaction.UploadedByUserId, cancellationToken);
        if (owner is null || string.IsNullOrWhiteSpace(owner.Email)) return;
        var ownerName = Name(owner);
        Add(SharePointNotificationEventType.PaymentApprovalRequested, owner.Email, ownerName, actor,
            Text("SharePointPaymentRequestedTitle", actor),
            TeamsHtml(Text("SharePointPaymentRequestedBody", ownerName, actor, participant.Transaction.MerchantName,
                participant.Transaction.TransactionNumber, Money(participant.Amount)),
                BuildApprovalsUrl(participant.Transaction), Text("SharePointOpenApprovals")),
            participant.Transaction, participant, approval, DateTimeOffset.UtcNow);
    }

    public async Task EnqueuePaymentRejectedAsync(TransactionParticipant participant, PaymentApproval approval, string actorUserId, CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken) || participant.Transaction is null || participant.AccountLink is null) return;
        EnsureTransactionOrigin(participant.Transaction);
        var actor = await DisplayNameAsync(actorUserId, cancellationToken);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == participant.AccountLink.UserId, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return;
        var recipient = Name(user);
        var reason = string.IsNullOrWhiteSpace(approval.Note) ? Text("SharePointNoRejectionReason") : approval.Note;
        Add(SharePointNotificationEventType.PaymentRejected, user.Email, recipient, actor,
            Text("SharePointPaymentRejectedTitle", actor),
            TeamsHtml(Text("SharePointPaymentRejectedBody", recipient, participant.Transaction.MerchantName,
                participant.Transaction.TransactionNumber, Money(participant.Amount), actor, reason),
                BuildParticipantUrl(participant.Transaction, participant.Id), Text("SharePointOpenBillDetails")),
            participant.Transaction, participant, approval, DateTimeOffset.UtcNow);
    }

    public async Task EnqueueFoodPickupSelectedAsync(BillTransaction transaction, FoodPickupAssignment assignment, FoodPickupDrawHistory history, string actorUserId, CancellationToken cancellationToken = default)
    {
        if (!await IsEnabledAsync(cancellationToken) || history.Id <= 0) return;
        var participant = transaction.Participants.FirstOrDefault(x => x.Id == assignment.SelectedParticipantId);
        if (participant is null) return;
        EnsureTransactionOrigin(transaction);
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assignment.SelectedUserId, cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.Email)) return;
        var eventId = $"food-pickup:{history.Id}";
        if (db.SharePointNotificationOutbox.Local.Any(x => x.EventId == eventId) ||
            await db.SharePointNotificationOutbox.AnyAsync(x => x.EventId == eventId, cancellationToken)) return;
        var recipient = Name(user);
        var actor = await DisplayNameAsync(actorUserId, cancellationToken);
        var body = assignment.Strategy == FoodPickupSelectionStrategy.RoundRobin
            ? Text("SharePointFoodPickupRoundRobinBody", recipient, transaction.MerchantName,
                transaction.TransactionNumber, transaction.Participants.Count)
            : Text("SharePointFoodPickupBody", recipient, transaction.MerchantName,
                transaction.TransactionNumber, transaction.Participants.Count,
                assignment.RecordedProbability.ToString("P2", CultureInfo.CurrentUICulture));
        Add(SharePointNotificationEventType.FoodPickupSelected, user.Email, recipient, actor,
            Text("SharePointFoodPickupTitle", recipient),
            TeamsHtml(body, BuildParticipantUrl(transaction, participant.Id), Text("SharePointOpenBillDetails")),
            transaction, participant, null, DateTimeOffset.UtcNow, eventId);
    }

    private void Add(SharePointNotificationEventType type, string email, string recipient, string actor,
        string title, string description, BillTransaction transaction, TransactionParticipant participant,
        PaymentApproval? approval, DateTimeOffset now, string? eventId = null)
    {
        db.SharePointNotificationOutbox.Add(new SharePointNotificationOutbox
        {
            EventId = eventId ?? $"{type}-{Guid.NewGuid():N}", EventType = type,
            RecipientEmail = email.Trim(), RecipientName = recipient, ActorName = actor,
            Title = title, Description = description, TransactionId = transaction.Id,
            TransactionNumber = transaction.TransactionNumber, MerchantName = transaction.MerchantName,
            ParticipantId = participant.Id, ApprovalId = approval?.Id > 0 ? approval.Id : null,
            Amount = participant.Amount, Status = SharePointNotificationStatus.Pending,
            NextAttemptAt = now, CreatedAt = now
        });
    }

    private async Task<bool> IsEnabledAsync(CancellationToken cancellationToken) =>
        await db.SharePointConfigurations.AsNoTracking().AnyAsync(x => x.Id == 1 && x.Enabled && x.LastTestSucceeded == true && x.SiteId != "" && x.ListId != "", cancellationToken);

    private async Task<string> DisplayNameAsync(string userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, cancellationToken);
        return user is null ? "SplitBill" : Name(user);
    }

    private async Task<Dictionary<string, ApplicationUser>> UsersAsync(IEnumerable<string> ids, CancellationToken cancellationToken)
    {
        var wanted = ids.Distinct(StringComparer.Ordinal).ToArray();
        return (await db.Users.AsNoTracking().Where(x => wanted.Contains(x.Id)).ToListAsync(cancellationToken))
            .ToDictionary(x => x.Id, StringComparer.Ordinal);
    }

    private static string Name(ApplicationUser user) => string.IsNullOrWhiteSpace(user.DisplayName) ? user.UserName ?? "User" : user.DisplayName;
    private static string Money(decimal amount) => Text("SharePointCurrencyAmount", amount);

    private static string Text(string key, params object[] arguments)
    {
        var culture = CultureInfo.CurrentUICulture;
        var format = Resources.GetString(key, culture) ?? key;
        return arguments.Length == 0 ? format : string.Format(culture, format, arguments);
    }

    private void EnsureTransactionOrigin(BillTransaction transaction)
    {
        if (string.IsNullOrWhiteSpace(transaction.NotificationBaseUrl))
            transaction.NotificationBaseUrl = RequestUrlBuilder.GetBaseUrl(httpContextAccessor?.HttpContext);
    }

    private static string? BuildParticipantUrl(BillTransaction transaction, long participantId) =>
        BuildUrl(transaction.NotificationBaseUrl, $"/MyBills/Details/{participantId}");

    private static string? BuildApprovalsUrl(BillTransaction transaction) =>
        BuildUrl(transaction.NotificationBaseUrl, "/Payments/Approvals");

    private static string? BuildUrl(string? baseUrl, string path) =>
        string.IsNullOrWhiteSpace(baseUrl) ? null : $"{baseUrl.TrimEnd('/')}{path}";

    private static string TeamsHtml(string plainText, string? actionUrl = null, string? actionLabel = null)
    {
        var html = WebUtility.HtmlEncode(plainText.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Replace("\n", "<br>", StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(actionUrl) || string.IsNullOrWhiteSpace(actionLabel)) return html;
        return $"{html}<br><br><a href=\"{WebUtility.HtmlEncode(actionUrl)}\">{WebUtility.HtmlEncode(actionLabel)}</a>";
    }
}

internal static class RequestUrlBuilder
{
    public static string? GetBaseUrl(HttpContext? context)
    {
        var request = context?.Request;
        if (request is null || !request.Host.HasValue) return null;
        var scheme = request.Scheme.Trim().ToLowerInvariant();
        if (scheme is not ("http" or "https") || string.IsNullOrWhiteSpace(request.Host.Host)) return null;

        var builder = new UriBuilder(scheme, request.Host.Host)
        {
            Path = request.PathBase.Value?.TrimEnd('/') ?? string.Empty
        };
        if (request.Host.Port is int port) builder.Port = port;
        var origin = builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
        return origin.Length is > 0 and <= 500 ? origin : null;
    }
}

public sealed class SharePointNotificationProcessor(
    ApplicationDbContext db,
    ISharePointGraphService graph,
    SharePointSecretProtector secretProtector,
    ILogger<SharePointNotificationProcessor> logger)
{
    private const int MaxAttempts = 8;

    public async Task<int> ProcessDueAsync(int limit, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var candidates = await db.SharePointNotificationOutbox
            .Where(x => x.Status == SharePointNotificationStatus.Pending || x.Status == SharePointNotificationStatus.Processing)
            .ToListAsync(cancellationToken);
        var due = candidates.Where(x => x.NextAttemptAt <= now && (x.LockedUntil is null || x.LockedUntil <= now))
            .OrderBy(x => x.NextAttemptAt).ThenBy(x => x.Id).Take(limit).ToList();
        if (due.Count == 0) return 0;

        var config = await db.SharePointConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
        if (config is null || !config.Enabled || string.IsNullOrWhiteSpace(config.ProtectedClientSecret)) return 0;

        string secret;
        try { secret = secretProtector.Unprotect(config.ProtectedClientSecret); }
        catch (InvalidOperationException)
        {
            foreach (var item in due) Fail(item, now, "SECRET_UNPROTECT_FAILED");
            await db.SaveChangesAsync(cancellationToken);
            return due.Count;
        }

        foreach (var item in due)
        {
            item.Status = SharePointNotificationStatus.Processing;
            item.LockedUntil = now.AddMinutes(2);
            await db.SaveChangesAsync(cancellationToken);
            try
            {
                await graph.CreateNotificationItemAsync(new SharePointListItemRequest(
                    config.TenantId, config.ClientId, secret, config.SiteId, config.ListId,
                    item.Title, item.RecipientEmail, item.Description), cancellationToken);
                item.Status = SharePointNotificationStatus.Sent;
                item.SentAt = DateTimeOffset.UtcNow;
                item.LockedUntil = null;
                item.LastErrorCode = null;
            }
            catch (SharePointGraphException exception)
            {
                logger.LogWarning("SharePoint notification {EventId} delivery failed with {Code}.", item.EventId, exception.DiagnosticCode ?? exception.Category.ToString());
                Fail(item, DateTimeOffset.UtcNow, exception.DiagnosticCode ?? exception.Category.ToString());
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(exception, "SharePoint notification {EventId} delivery failed.", item.EventId);
                Fail(item, DateTimeOffset.UtcNow, exception.GetType().Name);
            }
            await db.SaveChangesAsync(cancellationToken);
        }
        return due.Count;
    }

    private static void Fail(SharePointNotificationOutbox item, DateTimeOffset now, string code)
    {
        item.AttemptCount++;
        item.LockedUntil = null;
        item.LastErrorCode = code.Length <= 160 ? code : code[..160];
        item.Status = item.AttemptCount >= MaxAttempts ? SharePointNotificationStatus.DeadLetter : SharePointNotificationStatus.Pending;
        item.NextAttemptAt = now.AddSeconds(Math.Min(900, 15 * Math.Pow(2, Math.Min(item.AttemptCount - 1, 6))));
    }
}

public sealed class SharePointNotificationDispatcher(IServiceScopeFactory scopeFactory, ILogger<SharePointNotificationDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<SharePointNotificationProcessor>().ProcessDueAsync(20, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { logger.LogError(exception, "SharePoint notification dispatcher iteration failed."); }
            try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }
}
