using System.Security.Claims;
using Microsoft.AspNetCore.Localization;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;

namespace Splitbill.Services;

public sealed class AccountCultureProvider : RequestCultureProvider
{
    public override async Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true) return null;
        var id = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
        var db = httpContext.RequestServices.GetRequiredService<ApplicationDbContext>();
        var culture = await db.Users.AsNoTracking().Where(x => x.Id == id).Select(x => x.PreferredCulture).SingleOrDefaultAsync(httpContext.RequestAborted);
        return culture is "id-ID" or "en-US" ? new ProviderCultureResult(culture) : null;
    }
}
