using Microsoft.AspNetCore.DataProtection;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class SharePointTestStateProtectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 8, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StateIsBoundToAdminAndConfigurationAndContainsNoSecret()
    {
        var protector = new SharePointTestStateProtector(new EphemeralDataProtectionProvider());
        var request = Request("secret-value");
        var result = Result();
        var token = protector.Protect("admin-1", request, result, "secret-value", Now);

        Assert.True(protector.TryUnprotect(token, "admin-1", out var state, Now.AddMinutes(1)));
        Assert.NotNull(state);
        Assert.Equal("tenant.sharepoint.com,site,web", state!.SiteId);
        Assert.DoesNotContain("secret-value", token, StringComparison.Ordinal);
        Assert.False(protector.TryUnprotect(token, "admin-2", out _, Now.AddMinutes(1)));
        Assert.False(protector.TryUnprotect(token, "admin-1", out _, Now.AddMinutes(11)));
    }

    [Fact]
    public void FingerprintChangesWhenSecretOrSiteChanges()
    {
        var first = SharePointTestStateProtector.CreateFingerprint(
            Guid.Empty.ToString(), Guid.NewGuid().ToString(), "https://tenant.sharepoint.com/sites/Office", "one");
        var changedSecret = SharePointTestStateProtector.CreateFingerprint(
            Guid.Empty.ToString(), Guid.NewGuid().ToString(), "https://tenant.sharepoint.com/sites/Office", "two");

        Assert.NotEqual(first, changedSecret);
    }

    private static SharePointConnectionRequest Request(string secret)
        => new(Guid.Empty.ToString(), Guid.NewGuid().ToString(), secret, "https://tenant.sharepoint.com/sites/Office/");

    private static SharePointConnectionResult Result()
        => new("tenant.sharepoint.com,site,web", "Office", "https://tenant.sharepoint.com/sites/Office",
            [new SharePointListOption("list-1", "SplitBill Notifications", null)]);
}
