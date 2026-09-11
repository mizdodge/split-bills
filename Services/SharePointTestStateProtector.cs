using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Splitbill.Services;

public sealed record SharePointTestState(
    string UserId,
    string TenantId,
    string ClientId,
    string SiteUrl,
    string SiteId,
    string SiteDisplayName,
    string SiteWebUrl,
    string Fingerprint,
    DateTimeOffset TestedAt,
    DateTimeOffset ExpiresAt,
    IReadOnlyList<SharePointListOption> Lists);

public interface ISharePointTestStateProtector
{
    string Protect(string userId, SharePointConnectionRequest request, SharePointConnectionResult result, string clientSecret,
        DateTimeOffset now);

    bool TryUnprotect(string token, string userId, out SharePointTestState? state, DateTimeOffset now);
}

/// <summary>
/// Keeps a short-lived, tamper-protected result of Test Connection. It carries
/// list IDs and site metadata, never the OAuth token or client secret.
/// </summary>
public sealed class SharePointTestStateProtector(IDataProtectionProvider dataProtectionProvider)
    : ISharePointTestStateProtector
{
    private const string Purpose = "SplitBill.SharePoint.TestState.v1";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private readonly IDataProtector protector = dataProtectionProvider.CreateProtector(Purpose);

    public string Protect(
        string userId,
        SharePointConnectionRequest request,
        SharePointConnectionResult result,
        string clientSecret,
        DateTimeOffset now)
    {
        var normalizedSite = SharePointUrlNormalizer.Normalize(request.SiteUrl).ToString();
        var tenantId = Guid.Parse(request.TenantId.Trim()).ToString("D");
        var clientId = Guid.Parse(request.ClientId.Trim()).ToString("D");
        var state = new SharePointTestState(
            userId,
            tenantId,
            clientId,
            normalizedSite,
            result.SiteId,
            result.SiteDisplayName,
            result.SiteWebUrl,
            Fingerprint(tenantId, clientId, normalizedSite, clientSecret),
            now,
            now.Add(Lifetime),
            result.Lists);
        return protector.Protect(JsonSerializer.Serialize(state));
    }

    public bool TryUnprotect(string token, string userId, out SharePointTestState? state, DateTimeOffset now)
    {
        state = null;
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(userId)) return false;

        try
        {
            var json = protector.Unprotect(token);
            state = JsonSerializer.Deserialize<SharePointTestState>(json);
            return state is not null &&
                   string.Equals(state.UserId, userId, StringComparison.Ordinal) &&
                   state.ExpiresAt > now &&
                   state.Lists is not null;
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or NotSupportedException)
        {
            return false;
        }
    }

    public static string CreateFingerprint(string tenantId, string clientId, string siteUrl, string clientSecret)
        => Fingerprint(
            Guid.Parse(tenantId.Trim()).ToString("D"),
            Guid.Parse(clientId.Trim()).ToString("D"),
            SharePointUrlNormalizer.Normalize(siteUrl).ToString(),
            clientSecret);

    private static string Fingerprint(string tenantId, string clientId, string siteUrl, string clientSecret)
    {
        var input = string.Join("\n", tenantId, clientId, siteUrl, clientSecret.Trim());
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));
    }
}
