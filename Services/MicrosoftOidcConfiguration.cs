using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Splitbill.Data;

namespace Splitbill.Services;

public static class MicrosoftOidcConfiguration
{
    public static void Configure(OpenIdConnectOptions options)
    {
        options.SignInScheme = "MicrosoftLinkCookie";
        options.Authority = "https://login.microsoftonline.com/00000000-0000-0000-0000-000000000000/v2.0";
        options.ClientId = "unconfigured";
        options.CallbackPath = "/account/microsoft/oauth-callback";
        options.ResponseType = "code";
        options.UsePkce = true;
        options.MapInboundClaims = false;
        options.SaveTokens = false;
        options.GetClaimsFromUserInfoEndpoint = false;
        options.RequireHttpsMetadata = true;
        options.Scope.Clear();
        options.Scope.Add("openid"); options.Scope.Add("profile"); options.Scope.Add("email");
        options.TokenValidationParameters.NameClaimType = "name";
        options.TokenValidationParameters.ValidateIssuer = true;
        options.TokenValidationParameters.ValidateAudience = true;
        options.TokenValidationParameters.ValidateLifetime = true;
        options.TokenValidationParameters.RequireSignedTokens = true;
        options.Events.OnRedirectToIdentityProvider = context =>
        {
            if (context.Options.ClientId == "unconfigured")
            {
                context.Response.Redirect("/account/login?microsoftError=true");
                context.HandleResponse();
                return Task.CompletedTask;
            }
            context.ProtocolMessage.Prompt = "select_account";
            if (context.Properties.Items.ContainsKey("link-intent"))
                context.ProtocolMessage.MaxAge = "0";
            return Task.CompletedTask;
        };
        options.Events.OnTokenValidated = async context =>
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
            var config = await db.MicrosoftIntegrationConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1);
            var login = await db.MicrosoftLoginConfigurations.AsNoTracking().SingleAsync(x => x.Id == 1);
            if (!login.Enabled || MicrosoftIdentity.Read(context.Principal, config.TenantId) is null)
                context.Fail("Microsoft identity is not allowed.");
        };
        options.Events.OnRemoteFailure = context =>
        {
            // Provider errors may contain personal data or protocol details. Keep them off the URL.
            context.Response.Redirect("/account/login?microsoftError=true");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }
}

public sealed class MicrosoftOidcNamedOptions(IServiceScopeFactory scopeFactory) : IConfigureNamedOptions<OpenIdConnectOptions>
{
    public void Configure(string? name, OpenIdConnectOptions options)
    {
        if (name != "MicrosoftLink") return;
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var config = db.MicrosoftIntegrationConfigurations.AsNoTracking().SingleOrDefault(x => x.Id == 1);
        if (config is null || !Guid.TryParse(config.TenantId, out var tenant) ||
            !Guid.TryParse(config.ClientId, out _) || string.IsNullOrWhiteSpace(config.ProtectedClientSecret)) return;
        try
        {
            var secret = scope.ServiceProvider.GetRequiredService<IMicrosoftSecretProtector>().Unprotect(config.ProtectedClientSecret);
            options.Authority = $"https://login.microsoftonline.com/{tenant:D}/v2.0";
            options.ClientId = config.ClientId;
            options.ClientSecret = secret;
        }
        catch (InvalidOperationException)
        {
            // A moved or damaged DPAPI key must not block local recovery login.
            scope.ServiceProvider.GetRequiredService<ILogger<MicrosoftOidcNamedOptions>>()
                .LogWarning("Microsoft credentials cannot be decrypted; local login remains available.");
        }
    }
    public void Configure(OpenIdConnectOptions options) => Configure("MicrosoftLink", options);
}
