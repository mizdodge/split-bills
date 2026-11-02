using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace Splitbill.Controllers;

public sealed class CultureController(Microsoft.AspNetCore.Identity.UserManager<Splitbill.Models.ApplicationUser> users) : Controller
{
    [HttpPost("culture/set"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Set(string culture, string? returnUrl)
    {
        var allowed = culture is "id-ID" or "en-US";
        if (!allowed) culture = "id-ID";
        if (User.Identity?.IsAuthenticated == true && await users.GetUserAsync(User) is { } user)
        {
            user.PreferredCulture = culture;
            await users.UpdateAsync(user);
        }
        Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(new CultureInfo(culture))),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Home")!);
    }
}
