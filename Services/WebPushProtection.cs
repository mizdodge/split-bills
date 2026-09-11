using System.Security.Cryptography;
using System.Text;

namespace Splitbill.Services;

/// <summary>
/// Shared protection and hashing helpers for browser push data. Endpoints and
/// encryption keys are never stored in plaintext; the hash is only used for
/// lookup and deduplication.
/// </summary>
public static class WebPushProtection
{
    public const string VapidPrivateKeyPurpose = "SplitBill.WebPush.VapidPrivateKey.v1";
    public const string SubscriptionPurpose = "SplitBill.WebPush.Subscription.v1";

    public static string Hash(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim())));
    }
}
