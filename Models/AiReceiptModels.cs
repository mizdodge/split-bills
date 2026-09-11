using System.Text.Json.Serialization;

namespace Splitbill.Models;

public sealed class ReceiptAiResult
{
    [JsonPropertyName("schemaVersion")] public string SchemaVersion { get; set; } = "2.0";
    [JsonPropertyName("merchantName")] public string? MerchantName { get; set; }
    [JsonPropertyName("transactionDate")] public string? TransactionDate { get; set; }
    [JsonPropertyName("currency")] public string Currency { get; set; } = "IDR";
    [JsonPropertyName("items")] public List<ReceiptItemAiResult> Items { get; set; } = [];
    [JsonPropertyName("charges")] public List<ReceiptChargeAiResult> Charges { get; set; } = [];
    [JsonPropertyName("subtotal")] public decimal Subtotal { get; set; }
    [JsonPropertyName("discount")] public decimal Discount { get; set; }
    [JsonPropertyName("tax")] public decimal Tax { get; set; }
    [JsonPropertyName("serviceCharge")] public decimal ServiceCharge { get; set; }
    [JsonPropertyName("grandTotal")] public decimal GrandTotal { get; set; }
    [JsonPropertyName("confidence")] public decimal Confidence { get; set; }
    [JsonPropertyName("needsReview")] public bool NeedsReview { get; set; }
    [JsonPropertyName("warnings")] public List<string> Warnings { get; set; } = [];
}

public sealed class ReceiptChargeAiResult
{
    [JsonPropertyName("label")] public string Label { get; set; } = string.Empty;
    [JsonPropertyName("amount")] public decimal Amount { get; set; }
    [JsonPropertyName("operation")] public string Operation { get; set; } = "add";
}

public sealed class ReceiptItemAiResult
{
    [JsonPropertyName("lineNumber")] public int LineNumber { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
    [JsonPropertyName("quantity")] public decimal Quantity { get; set; }
    [JsonPropertyName("unitPrice")] public decimal UnitPrice { get; set; }
    [JsonPropertyName("totalPrice")] public decimal TotalPrice { get; set; }
    [JsonPropertyName("confidence")] public decimal Confidence { get; set; }
}
