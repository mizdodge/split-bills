namespace Splitbill.Tests;

public sealed class PwaAssetTests
{
    [Fact]
    public void ManifestContainsRoleScopedShortcutsAndStandaloneMode()
    {
        var root = FindRepositoryRoot();
        var manifest = File.ReadAllText(Path.Combine(root, "wwwroot", "manifest.webmanifest"));
        Assert.Contains("\"display\": \"standalone\"", manifest);
        Assert.Contains("/Transactions/Upload", manifest);
        Assert.Contains("/MyBills", manifest);
        Assert.Contains("/Payments/Approvals", manifest);
    }

    [Fact]
    public void RootWorkerNeverCachesPrivateRoutes()
    {
        var worker = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "wwwroot", "push-service-worker.js"));
        Assert.Contains("request.mode === 'navigate'", worker);
        Assert.Contains("/offline.html", worker);
        Assert.DoesNotContain("/MyBills", worker);
        Assert.DoesNotContain("/Transactions/ReceiptImage", worker);
        Assert.Contains("SPLITBILL_SKIP_WAITING", worker);
    }

    private static string FindRepositoryRoot()
    {
        var path = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(path, "Splitbill.csproj")) && Directory.GetParent(path) is { } parent) path = parent.FullName;
        return path;
    }
}
