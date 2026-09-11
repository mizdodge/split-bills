using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class MultipleReceiptImageTests
{
    [Theory]
    [InlineData(AiApiMode.Responses, "input_image")]
    [InlineData(AiApiMode.ChatCompletions, "image_url")]
    public void AiRequestContainsEveryImageInOriginalOrder(AiApiMode mode, string imageType)
    {
        var settings = new AiConfiguration { Provider = AiProvider.OpenAi, Model = "vision-test", ApiMode = mode };
        var body = AiReceiptService.BuildRequestBody(settings,
            ["data:image/jpeg;base64,ONE", "data:image/png;base64,TWO", "data:image/webp;base64,THREE"]);

        var serialized = body.ToJsonString();
        Assert.Equal(3, CountValues(body, "type", imageType));
        Assert.True(serialized.IndexOf("ONE", StringComparison.Ordinal) < serialized.IndexOf("TWO", StringComparison.Ordinal));
        Assert.True(serialized.IndexOf("TWO", StringComparison.Ordinal) < serialized.IndexOf("THREE", StringComparison.Ordinal));
        Assert.Contains("one receipt", serialized);
    }

    [Fact]
    public void ValidatorAcceptsFiveSupportedImagesWithinTotalLimit()
    {
        var images = Enumerable.Range(0, 5).Select(index => File(100, $"receipt-{index}.jpg", "image/jpeg")).ToList();
        Assert.Null(ReceiptUploadValidator.Validate(images));
    }

    [Fact]
    public void ValidatorRejectsMoreThanFiveImages() =>
        Assert.Contains("Maksimal 5", ReceiptUploadValidator.Validate(
            Enumerable.Range(0, 6).Select(index => File(1, $"{index}.jpg", "image/jpeg")).ToList()));

    [Fact]
    public void ValidatorRejectsOversizedIndividualImage() =>
        Assert.Contains("setiap foto maksimal", ReceiptUploadValidator.Validate(
            [File(ReceiptUploadValidator.MaximumImageBytes + 1, "large.jpg", "image/jpeg")]));

    [Fact]
    public void ValidatorRejectsOversizedCombinedUpload() =>
        Assert.Contains("Total ukuran", ReceiptUploadValidator.Validate(
            Enumerable.Range(0, 4).Select(index => File(8_000_000, $"{index}.png", "image/png")).ToList()));

    [Theory]
    [InlineData("application/pdf")]
    [InlineData("image/gif")]
    [InlineData("application/octet-stream")]
    public void ValidatorLeavesFormatDecodingToImageProcessor(string contentType) =>
        Assert.Null(ReceiptUploadValidator.Validate([File(1, "receipt.bin", contentType)]));

    [Fact]
    public void ValidatorRejectsEmptySelection() =>
        Assert.Contains("minimal satu", ReceiptUploadValidator.Validate([]));

    private static IFormFile File(long length, string name, string contentType) =>
        new FormFile(Stream.Null, 0, length, "ReceiptImages", name) { Headers = new HeaderDictionary(), ContentType = contentType };

    private static int CountValues(JsonNode? node, string propertyName, string expected)
    {
        if (node is JsonObject obj)
            return obj.Sum(pair => (pair.Key == propertyName && pair.Value is JsonValue value &&
                                    value.TryGetValue<string>(out var text) && text == expected ? 1 : 0) +
                                   CountValues(pair.Value, propertyName, expected));
        return node is JsonArray array ? array.Sum(child => CountValues(child, propertyName, expected)) : 0;
    }
}
