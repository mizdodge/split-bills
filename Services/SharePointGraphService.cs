using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Microsoft.Extensions.Logging;

namespace Splitbill.Services;

public sealed record SharePointConnectionRequest(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string SiteUrl,
    bool UseSharedMicrosoftCredentials = false);

public sealed record SharePointListOption(string Id, string DisplayName, string? WebUrl);

public sealed record SharePointConnectionResult(
    string SiteId,
    string SiteDisplayName,
    string SiteWebUrl,
    IReadOnlyList<SharePointListOption> Lists);

public sealed record SharePointListItemRequest(
    string TenantId,
    string ClientId,
    string ClientSecret,
    string SiteId,
    string ListId,
    string Title,
    string Email,
    string Description,
    bool UseSharedMicrosoftCredentials = false);

public enum SharePointGraphErrorCategory
{
    InvalidInput,
    AuthenticationFailed,
    PermissionDenied,
    NotFound,
    NoLists,
    RateLimited,
    NetworkFailure,
    InvalidResponse
}

public sealed class SharePointGraphException(
    SharePointGraphErrorCategory category,
    string message,
    HttpStatusCode? statusCode = null,
    string? requestId = null,
    Exception? innerException = null,
    string? diagnosticCode = null,
    string? providerMessage = null) : Exception(message, innerException)
{
    public SharePointGraphErrorCategory Category { get; } = category;
    public HttpStatusCode? StatusCode { get; } = statusCode;
    public string? RequestId { get; } = requestId;
    public string? DiagnosticCode { get; } = diagnosticCode;
    public string? ProviderMessage { get; } = providerMessage;
}

public interface ISharePointGraphService
{
    Task<SharePointConnectionResult> TestConnectionAsync(
        SharePointConnectionRequest request,
        CancellationToken cancellationToken = default);
    Task CreateNotificationItemAsync(
        SharePointListItemRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Small Microsoft Graph client for SharePoint site/list discovery. The later
/// synchronization phase will add item operations behind this same boundary.
/// </summary>
public sealed class SharePointGraphService(
    IHttpClientFactory httpClientFactory,
    IMicrosoftSecretProtector microsoftSecrets,
    ApplicationDbContext db,
    ILogger<SharePointGraphService> logger) : ISharePointGraphService
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string GraphHost = "graph.microsoft.com";

    public async Task<SharePointConnectionResult> TestConnectionAsync(
        SharePointConnectionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.UseSharedMicrosoftCredentials)
        {
            var shared = await db.MicrosoftIntegrationConfigurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
            if (shared is null || string.IsNullOrWhiteSpace(shared.ProtectedClientSecret))
                throw new SharePointGraphException(SharePointGraphErrorCategory.InvalidInput, "Shared Microsoft credentials are not configured.");
            request = request with
            {
                TenantId = shared.TenantId,
                ClientId = shared.ClientId,
                ClientSecret = microsoftSecrets.Unprotect(shared.ProtectedClientSecret)
            };
        }

        var tenantId = ValidateGuid(request.TenantId, "Tenant ID");
        var clientId = ValidateGuid(request.ClientId, "Client ID");
        var clientSecret = request.ClientSecret?.Trim();
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new SharePointGraphException(
                SharePointGraphErrorCategory.InvalidInput,
                "Microsoft Entra client secret wajib diisi.");

        var siteUri = SharePointUrlNormalizer.Normalize(request.SiteUrl);
        var accessToken = await AcquireTokenAsync(tenantId, clientId, clientSecret, cancellationToken);
        var site = await ResolveSiteAsync(siteUri, accessToken, cancellationToken);
        var lists = await ReadListsAsync(site.Id, accessToken, cancellationToken);

        if (lists.Count == 0)
            throw new SharePointGraphException(
                SharePointGraphErrorCategory.NoLists,
                "SharePoint site tidak memiliki list yang dapat dipilih.");

        return new SharePointConnectionResult(site.Id, site.DisplayName, site.WebUrl, lists);
    }

    public async Task CreateNotificationItemAsync(
        SharePointListItemRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.UseSharedMicrosoftCredentials)
        {
            var shared = await db.MicrosoftIntegrationConfigurations.SingleOrDefaultAsync(x => x.Id == 1, cancellationToken);
            if (shared is null || string.IsNullOrWhiteSpace(shared.ProtectedClientSecret))
                throw new SharePointGraphException(SharePointGraphErrorCategory.InvalidInput, "Shared Microsoft credentials are not configured.");
            request = request with
            {
                TenantId = shared.TenantId,
                ClientId = shared.ClientId,
                ClientSecret = microsoftSecrets.Unprotect(shared.ProtectedClientSecret)
            };
        }
        var tenantId = ValidateGuid(request.TenantId, "Tenant ID");
        var clientId = ValidateGuid(request.ClientId, "Client ID");
        if (string.IsNullOrWhiteSpace(request.ClientSecret) ||
            string.IsNullOrWhiteSpace(request.SiteId) ||
            string.IsNullOrWhiteSpace(request.ListId) ||
            string.IsNullOrWhiteSpace(request.Email))
            throw new SharePointGraphException(SharePointGraphErrorCategory.InvalidInput, "Konfigurasi atau penerima notifikasi SharePoint tidak lengkap.");

        var accessToken = await AcquireTokenAsync(tenantId, clientId, request.ClientSecret.Trim(), cancellationToken);
        var endpoint = $"{GraphBaseUrl}/sites/{Uri.EscapeDataString(request.SiteId)}/lists/{Uri.EscapeDataString(request.ListId)}/items";
        var payload = JsonSerializer.Serialize(new
        {
            fields = new Dictionary<string, object?>
            {
                ["Title"] = request.Title,
                ["Email"] = request.Email,
                ["Description"] = request.Description,
                ["IsProcessed"] = false
            }
        });
        using var response = await SendGraphAsync(HttpMethod.Post, endpoint, accessToken, cancellationToken, payload);
    }

    private async Task<string> AcquireTokenAsync(
        string tenantId,
        string clientId,
        string clientSecret,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token");
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        });

        HttpResponseMessage response;
        try
        {
            response = await httpClientFactory.CreateClient("SharePointGraph")
                .SendAsync(request, cancellationToken);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new SharePointGraphException(
                SharePointGraphErrorCategory.NetworkFailure,
                "Microsoft Entra tidak dapat dihubungi.",
                innerException: exception);
        }

        using (response)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var diagnosticCode = EntraDiagnosticCode(responseBody) ?? $"ENTRA_HTTP_{(int)response.StatusCode}";
                logger.LogWarning(
                    "SharePoint Entra token request failed with HTTP {StatusCode}, diagnostic code {DiagnosticCode}, and request ID {RequestId}",
                    (int)response.StatusCode,
                    diagnosticCode ?? "unavailable",
                    RequestId(response));
                throw new SharePointGraphException(
                    SharePointGraphErrorCategory.AuthenticationFailed,
                    "Autentikasi Microsoft Entra gagal.",
                    response.StatusCode,
                    RequestId(response),
                    diagnosticCode: diagnosticCode);
            }

            try
            {
                using var document = JsonDocument.Parse(responseBody);
                if (!document.RootElement.TryGetProperty("access_token", out var token) ||
                    token.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(token.GetString()))
                    throw new SharePointGraphException(
                        SharePointGraphErrorCategory.InvalidResponse,
                        "Respons token Microsoft Entra tidak memiliki access token.",
                        response.StatusCode,
                        RequestId(response));

                return token.GetString()!;
            }
            catch (JsonException exception)
            {
                throw new SharePointGraphException(
                    SharePointGraphErrorCategory.InvalidResponse,
                    "Respons token Microsoft Entra tidak valid.",
                    response.StatusCode,
                    RequestId(response),
                    exception);
            }
        }
    }

    private async Task<SharePointSiteInfo> ResolveSiteAsync(
        Uri siteUri,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var sitePath = siteUri.AbsolutePath.TrimEnd('/');
        var endpoint = string.IsNullOrWhiteSpace(sitePath)
            ? $"{GraphBaseUrl}/sites/{siteUri.Host}"
            : $"{GraphBaseUrl}/sites/{siteUri.Host}:{sitePath}";

        using var response = await SendGraphAsync(HttpMethod.Get, endpoint, accessToken, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            var id = RequiredString(root, "id");
            var displayName = root.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : siteUri.Host;
            var webUrl = root.TryGetProperty("webUrl", out var url) && url.ValueKind == JsonValueKind.String
                ? url.GetString()
                : siteUri.ToString();
            return new SharePointSiteInfo(id, displayName ?? siteUri.Host, webUrl ?? siteUri.ToString());
        }
        catch (SharePointGraphException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new SharePointGraphException(
                SharePointGraphErrorCategory.InvalidResponse,
                "Respons SharePoint site tidak valid.",
                response.StatusCode,
                RequestId(response),
                exception);
        }
    }

    private async Task<IReadOnlyList<SharePointListOption>> ReadListsAsync(
        string siteId,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var nextUrl = $"{GraphBaseUrl}/sites/{Uri.EscapeDataString(siteId)}/lists?$select=id,displayName,webUrl,list,system&$top=200";
        var lists = new Dictionary<string, SharePointListOption>(StringComparer.OrdinalIgnoreCase);

        while (!string.IsNullOrWhiteSpace(nextUrl))
        {
            using var response = await SendGraphAsync(HttpMethod.Get, nextUrl, accessToken, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            try
            {
                using var document = JsonDocument.Parse(responseBody);
                var root = document.RootElement;
                if (!root.TryGetProperty("value", out var values) || values.ValueKind != JsonValueKind.Array)
                    throw new SharePointGraphException(
                        SharePointGraphErrorCategory.InvalidResponse,
                        "Respons SharePoint list tidak memiliki data list.",
                        response.StatusCode,
                        RequestId(response));

                foreach (var item in values.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object || IsHiddenOrSystem(item)) continue;
                    var id = OptionalString(item, "id");
                    var displayName = OptionalString(item, "displayName");
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(displayName)) continue;
                    if (!lists.ContainsKey(id))
                        lists[id] = new SharePointListOption(id, displayName, OptionalString(item, "webUrl"));
                }

                nextUrl = NextLink(root);
            }
            catch (SharePointGraphException)
            {
                throw;
            }
            catch (JsonException exception)
            {
                throw new SharePointGraphException(
                    SharePointGraphErrorCategory.InvalidResponse,
                    "Respons daftar SharePoint tidak valid.",
                    response.StatusCode,
                    RequestId(response),
                    exception);
            }
        }

        return lists.Values
            .OrderBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<HttpResponseMessage> SendGraphAsync(
        HttpMethod method,
        string endpoint,
        string accessToken,
        CancellationToken cancellationToken,
        string? jsonContent = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
            !string.Equals(endpointUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(endpointUri.Host, GraphHost, StringComparison.OrdinalIgnoreCase))
            throw new SharePointGraphException(
                SharePointGraphErrorCategory.InvalidInput,
                "Graph pagination URL tidak valid.");

        using var request = new HttpRequestMessage(method, endpointUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (jsonContent is not null)
            request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");
        try
        {
            var response = await httpClientFactory.CreateClient("SharePointGraph")
                .SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode) return response;

            var category = response.StatusCode switch
            {
                HttpStatusCode.Forbidden => SharePointGraphErrorCategory.PermissionDenied,
                HttpStatusCode.Unauthorized => SharePointGraphErrorCategory.AuthenticationFailed,
                HttpStatusCode.NotFound => SharePointGraphErrorCategory.NotFound,
                (HttpStatusCode)429 => SharePointGraphErrorCategory.RateLimited,
                _ => SharePointGraphErrorCategory.NetworkFailure
            };
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            var graphError = GraphDiagnostic(responseBody, response.StatusCode);
            logger.LogWarning(
                "SharePoint Graph request failed with HTTP {StatusCode}, diagnostic code {DiagnosticCode}, and request ID {RequestId}",
                (int)response.StatusCode,
                graphError.Code,
                RequestId(response));
            response.Dispose();
            throw new SharePointGraphException(
                category,
                GraphMessage(category),
                response.StatusCode,
                RequestId(response),
                diagnosticCode: graphError.Code,
                providerMessage: graphError.Message);
        }
        catch (SharePointGraphException)
        {
            throw;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            throw new SharePointGraphException(
                SharePointGraphErrorCategory.NetworkFailure,
                "Microsoft Graph tidak dapat dihubungi.",
                innerException: exception);
        }
    }

    private static string ValidateGuid(string value, string field)
    {
        if (!Guid.TryParse(value?.Trim(), out var guid))
            throw new SharePointGraphException(SharePointGraphErrorCategory.InvalidInput, $"{field} harus berupa GUID.");
        return guid.ToString("D");
    }

    private static string RequiredString(JsonElement objectElement, string name)
    {
        var value = OptionalString(objectElement, name);
        if (string.IsNullOrWhiteSpace(value))
            throw new SharePointGraphException(SharePointGraphErrorCategory.InvalidResponse, $"Respons SharePoint site tidak memiliki {name}.");
        return value;
    }

    private static string? OptionalString(JsonElement objectElement, string name)
        => objectElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool IsHiddenOrSystem(JsonElement item)
    {
        var hidden = item.TryGetProperty("list", out var listInfo) &&
                     listInfo.ValueKind == JsonValueKind.Object &&
                     listInfo.TryGetProperty("hidden", out var hiddenValue) &&
                     hiddenValue.ValueKind == JsonValueKind.True;
        var system = item.TryGetProperty("system", out var systemValue) &&
                     systemValue.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
        return hidden || system;
    }

    private static string? NextLink(JsonElement root)
    {
        if (!root.TryGetProperty("@odata.nextLink", out var next) || next.ValueKind != JsonValueKind.String)
            return null;
        var value = next.GetString();
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(uri.Host, GraphHost, StringComparison.OrdinalIgnoreCase))
            throw new SharePointGraphException(SharePointGraphErrorCategory.InvalidResponse, "Graph pagination URL tidak dipercaya.");
        return uri.AbsoluteUri;
    }

    private static string? RequestId(HttpResponseMessage response)
        => response.Headers.TryGetValues("request-id", out var values) ? values.FirstOrDefault() : null;

    private static string? EntraDiagnosticCode(string responseBody)
    {
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.TryGetProperty("error_codes", out var codes) &&
                codes.ValueKind == JsonValueKind.Array)
            {
                foreach (var code in codes.EnumerateArray())
                {
                    if (code.ValueKind == JsonValueKind.Number && code.TryGetInt64(out var numericCode))
                        return $"AADSTS{numericCode}";
                    if (code.ValueKind == JsonValueKind.String && long.TryParse(code.GetString(), out numericCode))
                        return $"AADSTS{numericCode}";
                }
            }
        }
        catch (JsonException)
        {
            // The response body is intentionally not surfaced because it can contain tenant details.
        }

        return null;
    }

    private static GraphErrorDiagnostic GraphDiagnostic(string responseBody, HttpStatusCode statusCode)
    {
        var prefix = $"GRAPH_HTTP_{(int)statusCode}";
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("error", out var error) ||
                error.ValueKind != JsonValueKind.Object ||
                !error.TryGetProperty("code", out var codeElement) ||
                codeElement.ValueKind != JsonValueKind.String)
                return new GraphErrorDiagnostic(prefix, null);

            var code = codeElement.GetString();
            if (string.IsNullOrWhiteSpace(code) || code.Length > 80 ||
                code.Any(character => !char.IsLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
                code = null;

            var providerMessage = error.TryGetProperty("message", out var messageElement) &&
                                  messageElement.ValueKind == JsonValueKind.String
                ? SanitizeProviderMessage(messageElement.GetString())
                : null;
            return new GraphErrorDiagnostic(code is null ? prefix : $"{prefix}_{code}", providerMessage);
        }
        catch (JsonException)
        {
            return new GraphErrorDiagnostic(prefix, null);
        }
    }

    private static string? SanitizeProviderMessage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var singleLine = string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 300 ? singleLine : singleLine[..300];
    }

    private static string GraphMessage(SharePointGraphErrorCategory category) => category switch
    {
        SharePointGraphErrorCategory.PermissionDenied => "Aplikasi tidak memiliki akses ke SharePoint site ini.",
        SharePointGraphErrorCategory.AuthenticationFailed => "Token Microsoft Graph tidak valid atau sudah tidak berwenang.",
        SharePointGraphErrorCategory.NotFound => "SharePoint site atau resource tidak ditemukan.",
        SharePointGraphErrorCategory.RateLimited => "Microsoft Graph sedang membatasi request. Coba lagi beberapa saat.",
        _ => "Microsoft Graph mengembalikan error saat membaca SharePoint."
    };

    private sealed record SharePointSiteInfo(string Id, string DisplayName, string WebUrl);
    private sealed record GraphErrorDiagnostic(string Code, string? Message);
}

public static class SharePointUrlNormalizer
{
    public static Uri Normalize(string? value)
    {
        if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
            !IsSharePointHost(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new SharePointGraphException(
                SharePointGraphErrorCategory.InvalidInput,
                "SharePoint Site URL harus berupa URL HTTPS SharePoint yang valid.");

        var path = uri.AbsolutePath.TrimEnd('/');
        var builder = new UriBuilder(uri)
        {
            Path = string.IsNullOrEmpty(path) ? "/" : path,
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };
        return builder.Uri;
    }

    private static bool IsSharePointHost(string host)
        => host.EndsWith(".sharepoint.com", StringComparison.OrdinalIgnoreCase) &&
           host.Length > ".sharepoint.com".Length;
}
