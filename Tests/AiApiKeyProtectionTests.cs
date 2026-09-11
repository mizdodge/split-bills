using Microsoft.AspNetCore.DataProtection;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class AiApiKeyProtectionTests
{
    private readonly IDataProtector protector = new EphemeralDataProtectionProvider()
        .CreateProtector(AiApiKeyProtection.Purpose);

    [Fact]
    public void OpenAiUsesDecryptedDatabaseKeyAsBearerToken()
    {
        const string apiKey = "fake-openai-key";
        var ciphertext = protector.Protect(apiKey);
        using var request = new HttpRequestMessage();

        AiApiKeyProtection.ApplyAuthentication(request, AiProvider.OpenAi, ciphertext, protector);

        Assert.NotEqual(apiKey, ciphertext);
        Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
        Assert.Equal(apiKey, request.Headers.Authorization?.Parameter);
        Assert.False(request.Headers.Contains("api-key"));
    }

    [Fact]
    public void AzureUsesDecryptedDatabaseKeyAsApiKeyHeader()
    {
        const string apiKey = "fake-azure-key";
        var ciphertext = protector.Protect(apiKey);
        using var request = new HttpRequestMessage();

        AiApiKeyProtection.ApplyAuthentication(request, AiProvider.AzureOpenAi, ciphertext, protector);

        Assert.Equal(apiKey, Assert.Single(request.Headers.GetValues("api-key")));
        Assert.Null(request.Headers.Authorization);
    }

    [Theory]
    [InlineData(AiProvider.OpenAi)]
    [InlineData(AiProvider.AzureOpenAi)]
    public void MissingStoredKeyIsRejected(AiProvider provider)
    {
        using var request = new HttpRequestMessage();
        Assert.Throws<AiServiceException>(() =>
            AiApiKeyProtection.ApplyAuthentication(request, provider, null, protector));
    }
}
