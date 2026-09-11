using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Splitbill.Controllers;
using Splitbill.Data;

namespace Splitbill.Tests;

public sealed class AdminUsersAuthorizationTests
{
    [Fact]
    public void ControllerRequiresAdminRole()
    {
        var attribute = typeof(AdminUsersController).GetCustomAttribute<AuthorizeAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(DatabaseSeeder.AdminRole, attribute!.Roles);
    }

    [Fact]
    public void EveryMutationIsPostAndAntiforgeryProtected()
    {
        foreach (var name in new[] { "Create", "Edit", "ResetPassword", "Disable", "Enable" })
        {
            var method = typeof(AdminUsersController).GetMethods().Single(x => x.Name == name &&
                x.GetCustomAttribute<HttpPostAttribute>() is not null);
            Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
        }
    }
}
