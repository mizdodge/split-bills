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
    public void TransactionDetailsUseUnifiedHeaderModalAndKeepJpgUploaderScoped()
    {
        var details = File.ReadAllText(Path.Combine(Root, "Views", "Transactions", "Details.cshtml"));
        Assert.Contains("page-heading-actions", details);
        Assert.Contains("unified-share-tabs", details);
        Assert.Contains("data-share-tab=\"links\"", details);
        Assert.Contains("data-transaction-link-copy-endpoint", details);
        Assert.Contains("data-transaction-link-regenerate-endpoint", details);
        Assert.Contains("data-transaction-link-revoke-endpoint", details);
        Assert.Contains("GuestTransactionAccessLinks", details);
        Assert.DoesNotContain("guest-access-block", details);
        Assert.DoesNotContain("share-export-card", details);
        Assert.Contains("canShareJpg", details);
    }

    [Fact]
    public void UnifiedShareScriptSupportsLazyJpgAndAsyncLinkLifecycle()
    {
        var script = File.ReadAllText(Path.Combine(Root, "wwwroot", "js", "share-actions.js"));
        Assert.Contains("activateJpg", script);
        Assert.Contains("data-link-copy", script);
        Assert.Contains("data-link-regenerate", script);
        Assert.Contains("data-link-revoke", script);
        Assert.Contains("copyValue", script);
        Assert.Contains("legacy", script, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void TransactionGuestLinkActionsRemainAntiforgeryProtected()
    {
        var controller = File.ReadAllText(Path.Combine(Root, "Controllers", "TransactionsController.cs"));
        foreach (var action in new[] { "CopyTransactionGuestLink", "RegenerateTransactionGuestLink", "RevokeTransactionGuestLink", "RegenerateGuestLinkInline", "RevokeGuestLinkInline" })
        {
            var index = controller.IndexOf(action, StringComparison.Ordinal);
            Assert.True(index >= 0, $"Missing action {action}");
            var prefix = controller[Math.Max(0, index - 220)..index];
            Assert.Contains("HttpPost", prefix);
            Assert.Contains("ValidateAntiForgeryToken", prefix);
        }
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
