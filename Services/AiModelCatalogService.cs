using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Splitbill.Models;

namespace Splitbill.Services;

public sealed record AiModelLookup(
    AiProvider Provider,
    string? Endpoint = null,
    string? ApiVersion = null,
    string? ProtectedApiKey = null);

public interface IAiModelCatalogService
{
    Task<IReadOnlyList<string>> ListModelsAsync(
        AiModelLookup lookup,
        CancellationToken cancellationToken = default);
}

public sealed class AiModelCatalogService(
    IHttpClientFactory httpClientFactory,
    IDataProtectionProvider dataProtectionProvider) : IAiModelCatalogService
{
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector(AiApiKeyProtection.Purpose);

    public async Task<IReadOnlyList<string>> ListModelsAsync(
        AiModelLookup lookup,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildRequestUri(lookup));
        ApplyAuthentication(request, lookup);

        try
        {
            var client = httpClientFactory.CreateClient(nameof(AiModelCatalogService));
            using var response = await client.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var conciseError = responseBody.Length > 700 ? responseBody[..700] : responseBody;
                throw new AiServiceException($"Endpoint model mengembalikan {(int)response.StatusCode}: {conciseError}");
            }

            return AiModelCatalogParser.Parse(responseBody);
        }
        catch (AiServiceException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new AiServiceException("Respons daftar model dari provider tidak valid.", exception);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new AiServiceException("Tidak dapat mengambil daftar model dari provider.", exception);
        }
    }

    private static Uri BuildRequestUri(AiModelLookup lookup)
    {
        if (lookup.Provider == AiProvider.OpenAi)
            return new Uri("https://api.openai.com/v1/models");

        if (string.IsNullOrWhiteSpace(lookup.Endpoint))
            throw new AiServiceException("Endpoint Azure OpenAI wajib diisi sebelum mengambil model.");
        if (string.IsNullOrWhiteSpace(lookup.ApiVersion))
            throw new AiServiceException("API version Azure OpenAI wajib diisi sebelum mengambil model.");

        var endpoint = lookup.Endpoint.TrimEnd('/');
        var path = lookup.ApiVersion.Equals("v1", StringComparison.OrdinalIgnoreCase)
            ? "/openai/v1/models"
            : $"/openai/models?api-version={Uri.EscapeDataString(lookup.ApiVersion)}";
        return new Uri(endpoint + path);
    }

    private void ApplyAuthentication(HttpRequestMessage request, AiModelLookup lookup)
        => AiApiKeyProtection.ApplyAuthentication(request, lookup.Provider, lookup.ProtectedApiKey, _protector);
}

public static class AiModelCatalogParser
{
    public static IReadOnlyList<string> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new AiServiceException("Respons daftar model tidak memiliki array data.");

        var models = data.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.Object &&
                           item.TryGetProperty("id", out var id) &&
                           id.ValueKind == JsonValueKind.String &&
                           !string.IsNullOrWhiteSpace(id.GetString()))
            .Select(item => item.GetProperty("id").GetString()!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (models.Length == 0)
            throw new AiServiceException("Provider tidak mengembalikan model yang dapat dipilih.");

        return models;
    }
}
