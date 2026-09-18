using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using Splitbill.Services;
using Xunit;

namespace Splitbill.Tests;
public sealed class MicrosoftOidcTests
{
    [Fact]
    public void UsesValidatedCodeFlowWithoutSavingTokens()
    {
        var options = new OpenIdConnectOptions(); MicrosoftOidcConfiguration.Configure(options);
        Assert.Equal("code", options.ResponseType); Assert.True(options.UsePkce);
        Assert.True(options.ProtocolValidator.RequireNonce); Assert.True(options.RequireHttpsMetadata);
        Assert.False(options.SaveTokens); Assert.False(options.MapInboundClaims);
        Assert.Equal("/account/microsoft/oauth-callback", options.CallbackPath);
        Assert.Equal(new[] { "email", "openid", "profile" }, options.Scope.OrderBy(x => x));
    }
    [Theory]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("expiry")]
    public void TokenValidationRejectsInvalidIdentityTokens(string defect)
    {
        var options = new OpenIdConnectOptions(); MicrosoftOidcConfiguration.Configure(options);
        var key = new SymmetricSecurityKey(new byte[64]);
        var badKeyBytes = new byte[64]; badKeyBytes[0] = 1;
        var parameters = options.TokenValidationParameters.Clone();
        parameters.ValidIssuer = "https://issuer.example.com"; parameters.ValidAudience = "client";
        parameters.IssuerSigningKey = key; parameters.ClockSkew = TimeSpan.Zero;
        var token = new JwtSecurityToken(defect == "issuer" ? "https://other.example.com" : parameters.ValidIssuer,
            defect == "audience" ? "other-client" : "client", null, DateTime.UtcNow.AddHours(-2),
            defect == "expiry" ? DateTime.UtcNow.AddHours(-1) : DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(defect == "signature" ? new SymmetricSecurityKey(badKeyBytes) : key, SecurityAlgorithms.HmacSha256));
        var handler = new JwtSecurityTokenHandler();
        Assert.ThrowsAny<SecurityTokenException>(() => handler.ValidateToken(handler.WriteToken(token), parameters, out _));
    }
}
