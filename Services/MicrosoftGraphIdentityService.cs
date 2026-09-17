using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Splitbill.Models;
using Splitbill.ViewModels;

namespace Splitbill.Services;

public sealed class MicrosoftGraphIdentityService(
    IHttpClientFactory httpClientFactory,
    ILogger<MicrosoftGraphIdentityService> logger) : IMicrosoftGraphIdentityService
{
    public async Task<MicrosoftConnectionResult> TestCredentialsAsync(
        string tenantId, string clientId, string clientSecret, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(tenantId, out _) || !Guid.TryParse(clientId, out _))
            return new(false, "Tenant ID and client ID must be valid GUIDs.");
        if (string.IsNullOrWhiteSpace(clientSecret)) return new(false, "Client secret is required.");

        try
        {
            using var tokenClient = httpClientFactory.CreateClient("MicrosoftGraph");
            using var token = await tokenClient.PostAsync(
                $"https://login.microsoftonline.com/{Uri.EscapeDataString(tenantId)}/oauth2/v2.0/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["client_secret"] = clientSecret,
                    ["scope"] = "https://graph.microsoft.com/.default",
                    ["grant_type"] = "client_credentials"
                }), cancellationToken);
            if (!token.IsSuccessStatusCode) return new(false, "Microsoft credential validation failed.");
            var payload = await token.Content.ReadFromJsonAsync<TokenPayload>(cancellationToken: cancellationToken);
            if (string.IsNullOrWhiteSpace(payload?.AccessToken)) return new(false, "Microsoft did not return an access token.");
            return new(true, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Microsoft credential test failed");
            return new(false, "Microsoft credential validation could not be completed.");
        }
    }

    public async Task<MicrosoftGraphUser?> GetUserAsync(string accessToken, CancellationToken cancellationToken = default)
    {
        using var client = httpClientFactory.CreateClient("MicrosoftGraph");
        client.BaseAddress = new Uri("https://graph.microsoft.com/v1.0/");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.GetAsync("me?$select=id,displayName,mail,userPrincipalName", cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadFromJsonAsync<MicrosoftGraphUser>(cancellationToken: cancellationToken);
    }

    private sealed record TokenPayload([property: JsonPropertyName("access_token")] string? AccessToken);
}

public sealed record MicrosoftGraphUser(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("displayName")] string? DisplayName,
    [property: JsonPropertyName("mail")] string? Mail,
    [property: JsonPropertyName("userPrincipalName")] string? UserPrincipalName)
{
    public string? Email => string.IsNullOrWhiteSpace(Mail) ? UserPrincipalName : Mail;
}

public interface IMicrosoftGraphIdentityService
{
    Task<MicrosoftConnectionResult> TestCredentialsAsync(string tenantId, string clientId, string clientSecret, CancellationToken cancellationToken = default);
    Task<MicrosoftGraphUser?> GetUserAsync(string accessToken, CancellationToken cancellationToken = default);
}
