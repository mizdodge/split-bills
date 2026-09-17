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

    [Fact]
    public void IndexRedirectsToAdminMicrosoftSharePointTab()
    {
        var controller = new AdminSharePointController(null!, null!, null!, null!, null!, null!);
        var result = Assert.IsType<Microsoft.AspNetCore.Mvc.RedirectToActionResult>(controller.Index());
        Assert.Equal("Index", result.ActionName);
        Assert.Equal("AdminMicrosoft", result.ControllerName);
        Assert.NotNull(result.RouteValues);
        Assert.Equal("sharepoint", result.RouteValues["tab"]);
    }
}
