using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;

namespace Splitbill.ViewComponents;

public sealed class NotificationBadgeViewComponent(ApplicationDbContext db, UserManager<ApplicationUser> userManager) : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(CancellationToken cancellationToken = default)
    {
        var userId = userManager.GetUserId(HttpContext.User);
        if (string.IsNullOrWhiteSpace(userId)) return Content(string.Empty);
        var count = await db.UserNotifications.CountAsync(x => x.UserId == userId && !x.IsRead, cancellationToken);
        return View(count);
    }
}
