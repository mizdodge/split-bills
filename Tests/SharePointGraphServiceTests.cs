using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class SharePointGraphServiceTests
{
    [Fact]
    public async Task TestConnectionResolvesSiteAndReturnsSortedVisibleListsAcrossPages()
    {
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"token-1\"}");
            if (request.RequestUri.AbsolutePath == "/v1.0/sites/tenant.sharepoint.com:/sites/Office")
                return Json(HttpStatusCode.OK, "{\"id\":\"tenant.sharepoint.com,site,web\",\"displayName\":\"Office\",\"webUrl\":\"https://tenant.sharepoint.com/sites/Office\"}");
            if (request.RequestUri.Query.Contains("skiptoken=2", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"1\",\"displayName\":\"Alpha\",\"webUrl\":\"https://tenant.sharepoint.com/lists/a\"},{\"id\":\"2\",\"displayName\":\"Zeta duplicate\"}]}");
            if (request.RequestUri.AbsolutePath.EndsWith("/lists"))
            {
                Assert.Contains("list", request.RequestUri.Query, StringComparison.Ordinal);
                Assert.DoesNotContain(",hidden", request.RequestUri.Query, StringComparison.Ordinal);
                return Json(HttpStatusCode.OK, "{\"value\":[{\"id\":\"2\",\"displayName\":\"Zeta\",\"webUrl\":\"https://tenant.sharepoint.com/lists/z\",\"list\":{\"hidden\":false}},{\"id\":\"hidden\",\"displayName\":\"Hidden\",\"list\":{\"hidden\":true}},{\"id\":\"system\",\"displayName\":\"System\",\"system\":{}}],\"@odata.nextLink\":\"https://graph.microsoft.com/v1.0/sites/tenant.sharepoint.com%2Csite%2Cweb/lists?$skiptoken=2\"}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var service = CreateService(handler);

        var result = await service.TestConnectionAsync(new SharePointConnectionRequest(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "secret", "https://tenant.sharepoint.com/sites/Office/"));

        Assert.Equal("tenant.sharepoint.com,site,web", result.SiteId);
        Assert.Equal("Office", result.SiteDisplayName);
        Assert.Equal(["Alpha", "Zeta"], result.Lists.Select(x => x.DisplayName).ToArray());
        Assert.DoesNotContain(result.Lists, x => x.Id is "hidden" or "system");
    }

    [Fact]
    public async Task CreateNotificationItemPostsExistingListColumns()
    {
        string? posted = null;
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"token-1\"}");
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/v1.0/sites/site%2Cid/lists/list-id/items", request.RequestUri.AbsolutePath);
            Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
            posted = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return Json(HttpStatusCode.Created, "{\"id\":\"12\"}");
        });

        await CreateService(handler).CreateNotificationItemAsync(new SharePointListItemRequest(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "secret", "site,id", "list-id",
            "Tagihan baru", "member@example.com", "Buka My bills."));

        using var payload = System.Text.Json.JsonDocument.Parse(posted!);
        var fields = payload.RootElement.GetProperty("fields");
        Assert.Equal("Tagihan baru", fields.GetProperty("Title").GetString());
        Assert.Equal("member@example.com", fields.GetProperty("Email").GetString());
        Assert.Equal("Buka My bills.", fields.GetProperty("Description").GetString());
        Assert.False(fields.GetProperty("IsProcessed").GetBoolean());
    }

    [Theory]
    [InlineData("http://tenant.sharepoint.com/sites/Office")]
    [InlineData("https://tenant.sharepoint.com/sites/Office?x=1")]
    [InlineData("https://tenant.sharepoint.com/sites/Office#fragment")]
    [InlineData("https://evil.example.com/sites/Office")]
    [InlineData("https://sharepoint.com/sites/Office")]
    public void SiteUrlRejectsUnsafeOrNonSharePointValues(string value)
    {
        var exception = Assert.Throws<SharePointGraphException>(() => SharePointUrlNormalizer.Normalize(value));

        Assert.Equal(SharePointGraphErrorCategory.InvalidInput, exception.Category);
    }

    [Fact]
    public async Task ForbiddenGraphResponseMapsToPermissionDenied()
    {
        var handler = new StubHttpHandler(request =>
        {
            if (request.RequestUri!.Host == "login.microsoftonline.com")
                return Json(HttpStatusCode.OK, "{\"access_token\":\"token-1\"}");
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.TryAddWithoutValidation("request-id", "request-123");
            return response;
        });
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SharePointGraphException>(() => service.TestConnectionAsync(new SharePointConnectionRequest(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "secret", "https://tenant.sharepoint.com/sites/Office")));

        Assert.Equal(SharePointGraphErrorCategory.PermissionDenied, exception.Category);
        Assert.Equal("request-123", exception.RequestId);
        Assert.DoesNotContain("secret", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InvalidTokenResponseDoesNotLeakSecret()
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.OK, "{\"not_token\":true}"));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SharePointGraphException>(() => service.TestConnectionAsync(new SharePointConnectionRequest(
            Guid.NewGuid().ToString(), Guid.NewGuid().ToString(), "secret-value", "https://tenant.sharepoint.com/sites/Office")));

        Assert.Equal(SharePointGraphErrorCategory.InvalidResponse, exception.Category);
        Assert.DoesNotContain("secret-value", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"error\":\"invalid_client\",\"error_codes\":[7000215],\"error_description\":\"secret-value must never escape\"}", "AADSTS7000215")]
    [InlineData("{\"error\":\"invalid_client\",\"error_codes\":[\"7000222\"]}", "AADSTS7000222")]
    public async Task FailedTokenRequestReturnsOnlySafeEntraDiagnosticCode(string body, string expectedCode)
    {
        var handler = new StubHttpHandler(_ => Json(HttpStatusCode.Unauthorized, body));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SharePointGraphException>(() => service.TestConnectionAsync(
            new SharePointConnectionRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "secret-value", "https://tenant.sharepoint.com/sites/Office")));

        Assert.Equal(SharePointGraphErrorCategory.AuthenticationFailed, exception.Category);
        Assert.Equal(expectedCode, exception.DiagnosticCode);
        Assert.DoesNotContain("secret-value", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedTokenRequestWithoutEntraEnvelopeReturnsSafeStageAndStatus()
    {
        var service = CreateService(new StubHttpHandler(_ => Json(HttpStatusCode.Unauthorized, "not-json")));

        var exception = await Assert.ThrowsAsync<SharePointGraphException>(() => service.TestConnectionAsync(
            new SharePointConnectionRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "secret-value", "https://tenant.sharepoint.com/sites/Office")));

        Assert.Equal("ENTRA_HTTP_401", exception.DiagnosticCode);
        Assert.DoesNotContain("not-json", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnauthorizedGraphResponseIdentifiesPostTokenStage()
    {
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.Host == "login.microsoftonline.com"
                ? Json(HttpStatusCode.OK, "{\"access_token\":\"token-1\"}")
                : Json(HttpStatusCode.Unauthorized, "{\"error\":{\"message\":\"token rejected\"}}"));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SharePointGraphException>(() => service.TestConnectionAsync(
            new SharePointConnectionRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "secret-value", "https://tenant.sharepoint.com/sites/Office")));

        Assert.Equal(SharePointGraphErrorCategory.AuthenticationFailed, exception.Category);
        Assert.Equal("GRAPH_HTTP_401", exception.DiagnosticCode);
        Assert.DoesNotContain("token rejected", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnknownGraphHttpFailureReturnsSafeStatusAndProviderCode()
    {
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.Host == "login.microsoftonline.com"
                ? Json(HttpStatusCode.OK, "{\"access_token\":\"token-1\"}")
                : Json(HttpStatusCode.BadRequest,
                    "{\"error\":{\"code\":\"invalidRequest\",\"message\":\"sensitive tenant detail\"}}"));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SharePointGraphException>(() => service.TestConnectionAsync(
            new SharePointConnectionRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "secret-value", "https://tenant.sharepoint.com/sites/Office")));

        Assert.Equal(SharePointGraphErrorCategory.NetworkFailure, exception.Category);
        Assert.Equal("GRAPH_HTTP_400_invalidRequest", exception.DiagnosticCode);
        Assert.Equal("sensitive tenant detail", exception.ProviderMessage);
        Assert.DoesNotContain("sensitive tenant detail", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-value", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task GraphProviderMessageIsSingleLineAndBounded()
    {
        var longMessage = $"  Invalid   hostname\r\nfor tenancy {new string('x', 400)}";
        var handler = new StubHttpHandler(request =>
            request.RequestUri!.Host == "login.microsoftonline.com"
                ? Json(HttpStatusCode.OK, "{\"access_token\":\"token-1\"}")
                : Json(HttpStatusCode.BadRequest, System.Text.Json.JsonSerializer.Serialize(new
                {
                    error = new { code = "BadRequest", message = longMessage }
                })));
        var service = CreateService(handler);

        var exception = await Assert.ThrowsAsync<SharePointGraphException>(() => service.TestConnectionAsync(
            new SharePointConnectionRequest(Guid.NewGuid().ToString(), Guid.NewGuid().ToString(),
                "secret-value", "https://tenant.sharepoint.com/sites/Office")));

        Assert.NotNull(exception.ProviderMessage);
        Assert.Equal(300, exception.ProviderMessage!.Length);
        Assert.StartsWith("Invalid hostname for tenancy", exception.ProviderMessage, StringComparison.Ordinal);
        Assert.DoesNotContain('\r', exception.ProviderMessage);
        Assert.DoesNotContain('\n', exception.ProviderMessage);
    }

    private static SharePointGraphService CreateService(StubHttpHandler handler)
    {
        var services = new ServiceCollection();
        services.AddHttpClient("SharePointGraph").ConfigurePrimaryHttpMessageHandler(() => handler);
        var provider = services.BuildServiceProvider();
        return new SharePointGraphService(provider.GetRequiredService<IHttpClientFactory>(), NullLogger<SharePointGraphService>.Instance);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body)
        => new(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };

    private sealed class StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(responder(request));
    }
}
