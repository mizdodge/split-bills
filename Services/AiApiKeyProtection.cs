using System.Net.Http.Headers;
using Microsoft.AspNetCore.DataProtection;
using Splitbill.Models;

namespace Splitbill.Services;

public static class AiApiKeyProtection
{
    public const string Purpose = "SplitBill.AiConfiguration.ApiKey.v1";

    public static void ApplyAuthentication(
        HttpRequestMessage request, AiProvider provider, string? protectedApiKey, IDataProtector protector)
    {
        if (string.IsNullOrWhiteSpace(protectedApiKey))
            throw new AiServiceException($"{ProviderName(provider)} API key belum disimpan di AI Settings.");
        try
        {
            var apiKey = protector.Unprotect(protectedApiKey);
            if (provider == AiProvider.OpenAi)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            else
                request.Headers.Add("api-key", apiKey);
        }
        catch (AiServiceException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new AiServiceException("API key tidak dapat dibuka. Simpan ulang key di AI Settings.", exception);
        }
    }

    private static string ProviderName(AiProvider provider) =>
        provider == AiProvider.OpenAi ? "OpenAI" : "Azure OpenAI";
}
