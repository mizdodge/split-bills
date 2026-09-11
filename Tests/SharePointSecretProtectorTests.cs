using Microsoft.AspNetCore.DataProtection;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class SharePointSecretProtectorTests
{
    [Fact]
    public void ProtectRoundTripsWithoutStoringPlaintext()
    {
        var protector = new SharePointSecretProtector(new EphemeralDataProtectionProvider());

        var ciphertext = protector.Protect("super-secret-value");

        Assert.NotEqual("super-secret-value", ciphertext);
        Assert.Equal("super-secret-value", protector.Unprotect(ciphertext));
        Assert.DoesNotContain("super-secret-value", ciphertext, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptySecretIsRejected()
    {
        var protector = new SharePointSecretProtector(new EphemeralDataProtectionProvider());

        Assert.Throws<ArgumentException>(() => protector.Protect("  "));
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(""));
    }

    [Fact]
    public void DedicatedPurposeCannotOpenAiCiphertext()
    {
        var provider = new EphemeralDataProtectionProvider();
        var aiCiphertext = provider.CreateProtector(AiApiKeyProtection.Purpose).Protect("secret");
        var sharePoint = new SharePointSecretProtector(provider);

        Assert.Throws<InvalidOperationException>(() => sharePoint.Unprotect(aiCiphertext));
    }
}
