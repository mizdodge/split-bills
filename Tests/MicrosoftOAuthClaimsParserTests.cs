using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Splitbill.Services;
using Xunit;

namespace Splitbill.Tests;

public sealed class MicrosoftOAuthClaimsParserTests
{
    [Fact]
    public void PopulateClaimsFromJwtPayload_ExtractsAllStandardEntraClaims()
    {
        var payload = new
        {
            oid = "c4b12345-aaaa-bbbb-cccc-dddddddddddd",
            sub = "sub-12345678",
            tid = "954c8f02-f2f9-40e1-89da-3813e56e5bbb",
            name = "Test Admin User",
            email = "admin@glmsystems.com",
            preferred_username = "admin@glmsystems.com",
            upn = "admin@glmsystems.com"
        };

        var json = JsonSerializer.Serialize(payload);
        var base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var mockJwt = $"eyJhbGciOiJSUzI1NiJ9.{base64Payload}.mockSignature";

        var identity = new ClaimsIdentity();
        MicrosoftOAuthClaimsParser.PopulateClaimsFromJwtPayload(mockJwt, identity);

        Assert.Equal("c4b12345-aaaa-bbbb-cccc-dddddddddddd", identity.FindFirst(MicrosoftOAuthClaimsParser.OidClaimType)?.Value);
        Assert.Equal("sub-12345678", identity.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("954c8f02-f2f9-40e1-89da-3813e56e5bbb", identity.FindFirst(MicrosoftOAuthClaimsParser.TenantIdClaimType)?.Value);
        Assert.Equal("Test Admin User", identity.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal("admin@glmsystems.com", identity.FindFirst(ClaimTypes.Email)?.Value);
        Assert.Equal("admin@glmsystems.com", identity.FindFirst("preferred_username")?.Value);
    }

    [Fact]
    public void PopulateClaimsFromJwtPayload_FallsBackToPreferredUsernameForEmailWhenEmailClaimMissing()
    {
        var payload = new
        {
            oid = "c4b12345-aaaa-bbbb-cccc-dddddddddddd",
            sub = "sub-12345678",
            tid = "954c8f02-f2f9-40e1-89da-3813e56e5bbb",
            preferred_username = "user@glmsystems.com"
        };

        var json = JsonSerializer.Serialize(payload);
        var base64Payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var mockJwt = $"header.{base64Payload}.sig";

        var identity = new ClaimsIdentity();
        MicrosoftOAuthClaimsParser.PopulateClaimsFromJwtPayload(mockJwt, identity);

        Assert.Equal("user@glmsystems.com", identity.FindFirst(ClaimTypes.Email)?.Value);
        Assert.Equal("user@glmsystems.com", identity.FindFirst("preferred_username")?.Value);
    }

    [Fact]
    public void PopulateClaimsFromJwtPayload_MalformedJwtDoesNotThrow()
    {
        var identity = new ClaimsIdentity();
        var ex = Record.Exception(() => MicrosoftOAuthClaimsParser.PopulateClaimsFromJwtPayload("invalid-token", identity));
        Assert.Null(ex);
        Assert.Empty(identity.Claims);
    }
}
