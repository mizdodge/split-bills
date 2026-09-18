using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Splitbill.Controllers;
using Xunit;
namespace Splitbill.Tests;
public sealed class AccountSettingsAuthorizationTests
{
    [Fact]
    public void SettingsRequireAuthenticationAndMutationsRequireCsrf()
    {
        Assert.NotNull(typeof(AccountSettingsController).GetCustomAttribute<AuthorizeAttribute>());
        foreach(var name in new[] { "Connect", "ConfirmLink", "Cancel", "Disconnect" })
        {
            var method = typeof(AccountSettingsController).GetMethod(name)!;
            Assert.NotNull(method.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
        var start = typeof(MicrosoftSignInController).GetMethod("Start")!;
        Assert.NotNull(start.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(start.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }
}
