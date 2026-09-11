using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Splitbill.Controllers;
using Splitbill.Data;

namespace Splitbill.Tests;

public sealed class AdminFoodPickupAuthorizationTests
{
    [Fact]
    public void ControllerRequiresAdminRole()
    {
        var attribute = typeof(AdminFoodPickupController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DatabaseSeeder.AdminRole, attribute!.Roles);
    }

    [Fact]
    public void SaveIsPostAndAntiforgeryProtected()
    {
        var method = typeof(AdminFoodPickupController).GetMethod(nameof(AdminFoodPickupController.Save));

        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }
}
