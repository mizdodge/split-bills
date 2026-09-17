using Microsoft.AspNetCore.DataProtection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Splitbill.Services;

public sealed class MicrosoftLinkStateProtector(IDataProtectionProvider provider) : IMicrosoftLinkStateProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("Splitbill.MicrosoftIntegration.LinkState.v1");

    public string Protect(MicrosoftLinkState state) => protector.Protect(JsonSerializer.Serialize(state));
    public MicrosoftLinkState Unprotect(string value) => JsonSerializer.Deserialize<MicrosoftLinkState>(protector.Unprotect(value))
        ?? throw new InvalidOperationException("The Microsoft link state is invalid.");

    public static string Hash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash);
    }
}

public sealed record MicrosoftLinkState(string IntentId, string UserId, string Nonce);
public interface IMicrosoftLinkStateProtector
{
    string Protect(MicrosoftLinkState state);
    MicrosoftLinkState Unprotect(string value);
}
