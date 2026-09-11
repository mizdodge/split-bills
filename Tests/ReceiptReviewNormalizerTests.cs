using Splitbill.Services;
using Splitbill.ViewModels;
using Splitbill.Models;

namespace Splitbill.Tests;

public sealed class ReceiptReviewNormalizerTests
{
    [Fact]
    public void Normalize_UsesExactTaxNominalWithoutAssumingAPercentage()
    {
        var model = new ReviewTransactionViewModel
        {
            Subtotal = 123_456,
            Tax = 11_728,
            ServiceCharge = 4_321
        };

        ReceiptReviewNormalizer.Normalize(model);

        Assert.Equal(11_728m, model.Tax);
        Assert.Equal(139_505m, model.GrandTotal);
    }

    [Fact]
    public void Normalize_SummarizesArbitraryNamedChargesAndDiscounts()
    {
        var model = new ReviewTransactionViewModel
        {
            Subtotal = 100_000,
            Charges =
            [
                new ReviewChargeViewModel { Label = "PB1", Amount = 9_758, Operation = ChargeOperation.Add },
                new ReviewChargeViewModel { Label = "Packaging", Amount = 2_000, Operation = ChargeOperation.Add },
                new ReviewChargeViewModel { Label = "Voucher", Amount = 5_000, Operation = ChargeOperation.Subtract }
            ]
        };

        ReceiptReviewNormalizer.Normalize(model);

        Assert.Equal(9_758m, model.Tax);
        Assert.Equal(2_000m, model.ServiceCharge);
        Assert.Equal(5_000m, model.Discount);
        Assert.Equal(106_758m, model.GrandTotal);
        Assert.Equal(100_000m, model.Items[0].TotalPrice);
        Assert.Equal(9.76m, ReceiptReviewNormalizer.CalculateEffectivePercentage(9_758, 100_000));
    }

    [Fact]
    public void Normalize_LeavesUnknownTotalAtZeroSoSubmissionCanAskForAnAmount()
    {
        var model = new ReviewTransactionViewModel();
        ReceiptReviewNormalizer.Normalize(model);
        Assert.Equal(0m, model.GrandTotal);
        Assert.Empty(model.Items);
    }

    [Fact]
    public void Normalize_PreservesAnEnteredGrandTotal()
    {
        var model = new ReviewTransactionViewModel
        {
            GrandTotal = 29_500,
            Items = [new ReviewItemViewModel { Name = "Makan", Quantity = 1, UnitPrice = 30_000 }]
        };
        ReceiptReviewNormalizer.Normalize(model);
        Assert.Equal(29_500m, model.GrandTotal);
    }

    [Fact]
    public void Normalize_InfersMissingItemAndReceiptTotals()
    {
        var model = new ReviewTransactionViewModel
        {
            Items = [new ReviewItemViewModel { Name = "Nasi Goreng", Quantity = 2, UnitPrice = 25_000 }],
            Tax = 5_000
        };

        ReceiptReviewNormalizer.Normalize(model);

        Assert.Equal(50_000m, model.Items[0].TotalPrice);
        Assert.Equal(50_000m, model.Subtotal);
        Assert.Equal(55_000m, model.GrandTotal);
    }

    [Fact]
    public void Normalize_AllowsOnlyGrandTotalAndCreatesFallbackItem()
    {
        var model = new ReviewTransactionViewModel { GrandTotal = 80_500 };

        ReceiptReviewNormalizer.Normalize(model);

        Assert.Equal("Merchant tidak diketahui", model.MerchantName);
        Assert.Single(model.Items);
        Assert.Equal("Total tagihan", model.Items[0].Name);
        Assert.Equal(80_500m, model.Items[0].TotalPrice);
    }

    [Fact]
    public void Normalize_DropsFullyBlankRowsAndDefaultsPartialRows()
    {
        var model = new ReviewTransactionViewModel
        {
            GrandTotal = 10_000,
            Items = [new ReviewItemViewModel { Quantity = 1 }, new ReviewItemViewModel { Name = "Es Teh", TotalPrice = 10_000 }]
        };

        ReceiptReviewNormalizer.Normalize(model);

        Assert.Single(model.Items);
        Assert.Equal(1, model.Items[0].Quantity);
        Assert.Equal(10_000m, model.Items[0].UnitPrice);
    }
}
