namespace Splitbill.Tests;

public sealed class ShareMarkupTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    [Fact]
    public void DetailsAndMemberViewsExposeScopedShareActions()
    {
        var details = File.ReadAllText(Path.Combine(Root, "Views", "Transactions", "Details.cshtml"));
        var member = File.ReadAllText(Path.Combine(Root, "Views", "MyBills", "Details.cshtml"));
        Assert.Contains("data-share-endpoint", details);
        Assert.Contains("ShareData", details);
        Assert.Contains("data-share-open", details);
        Assert.Contains("data-share-endpoint", member);
        Assert.Contains("MyBills", member);
    }

    [Fact]
    public void ShareActionsUseLocalRendererAndNoRemoteRuntimeDependency()
    {
        var layout = File.ReadAllText(Path.Combine(Root, "Views", "Shared", "_Layout.cshtml"));
        var script = File.ReadAllText(Path.Combine(Root, "wwwroot", "js", "share-actions.js"));
        Assert.Contains("transaction-share.js", layout);
        Assert.Contains("share-actions.js", layout);
        Assert.DoesNotContain("https://", script, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("html2canvas", script, StringComparison.OrdinalIgnoreCase);
    }
}
