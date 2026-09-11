using System.Text.Json;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class AiResponseParserTests
{
    private const string ReceiptJson = """
        {"schemaVersion":"1.0","merchantName":"Solaria","transactionDate":"2026-09-03","currency":"IDR","items":[{"lineNumber":1,"name":"Nasi Goreng","quantity":1,"unitPrice":35000,"totalPrice":35000,"confidence":0.98}],"subtotal":35000,"discount":0,"tax":3500,"serviceCharge":0,"grandTotal":38500,"confidence":0.95,"needsReview":false,"warnings":[]}
        """;

    [Fact]
    public void Parse_ReadsChatCompletionsEnvelope()
    {
        var envelope = JsonSerializer.Serialize(new
        {
            choices = new[] { new { message = new { content = ReceiptJson } } }
        });

        var result = AiResponseParser.Parse(envelope, AiApiMode.ChatCompletions);

        Assert.Equal("Solaria", result.MerchantName);
        Assert.Equal(38_500m, result.GrandTotal);
        Assert.Single(result.Items);
    }

    [Fact]
    public void Parse_ReadsResponsesOutputEnvelope()
    {
        var envelope = JsonSerializer.Serialize(new
        {
            output = new[] { new { content = new[] { new { type = "output_text", text = ReceiptJson } } } }
        });

        var result = AiResponseParser.Parse(envelope, AiApiMode.Responses);

        Assert.Equal("Nasi Goreng", result.Items[0].Name);
        Assert.Equal(.95m, result.Confidence);
    }

    [Fact]
    public void Parse_RejectsMissingContent()
    {
        Assert.Throws<AiServiceException>(() => AiResponseParser.Parse("{\"output\":[]}", AiApiMode.Responses));
    }

    [Fact]
    public void Parse_ReadsDynamicNamedCharges()
    {
        const string receipt = """
            {"schemaVersion":"1.0","merchantName":"Cafe","transactionDate":null,"currency":"IDR","items":[],"charges":[{"label":"PB1","amount":9758,"operation":"add"},{"label":"Voucher","amount":5000,"operation":"subtract"}],"subtotal":100000,"grandTotal":104758,"confidence":0.9,"needsReview":false,"warnings":[]}
            """;
        var envelope = JsonSerializer.Serialize(new
        {
            output = new[] { new { content = new[] { new { type = "output_text", text = receipt } } } }
        });

        var result = AiResponseParser.Parse(envelope, AiApiMode.Responses);

        Assert.Equal("PB1", result.Charges[0].Label);
        Assert.Equal(9_758m, result.Charges[0].Amount);
        Assert.Equal("subtract", result.Charges[1].Operation);
    }
}
