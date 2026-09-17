using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Splitbill.Services;
using Xunit;

namespace Splitbill.Tests;

public sealed class MicrosoftOAuthConfigurationTests
{
    [Fact]
    public void UnconfiguredOptionsValidateCleanlyWithoutThrowing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthentication()
            .AddOAuth("MicrosoftLink", options =>
            {
                options.SignInScheme = "MicrosoftLinkCookie";
                options.AuthorizationEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/authorize";
                options.TokenEndpoint = "https://login.microsoftonline.com/common/oauth2/v2.0/token";
                options.UserInformationEndpoint = "https://graph.microsoft.com/oidc/userinfo";
                options.CallbackPath = "/account/microsoft/oauth-callback";
                options.ClientId = "unconfigured";
                options.ClientSecret = "unconfigured";
                options.Scope.Add("openid");
                options.SaveTokens = true;
            });
        services.AddSingleton<IConfigureOptions<OAuthOptions>, MicrosoftOAuthNamedOptions>();
        using var provider = services.BuildServiceProvider();

        var monitor = provider.GetRequiredService<IOptionsMonitor<OAuthOptions>>();
        var options = monitor.Get("MicrosoftLink");

        // Validate must not throw ArgumentException: The 'ClientId' option must be provided.
        var exception = Record.Exception(() => options.Validate("MicrosoftLink"));
        Assert.Null(exception);
        Assert.False(string.IsNullOrWhiteSpace(options.ClientId));
        Assert.False(string.IsNullOrWhiteSpace(options.ClientSecret));
    }
}
