using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Splitbill.Controllers;
using Splitbill.Data;

namespace Splitbill.Tests;

public sealed class AdminSharePointAuthorizationTests
{
    [Fact]
    public void ControllerRequiresAdminRole()
    {
        var attribute = typeof(AdminSharePointController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DatabaseSeeder.AdminRole, attribute!.Roles);
    }

    [Fact]
    public void MutatingActionsRequirePostAndAntiforgery()
    {
        foreach (var name in new[] { "TestConnection", "Save" })
        {
            var method = typeof(AdminSharePointController).GetMethod(name);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<Microsoft.AspNetCore.Mvc.HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<Microsoft.AspNetCore.Mvc.ValidateAntiForgeryTokenAttribute>());
        }
    }
}
