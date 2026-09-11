using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Splitbill.Controllers;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.ViewModels;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class NotificationsControllerTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly UserManager<ApplicationUser> users;
    private readonly WebPushSecretProtector pushSecrets = new(new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());

    public NotificationsControllerTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(),
            null!, NullLogger<UserManager<ApplicationUser>>.Instance);
        db.Users.Add(new ApplicationUser { Id = "member", UserName = "member", DisplayName = "Member" });
        db.UserNotifications.AddRange(
            new UserNotification { UserId = "member", Type = NotificationType.BillAssigned, CreatedAt = new DateTimeOffset(2026, 9, 1, 8, 0, 0, TimeSpan.Zero) },
            new UserNotification { UserId = "member", Type = NotificationType.PaymentApproved, CreatedAt = new DateTimeOffset(2026, 9, 2, 8, 0, 0, TimeSpan.Zero) });
        db.SaveChanges();
    }

    [Fact]
    public async Task Index_SortsDateTimeOffsetNotificationsAfterSqliteMaterialization()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "member")], "test"))
        };
        var controller = new NotificationsController(db, users,
            new WebPushKeyService(db, pushSecrets),
            new WebPushSubscriptionService(db, pushSecrets))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };

        var view = Assert.IsType<ViewResult>(await controller.Index(default));
        var model = Assert.IsType<NotificationListViewModel>(view.Model);

        Assert.Equal(NotificationType.PaymentApproved, model.Notifications[0].Type);
        Assert.Equal(NotificationType.BillAssigned, model.Notifications[1].Type);
    }

    public void Dispose()
    {
        users.Dispose();
        db.Dispose();
        connection.Dispose();
    }
}
