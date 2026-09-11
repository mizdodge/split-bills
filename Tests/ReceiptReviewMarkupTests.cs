namespace Splitbill.Tests;

public sealed class ReceiptReviewMarkupTests
{
    [Fact]
    public void QuantityInputsUseWholeNumbersWhileMoneyInputsKeepDecimals()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        var markup = File.ReadAllText(Path.Combine(repositoryRoot, "Views", "Transactions", "Review.cshtml"));

        Assert.Contains("min=\"1\" step=\"1\"", markup);
        Assert.Contains("asp-for=\"GrandTotal\" type=\"number\" step=\"0.01\"", markup);
        Assert.Contains("asp-for=\"Charges[i].Amount\" class=\"form-control charge-amount\" type=\"number\" step=\"0.01\"", markup);
    }
}
