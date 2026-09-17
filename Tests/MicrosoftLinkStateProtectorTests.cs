using Microsoft.AspNetCore.DataProtection;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class MicrosoftLinkStateProtectorTests
{
    [Fact]
    public void StateRoundTripsAndHashIsDeterministic()
    {
        var protector = new MicrosoftLinkStateProtector(new EphemeralDataProtectionProvider());
        var state = new MicrosoftLinkState("intent", "user", "nonce");
        var protectedState = protector.Protect(state);
        Assert.Equal(state, protector.Unprotect(protectedState));
        Assert.Equal(MicrosoftLinkStateProtector.Hash("browser"), MicrosoftLinkStateProtector.Hash("browser"));
    }
}
