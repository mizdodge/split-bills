using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Splitbill.Controllers;
using Splitbill.Data;
using Xunit;

namespace Splitbill.Tests;

public sealed class AdminMicrosoftUnifiedTests
{
    [Fact]
    public void AdminMicrosoftController_RequiresAdminRole()
    {
        var attribute = typeof(AdminMicrosoftController).GetCustomAttribute<AuthorizeAttribute>();
        Assert.NotNull(attribute);
        Assert.Equal(DatabaseSeeder.AdminRole, attribute!.Roles);
    }

    [Fact]
    public void AdminMicrosoftController_HasExpectedRoutes()
    {
        var attributes = typeof(AdminMicrosoftController).GetCustomAttributes<RouteAttribute>().ToArray();
        Assert.Contains(attributes, a => a.Template == "admin/microsoft");
        Assert.Contains(attributes, a => a.Template == "AdminMicrosoft");
    }

    [Fact]
    public void AdminMicrosoftController_MutatingActionsRequirePostAndAntiforgery()
    {
        foreach (var actionName in new[] { "Save", "MigrateSharePoint" })
        {
            var method = typeof(AdminMicrosoftController).GetMethod(actionName);
            Assert.NotNull(method);
            Assert.NotNull(method!.GetCustomAttribute<HttpPostAttribute>());
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
    }
}
