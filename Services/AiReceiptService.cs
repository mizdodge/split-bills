using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;

public interface IAiReceiptService
{
    Task<(ReceiptAiResult Result, string RawResponse)> AnalyzeAsync(
        IReadOnlyList<ReceiptImageInput> images,
        CancellationToken cancellationToken = default);

    Task TestConnectionAsync(AiConfiguration configuration, CancellationToken cancellationToken = default);
}

public sealed record ReceiptImageInput(Stream Stream, string ContentType);

public sealed class AiReceiptService(
    ApplicationDbContext db,
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider) : IAiReceiptService
{
    private const string SystemPrompt = """
        You extract structured data from receipts. Return only data matching the supplied JSON schema.
        Monetary values must be plain numbers without separators or currency symbols, in the currency printed on the receipt.
        Set currency to the three-letter ISO 4217 code you can read from the receipt; use IDR only when the receipt is clearly Indonesian or the currency is unreadable.
        Put every non-item amount in charges as its own row, preserving the printed label such as PB1, PPN, service charge,
        packaging, delivery fee, voucher, discount, or any other receipt-specific adjustment. Use operation "add" for fees and
        "subtract" for discounts. The amount is always the exact positive nominal printed on the receipt, never a percentage.
        Do not include charge or discount rows in items, and never count the same amount twice.
        Ignore percentage labels such as 9.5%, 10%, or 11% when an associated monetary amount is printed. Never calculate an
        amount from a percentage. If only a percentage is visible without its nominal amount, omit that charge, set needsReview
        to true, and add an Indonesian warning asking the user to enter the printed nominal manually.
        Set needsReview to true and add a short Indonesian warning when a value is uncertain or totals do not match.
        """;

    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(AiApiKeyProtection.Purpose);

    public async Task<(ReceiptAiResult Result, string RawResponse)> AnalyzeAsync(
        IReadOnlyList<ReceiptImageInput> images,
        CancellationToken cancellationToken = default)
    {
        if (images.Count == 0) throw new AiServiceException("Minimal satu foto struk diperlukan.");
        var settings = await db.AiConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.IsActive, cancellationToken)
                       ?? throw new AiServiceException("Konfigurasi AI belum diatur oleh Admin.");

        var dataUrls = new List<string>(images.Count);
        foreach (var image in images)
        {
            await using var buffer = new MemoryStream();
            await image.Stream.CopyToAsync(buffer, cancellationToken);
            dataUrls.Add($"data:{image.ContentType};base64,{Convert.ToBase64String(buffer.ToArray())}");
        }
        var raw = await SendAsync(settings, dataUrls, cancellationToken);
        var result = AiResponseParser.Parse(raw, settings.ApiMode);
        ValidateAndNormalize(result);
        return (result, raw);
    }

    public async Task TestConnectionAsync(AiConfiguration settings, CancellationToken cancellationToken = default)
    {
        var raw = await SendAsync(settings, [], cancellationToken);
        _ = AiResponseParser.Parse(raw, settings.ApiMode);
    }

    private async Task<string> SendAsync(AiConfiguration settings, IReadOnlyList<string> imageDataUrls, CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(settings);
        using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
        ApplyAuthentication(request, settings);
        request.Content = new StringContent(
            BuildRequestBody(settings, imageDataUrls).ToJsonString(),
            Encoding.UTF8,
            "application/json");

        try
        {
            var client = httpClientFactory.CreateClient(nameof(AiReceiptService));
            using var response = await client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var conciseError = responseBody.Length > 700 ? responseBody[..700] : responseBody;
                throw new AiServiceException($"AI mengembalikan {(int)response.StatusCode}: {conciseError}");
            }

            return responseBody;
        }
        catch (AiServiceException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new AiServiceException("Tidak dapat terhubung ke layanan AI. Periksa endpoint, model, dan API key.", exception);
        }
    }

    private Uri BuildRequestUri(AiConfiguration settings)
    {
        if (settings.Provider == AiProvider.OpenAi)
        {
            var openAiPath = settings.ApiMode == AiApiMode.Responses ? "responses" : "chat/completions";
            return new Uri($"https://api.openai.com/v1/{openAiPath}");
        }

        if (string.IsNullOrWhiteSpace(settings.Endpoint))
            throw new AiServiceException("Endpoint Azure OpenAI wajib diisi.");
        if (string.IsNullOrWhiteSpace(settings.DeploymentName))
            throw new AiServiceException("Deployment Azure OpenAI wajib diisi.");
        if (string.IsNullOrWhiteSpace(settings.ApiVersion))
            throw new AiServiceException("API version Azure OpenAI wajib diisi.");

        var endpoint = settings.Endpoint.TrimEnd('/');
        var azurePath = settings.ApiMode == AiApiMode.Responses
            ? $"/openai/v1/responses?api-version={Uri.EscapeDataString(settings.ApiVersion)}"
            : $"/openai/deployments/{Uri.EscapeDataString(settings.DeploymentName)}/chat/completions?api-version={Uri.EscapeDataString(settings.ApiVersion)}";
        return new Uri(endpoint + azurePath);
    }

    private void ApplyAuthentication(HttpRequestMessage request, AiConfiguration settings)
        => AiApiKeyProtection.ApplyAuthentication(request, settings.Provider, settings.ProtectedApiKey, _protector);

    internal static JsonObject BuildRequestBody(AiConfiguration settings, IReadOnlyList<string> imageDataUrls)
    {
        var userText = imageDataUrls.Count == 0
            ? "Return a valid empty receipt object for a connection test."
            : $"These {imageDataUrls.Count} ordered images are pages or sections of one receipt. Extract every visible line item and the final receipt totals once; do not duplicate overlapping lines.";
        var schema = BuildReceiptSchema();

        if (settings.ApiMode == AiApiMode.ChatCompletions)
        {
            var content = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = userText } };
            foreach (var imageDataUrl in imageDataUrls)
            {
                content.Add(new JsonObject
                {
                    ["type"] = "image_url",
                    ["image_url"] = new JsonObject { ["url"] = imageDataUrl, ["detail"] = "high" }
                });
            }

            return new JsonObject
            {
                ["model"] = settings.Provider == AiProvider.AzureOpenAi ? settings.DeploymentName : settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = SystemPrompt },
                    new JsonObject { ["role"] = "user", ["content"] = content }
                },
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = "receipt", ["strict"] = true, ["schema"] = schema.DeepClone() }
                }
            };
        }

        var inputContent = new JsonArray { new JsonObject { ["type"] = "input_text", ["text"] = userText } };
        foreach (var imageDataUrl in imageDataUrls)
            inputContent.Add(new JsonObject { ["type"] = "input_image", ["image_url"] = imageDataUrl, ["detail"] = "high" });

        return new JsonObject
        {
            ["model"] = settings.Provider == AiProvider.AzureOpenAi ? settings.DeploymentName : settings.Model,
            ["instructions"] = SystemPrompt,
            ["input"] = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = inputContent } },
            ["text"] = new JsonObject
            {
                ["format"] = new JsonObject
                {
                    ["type"] = "json_schema", ["name"] = "receipt", ["strict"] = true, ["schema"] = schema.DeepClone()
                }
            }
        };
    }

    private static JsonObject BuildReceiptSchema()
    {
        var number = new JsonObject { ["type"] = "number" };
        return new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("schemaVersion", "merchantName", "transactionDate", "currency", "items", "charges", "subtotal", "grandTotal", "confidence", "needsReview", "warnings"),
            ["properties"] = new JsonObject
            {
                ["schemaVersion"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("2.0") },
                ["merchantName"] = new JsonObject { ["type"] = new JsonArray("string", "null") },
                ["transactionDate"] = new JsonObject { ["type"] = new JsonArray("string", "null"), ["description"] = "ISO date YYYY-MM-DD" },
                ["currency"] = new JsonObject { ["type"] = "string" },
                ["items"] = new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["required"] = new JsonArray("lineNumber", "name", "quantity", "unitPrice", "totalPrice", "confidence"),
                        ["properties"] = new JsonObject
                        {
                            ["lineNumber"] = new JsonObject { ["type"] = "integer" },
                            ["name"] = new JsonObject { ["type"] = "string" },
                            ["quantity"] = number.DeepClone(), ["unitPrice"] = number.DeepClone(),
                            ["totalPrice"] = number.DeepClone(), ["confidence"] = number.DeepClone()
                        }
                    }
                },
                ["charges"] = new JsonObject
                {
                    ["type"] = "array",
                    ["description"] = "Every printed non-item amount as a separate named nominal adjustment.",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object", ["additionalProperties"] = false,
                        ["required"] = new JsonArray("label", "amount", "operation"),
                        ["properties"] = new JsonObject
                        {
                            ["label"] = new JsonObject { ["type"] = "string", ["description"] = "Label exactly as printed on the receipt." },
                            ["amount"] = new JsonObject { ["type"] = "number", ["description"] = "Exact positive IDR nominal, never a rate or percentage." },
                            ["operation"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("add", "subtract") }
                        }
                    }
                },
                ["subtotal"] = number.DeepClone(),
                ["grandTotal"] = number.DeepClone(), ["confidence"] = number.DeepClone(),
                ["needsReview"] = new JsonObject { ["type"] = "boolean" },
                ["warnings"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } }
            }
        };
    }

    private static void ValidateAndNormalize(ReceiptAiResult result)
    {
        for (var index = 0; index < result.Items.Count; index++)
        {
            var item = result.Items[index];
            item.LineNumber = item.LineNumber <= 0 ? index + 1 : item.LineNumber;
            item.Name = string.IsNullOrWhiteSpace(item.Name) ? $"Item {index + 1}" : item.Name.Trim();
            if (item.Quantity <= 0) item.Quantity = 1;
            if (item.TotalPrice <= 0 && item.UnitPrice > 0) item.TotalPrice = item.UnitPrice * item.Quantity;
        }

        result.Charges = result.Charges
            .Where(charge => !string.IsNullOrWhiteSpace(charge.Label) && charge.Amount > 0)
            .Select(charge => new ReceiptChargeAiResult
            {
                Label = charge.Label.Trim(),
                Amount = charge.Amount,
                Operation = charge.Operation.Equals("subtract", StringComparison.OrdinalIgnoreCase) ? "subtract" : "add"
            }).ToList();
        if (result.Charges.Count == 0)
        {
            if (result.Discount > 0) result.Charges.Add(new ReceiptChargeAiResult { Label = "Diskon", Amount = result.Discount, Operation = "subtract" });
            if (result.Tax > 0) result.Charges.Add(new ReceiptChargeAiResult { Label = "Pajak", Amount = result.Tax, Operation = "add" });
            if (result.ServiceCharge > 0) result.Charges.Add(new ReceiptChargeAiResult { Label = "Service charge", Amount = result.ServiceCharge, Operation = "add" });
        }

        var expected = result.Subtotal + result.Charges.Sum(charge =>
            charge.Operation == "subtract" ? -charge.Amount : charge.Amount);
        if (Math.Abs(expected - result.GrandTotal) > 1)
        {
            result.NeedsReview = true;
            result.Warnings.Add("Subtotal dan rincian biaya tambahan tidak cocok dengan grand total.");
        }

        if (result.Items.Count == 0)
        {
            result.NeedsReview = true;
            result.Warnings.Add("AI tidak menemukan item pada struk.");
        }
    }
}
