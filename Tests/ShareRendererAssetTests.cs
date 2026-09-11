namespace Splitbill.Tests;

public sealed class ShareRendererAssetTests
{
    [Fact]
    public void LongShareRendererIsLocalCanvasOnlyAndViewportIndependent()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../wwwroot/js/transaction-share.js"));
        var source = File.ReadAllText(path);
        Assert.Contains("canvas.width = WIDTH", source);
        Assert.Contains("canvas.height = Math.ceil(measured.height)", source);
        Assert.Contains("const summaryHeight = 168 + receiptRowsHeight", source);
        Assert.Contains("toBlob", source);
        Assert.DoesNotContain("window.innerHeight", source, StringComparison.Ordinal);
        Assert.DoesNotContain("html2canvas", source, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("https://", source, StringComparison.OrdinalIgnoreCase);
    }
}
