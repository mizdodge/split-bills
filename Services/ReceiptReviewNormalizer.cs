using Splitbill.ViewModels;
using Splitbill.Models;

namespace Splitbill.Services;

public static class ReceiptReviewNormalizer
{
    public static void Normalize(ReviewTransactionViewModel model)
    {
        model.MerchantName = string.IsNullOrWhiteSpace(model.MerchantName)
            ? "Merchant tidak diketahui"
            : model.MerchantName.Trim();

        model.Items = model.Items
            .Where(item => !string.IsNullOrWhiteSpace(item.Name) ||
                           item.UnitPrice.GetValueOrDefault() > 0 ||
                           item.TotalPrice.GetValueOrDefault() > 0)
            .ToList();

        for (var index = 0; index < model.Items.Count; index++)
        {
            var item = model.Items[index];
            item.Name = string.IsNullOrWhiteSpace(item.Name) ? $"Item {index + 1}" : item.Name.Trim();
            var quantity = item.Quantity.GetValueOrDefault();
            item.Quantity = quantity > 0 ? Math.Max(1, quantity) : 1;
            item.UnitPrice = Math.Max(0, item.UnitPrice ?? 0);
            item.TotalPrice = Math.Max(0, item.TotalPrice ?? 0);

            if (item.TotalPrice == 0 && item.UnitPrice > 0)
                item.TotalPrice = item.Quantity * item.UnitPrice;
            else if (item.UnitPrice == 0 && item.TotalPrice > 0)
                item.UnitPrice = item.TotalPrice / item.Quantity;
        }

        if (model.Charges.Count == 0)
        {
            AddLegacyCharge(model.Charges, "Diskon", model.Discount, ChargeOperation.Subtract);
            AddLegacyCharge(model.Charges, "Pajak", model.Tax, ChargeOperation.Add);
            AddLegacyCharge(model.Charges, "Service charge", model.ServiceCharge, ChargeOperation.Add);
        }

        model.Charges = model.Charges
            .Where(charge => !string.IsNullOrWhiteSpace(charge.Label) || charge.Amount.GetValueOrDefault() > 0)
            .Select((charge, index) => new ReviewChargeViewModel
            {
                Id = charge.Id,
                Label = string.IsNullOrWhiteSpace(charge.Label) ? $"Biaya lainnya {index + 1}" : charge.Label.Trim(),
                Amount = Math.Max(0, charge.Amount ?? 0),
                Operation = charge.Operation
            })
            .Where(charge => charge.Amount > 0)
            .ToList();

        model.Discount = model.Charges
            .Where(charge => charge.Operation == ChargeOperation.Subtract)
            .Sum(charge => charge.Amount ?? 0);
        model.Tax = model.Charges
            .Where(charge => charge.Operation == ChargeOperation.Add && IsTaxLabel(charge.Label))
            .Sum(charge => charge.Amount ?? 0);
        model.ServiceCharge = model.Charges
            .Where(charge => charge.Operation == ChargeOperation.Add && !IsTaxLabel(charge.Label))
            .Sum(charge => charge.Amount ?? 0);

        var itemTotal = model.Items.Sum(item => item.TotalPrice ?? 0);
        var enteredSubtotal = model.Subtotal.GetValueOrDefault();
        model.Subtotal = enteredSubtotal > 0 ? enteredSubtotal : itemTotal;
        var chargeTotal = model.Charges.Sum(charge =>
            charge.Operation == ChargeOperation.Subtract ? -(charge.Amount ?? 0) : charge.Amount ?? 0);
        var calculatedTotal = Math.Max(0, model.Subtotal.GetValueOrDefault() + chargeTotal);
        var enteredGrandTotal = model.GrandTotal.GetValueOrDefault();
        model.GrandTotal = enteredGrandTotal > 0
            ? enteredGrandTotal
            : calculatedTotal;

        if (model.Items.Count == 0 && model.GrandTotal > 0)
        {
            if (model.Subtotal <= 0)
                model.Subtotal = Math.Max(0, model.GrandTotal.GetValueOrDefault() - chargeTotal);
            var baseAmount = model.Subtotal.GetValueOrDefault();
            model.Items.Add(new ReviewItemViewModel
            {
                Name = "Total tagihan",
                Quantity = 1,
                UnitPrice = baseAmount,
                TotalPrice = baseAmount
            });
        }
    }

    public static decimal? CalculateEffectivePercentage(decimal amount, decimal subtotal) =>
        subtotal > 0 ? Math.Round(amount / subtotal * 100, 2, MidpointRounding.AwayFromZero) : null;

    public static bool IsTaxLabel(string? label)
    {
        var normalized = label?.Trim().ToLowerInvariant() ?? string.Empty;
        return normalized.Contains("tax") || normalized.Contains("pajak") || normalized.Contains("ppn") || normalized.Contains("pb1");
    }

    private static void AddLegacyCharge(
        ICollection<ReviewChargeViewModel> charges,
        string label,
        decimal? amount,
        ChargeOperation operation)
    {
        if (amount.GetValueOrDefault() <= 0) return;
        charges.Add(new ReviewChargeViewModel { Label = label, Amount = amount, Operation = operation });
    }
}
