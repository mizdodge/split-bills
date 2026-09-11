using Microsoft.AspNetCore.DataProtection;

namespace Splitbill.Services;

public static class SharePointProtection
{
    public const string ClientSecretPurpose = "SplitBill.SharePoint.ClientSecret.v1";
}

/// <summary>
/// Protects the Microsoft Entra client secret at rest. The secret is never
/// returned to a view or included in a tested-connection payload.
/// </summary>
public sealed class SharePointSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector protector =
        dataProtectionProvider.CreateProtector(SharePointProtection.ClientSecretPurpose);

    public string Protect(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Client secret tidak boleh kosong.", nameof(value));

        return protector.Protect(value.Trim());
    }

    public string Unprotect(string protectedValue)
    {
        if (string.IsNullOrWhiteSpace(protectedValue))
            throw new InvalidOperationException("SharePoint client secret belum tersedia.");

        try
        {
            return protector.Unprotect(protectedValue);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "SharePoint client secret tidak dapat dibuka. Pastikan folder data-protection-keys ikut dipertahankan saat update.",
                exception);
        }
    }
}
