using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Splitbill.Controllers;
using Xunit;
namespace Splitbill.Tests;
public sealed class AccountSettingsAuthorizationTests
{
    [Fact]
    public async Task MicrosoftSecurityProofRequestsFreshAuthentication()
    {
        var options = new Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectOptions();
        Splitbill.Services.MicrosoftOidcConfiguration.Configure(options); options.ClientId = "test-client";
        var properties = new Microsoft.AspNetCore.Authentication.AuthenticationProperties();
        properties.Items["security-intent"] = "test-request";
        var context = new Microsoft.AspNetCore.Authentication.OpenIdConnect.RedirectContext(new Microsoft.AspNetCore.Http.DefaultHttpContext(),
            new Microsoft.AspNetCore.Authentication.AuthenticationScheme("MicrosoftLink", null, typeof(Microsoft.AspNetCore.Authentication.OpenIdConnect.OpenIdConnectHandler)), options, properties)
        { ProtocolMessage = new Microsoft.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectMessage() };
        await options.Events.OnRedirectToIdentityProvider(context);
        Assert.Equal("login", context.ProtocolMessage.Prompt); Assert.Equal("0", context.ProtocolMessage.MaxAge);
        Assert.False(options.SaveTokens); Assert.True(options.UsePkce);
    }

    [Fact]
    public void SettingsRequireAuthenticationAndMutationsRequireCsrf()
    {
        Assert.NotNull(typeof(AccountSettingsController).GetCustomAttribute<AuthorizeAttribute>());
        foreach(var name in new[] { "Connect", "ConfirmLink", "Cancel", "Disconnect", "Profile", "Preferences", "VerifyMicrosoft", "RevokeSessions" })
        {
            var method = typeof(AccountSettingsController).GetMethod(name)!;
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
        var start = typeof(MicrosoftSignInController).GetMethod("Start")!;
        Assert.NotNull(start.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(start.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        var create = typeof(AccountSettingsController).GetMethods().Single(x => x.Name == "CreatePassword" && x.GetCustomAttribute<HttpPostAttribute>() != null);
        Assert.NotNull(create.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }
}
