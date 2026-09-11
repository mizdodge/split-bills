using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.Mvc;
using System.Globalization;

namespace Splitbill.Controllers;

public sealed class CultureController : Controller
{
    [HttpPost("culture/set"), ValidateAntiForgeryToken]
    public IActionResult Set(string culture, string? returnUrl)
    {
        var allowed = culture is "id-ID" or "en-US";
        if (!allowed) culture = "id-ID";
        Response.Cookies.Append(CookieRequestCultureProvider.DefaultCookieName,
            CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(new CultureInfo(culture))),
            new CookieOptions { Expires = DateTimeOffset.UtcNow.AddYears(1), IsEssential = true, SameSite = SameSiteMode.Lax });
        return LocalRedirect(Url.IsLocalUrl(returnUrl) ? returnUrl! : Url.Action("Index", "Home")!);
    }
}
