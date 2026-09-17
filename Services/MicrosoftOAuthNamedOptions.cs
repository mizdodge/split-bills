using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Splitbill.Data;

namespace Splitbill.Services;

public sealed class MicrosoftOAuthNamedOptions(IServiceScopeFactory scopeFactory) : IConfigureNamedOptions<OAuthOptions>
{
    public void Configure(string? name, OAuthOptions options)
    {
        if (name != "MicrosoftLink") return;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetService<ApplicationDbContext>();
            if (db != null)
            {
                var config = db.MicrosoftIntegrationConfigurations.Find(1);
                if (config != null && !string.IsNullOrWhiteSpace(config.ClientId) && !string.IsNullOrWhiteSpace(config.ProtectedClientSecret))
                {
                    var protector = scope.ServiceProvider.GetRequiredService<IMicrosoftSecretProtector>();
                    options.ClientId = config.ClientId;
                    options.ClientSecret = protector.Unprotect(config.ProtectedClientSecret);
                    if (!string.IsNullOrWhiteSpace(config.TenantId))
                    {
                        options.AuthorizationEndpoint = $"https://login.microsoftonline.com/{config.TenantId}/oauth2/v2.0/authorize";
                        options.TokenEndpoint = $"https://login.microsoftonline.com/{config.TenantId}/oauth2/v2.0/token";
                    }
                    return;
                }
            }
        }
        catch
        {
            // Safe fallback during bootstrap, tests, or initial schema migration
        }

        if (string.IsNullOrEmpty(options.ClientId)) options.ClientId = "unconfigured";
        if (string.IsNullOrEmpty(options.ClientSecret)) options.ClientSecret = "unconfigured";
    }

    public void Configure(OAuthOptions options) => Configure("MicrosoftLink", options);
}
