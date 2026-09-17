using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;

namespace Splitbill.Services;

/// <summary>Dedicated purpose boundary for the single shared Entra client secret.</summary>
public sealed class MicrosoftSecretProtector(IDataProtectionProvider provider) : IMicrosoftSecretProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Splitbill.MicrosoftIntegration.ClientSecret.v1");

    public string Protect(string secret)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("A secret is required.", nameof(secret));
        return protector.Protect(secret.Trim());
    }

    public string Unprotect(string protectedSecret)
    {
        if (string.IsNullOrWhiteSpace(protectedSecret))
            throw new InvalidOperationException("Protected Microsoft client secret is empty.");
        try
        {
            return protector.Unprotect(protectedSecret);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        {
            throw new InvalidOperationException("Protected Microsoft client secret cannot be decrypted.", ex);
        }
    }
}

public interface IMicrosoftSecretProtector
{
    string Protect(string secret);
    string Unprotect(string protectedSecret);
}
