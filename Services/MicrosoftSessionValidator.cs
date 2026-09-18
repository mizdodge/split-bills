using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.Services;
public static class MicrosoftSessionValidator
{
    public static async Task ValidateAsync(CookieValidatePrincipalContext context)
    {
        var marker = context.Principal?.FindFirstValue("splitbill:microsoft");
        if (marker is not null)
        {
            var db = context.HttpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
            var id = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id);
            var login = await db.MicrosoftLoginConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            var config = await db.MicrosoftIntegrationConfigurations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == 1);
            if (user is null || login?.Enabled != true || MicrosoftAccountService.IsRecoveryAdmin(user) ||
                user.MicrosoftLinkRevoked || user.MicrosoftSubject is null ||
                user.LockoutEnd > DateTimeOffset.UtcNow ||
                !string.Equals(user.MicrosoftTenantId, config?.TenantId, StringComparison.OrdinalIgnoreCase) ||
                marker != user.MicrosoftLinkVersion.ToString(System.Globalization.CultureInfo.InvariantCulture))
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
                return;
            }
        }
        await SecurityStampValidator.ValidatePrincipalAsync(context);
        if (marker is not null && context.Principal?.Identity is ClaimsIdentity identity && !identity.HasClaim(x => x.Type == "splitbill:microsoft"))
            identity.AddClaim(new Claim("splitbill:microsoft", marker));
    }
}
