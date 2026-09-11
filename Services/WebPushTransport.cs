using System.Net;
using System.Text.Json;
using Lib.Net.Http.WebPush;
using Lib.Net.Http.WebPush.Authentication;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record WebPushSendResult(bool Succeeded, bool PermanentFailure, string ErrorCode);

public interface IWebPushTransport
{
    Task<WebPushSendResult> SendAsync(
        WebPushConfiguration configuration,
        WebPushSubscription subscription,
        string payload,
        CancellationToken cancellationToken = default);
}

public sealed class LibNetWebPushTransport(
    HttpClient httpClient,
    WebPushSecretProtector secrets) : IWebPushTransport
{
    public async Task<WebPushSendResult> SendAsync(
        WebPushConfiguration configuration,
        WebPushSubscription subscription,
        string payload,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = secrets.UnprotectSubscriptionValue(subscription.ProtectedEndpoint);
            var p256dh = secrets.UnprotectSubscriptionValue(subscription.ProtectedP256dh);
            var auth = secrets.UnprotectSubscriptionValue(subscription.ProtectedAuth);
            var privateKey = secrets.UnprotectVapidPrivateKey(configuration.ProtectedPrivateKey);

            var pushSubscription = new PushSubscription { Endpoint = endpoint };
            pushSubscription.SetKey(PushEncryptionKeyName.P256DH, p256dh);
            pushSubscription.SetKey(PushEncryptionKeyName.Auth, auth);
            var message = new PushMessage(payload)
            {
                TimeToLive = 86_400,
                Urgency = PushMessageUrgency.High,
                Topic = "splitbill-payment"
            };
            using var authentication = new VapidAuthentication(configuration.PublicKey, privateKey)
            {
                Subject = configuration.Subject,
                Expiration = 3_600
            };
            var client = new PushServiceClient(httpClient)
            {
                AutoRetryAfter = true,
                MaxRetriesAfter = 2
            };
            await client.RequestPushMessageDeliveryAsync(
                pushSubscription, message, authentication,
                VapidAuthenticationScheme.Vapid, cancellationToken);
            return new WebPushSendResult(true, false, "sent");
        }
        catch (PushServiceClientException exception)
        {
            var status = (int)exception.StatusCode;
            var permanent = exception.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden or HttpStatusCode.NotFound or (HttpStatusCode)410;
            return new WebPushSendResult(false, permanent, $"http-{status}");
        }
        catch (HttpRequestException)
        {
            return new WebPushSendResult(false, false, "network-error");
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WebPushSendResult(false, false, "timeout");
        }
        catch (Exception)
        {
            // Do not leak endpoint, key, or payload contents to logs or the DB.
            return new WebPushSendResult(false, true, "configuration-error");
        }
    }
}
