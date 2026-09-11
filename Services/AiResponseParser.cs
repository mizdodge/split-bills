using System.Text.Json;
using Splitbill.Models;

namespace Splitbill.Services;

public static class AiResponseParser
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static ReceiptAiResult Parse(string responseJson, AiApiMode mode)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;
        string? content = null;

        if (mode == AiApiMode.ChatCompletions &&
            root.TryGetProperty("choices", out var choices) &&
            choices.GetArrayLength() > 0)
        {
            var message = choices[0].GetProperty("message");
            if (message.TryGetProperty("content", out var messageContent))
            {
                content = messageContent.ValueKind == JsonValueKind.String
                    ? messageContent.GetString()
                    : ReadTextArray(messageContent);
            }
        }
        else
        {
            if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
            {
                content = outputText.GetString();
            }
            else if (root.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Array)
            {
                foreach (var outputItem in output.EnumerateArray())
                {
                    if (!outputItem.TryGetProperty("content", out var parts)) continue;
                    content = ReadTextArray(parts);
                    if (!string.IsNullOrWhiteSpace(content)) break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(content))
            throw new AiServiceException("AI tidak mengembalikan konten yang dapat dibaca.");

        content = StripCodeFence(content);
        var result = JsonSerializer.Deserialize<ReceiptAiResult>(content, SerializerOptions)
                     ?? throw new AiServiceException("JSON hasil AI kosong.");

        result.Items ??= [];
        result.Warnings ??= [];
        return result;
    }

    private static string? ReadTextArray(JsonElement parts)
    {
        if (parts.ValueKind != JsonValueKind.Array) return null;
        foreach (var part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                return text.GetString();
        }

        return null;
    }

    private static string StripCodeFence(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal)) return trimmed;
        var firstLine = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine
            ? trimmed[(firstLine + 1)..lastFence].Trim()
            : trimmed;
    }
}

public sealed class AiServiceException(string message, Exception? innerException = null)
    : Exception(message, innerException);
