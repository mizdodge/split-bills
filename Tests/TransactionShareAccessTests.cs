using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Splitbill.Controllers;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class TransactionShareAccessTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly long transactionId;

    public TransactionShareAccessTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", DisplayName = "Owner" });
        var transaction = new BillTransaction
        {
            TransactionNumber = "TEST-GUEST-JPG", MerchantName = "Cafe", UploadedByUserId = "owner",
            Status = TransactionStatus.Unpaid, SplitMethod = SplitMethod.Equal,
            Subtotal = 10_000m, GrandTotal = 10_000m,
            Items = [new TransactionItem { Name = "Meal", Quantity = 1, UnitPrice = 10_000m, TotalPrice = 10_000m }],
            Participants =
            [
                new TransactionParticipant { Name = "Guest A", Amount = 5_000m },
                new TransactionParticipant { Name = "Guest B", Amount = 5_000m }
            ]
        };
        db.Transactions.Add(transaction);
        db.SaveChanges();
        transactionId = transaction.Id;
        db.ChangeTracker.Clear();
    }

    [Theory]
    [InlineData(true, "admin", true)]
    [InlineData(false, "owner", true)]
    [InlineData(false, "other-moderator", false)]
    public async Task JpgDataAllowsAdminOrUploaderEvenWhenEveryParticipantIsGuest(bool isAdmin, string userId, bool expectedAccess)
    {
        var result = await Controller(isAdmin, userId).ShareData(transactionId, CancellationToken.None);

        if (!expectedAccess)
        {
            Assert.IsType<NotFoundResult>(result);
            return;
        }

        var json = Assert.IsType<JsonResult>(result);
        var export = Assert.IsType<TransactionShareExport>(json.Value);
        Assert.Equal(2, export.Participants.Count);
        Assert.Equal(["Guest A", "Guest B"], export.Participants.Select(x => x.DisplayName).ToArray());
    }

    private TransactionsController Controller(bool isAdmin, string userId)
    {
        var users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser>(db),
            Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(), [], [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, isAdmin ? DatabaseSeeder.AdminRole : DatabaseSeeder.ModeratorRole)
            ], "Test"))
        };
        return new TransactionsController(db, users, null!, new SplitBillCalculator(), new TestEnvironment(),
            shareExportService: new TransactionShareExportService(new TestLocalizer()))
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Splitbill";
        public string EnvironmentName { get; set; } = "Testing";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class TestLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
        public IStringLocalizer WithCulture(CultureInfo culture) => this;
    }
}
