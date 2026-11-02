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
using Splitbill.ViewModels;
using System.Text.Json;

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

    [Theory]
    [InlineData(true, "admin", true)]
    [InlineData(false, "owner", true)]
    [InlineData(false, "other-moderator", false)]
    public async Task ReviewPreviewIsScopedAndDoesNotPersist(bool isAdmin, string userId, bool allowed)
    {
        var result = await Controller(isAdmin, userId).ReviewTotalPreview(new ReviewTotalPreviewInput
        {
            Id = transactionId, Subtotal = 10_000,
            Charges = [new() { Amount = 1_000, Operation = ChargeOperation.Subtract },
                new() { Amount = 500, Operation = ChargeOperation.Add }]
        });
        if (!allowed) { Assert.IsType<NotFoundResult>(result); return; }
        var payload = JsonSerializer.SerializeToElement(Assert.IsType<JsonResult>(result).Value);
        Assert.Equal("9500", payload.GetProperty("grandTotal").GetString());
        Assert.Equal(2, payload.GetProperty("rates").GetArrayLength());
        Assert.Equal(10_000m, (await db.Transactions.AsNoTracking().SingleAsync()).GrandTotal);
        Assert.Empty(await db.TransactionCharges.ToListAsync());
    }

    [Fact]
    public async Task ReviewPreviewRejectsPaymentLockedTransaction()
    {
        var participant = await db.TransactionParticipants.FirstAsync();
        participant.PaymentStatus = ParticipantPaymentStatus.Paid;
        await db.SaveChangesAsync();
        Assert.IsType<ConflictResult>(await Controller(true, "admin").ReviewTotalPreview(
            new ReviewTotalPreviewInput { Id = transactionId, Subtotal = 10_000 }));
    }

    [Theory]
    [InlineData(-1, ChargeOperation.Add)]
    [InlineData(1, (ChargeOperation)99)]
    public async Task ReviewPreviewRejectsInvalidCharge(int amount, ChargeOperation operation)
    {
        Assert.IsType<BadRequestResult>(await Controller(true, "admin").ReviewTotalPreview(
            new ReviewTotalPreviewInput { Id = transactionId, Subtotal = 10_000,
                Charges = [new() { Amount = amount, Operation = operation }] }));
    }

    [Fact]
    public async Task ReviewPreviewRejectsBindingErrors()
    {
        var controller = Controller(true, "admin");
        controller.ModelState.AddModelError("Subtotal", "Invalid number");
        Assert.IsType<BadRequestResult>(await controller.ReviewTotalPreview(
            new ReviewTotalPreviewInput { Id = transactionId }));
    }

    [Theory]
    [InlineData(true, 9500)]
    [InlineData(false, 8888)]
    public async Task ReviewSaveRecalculatesPendingAutoTotalButPreservesManualTotal(bool automatic, int expected)
    {
        var result = await Controller(true, "admin").Review(new ReviewTransactionViewModel
        {
            Id = transactionId, MerchantName = "Cafe", TransactionDate = new DateTime(2026, 10, 1),
            Subtotal = 10_000, GrandTotal = 8888, RecalculateGrandTotal = automatic,
            Items = [new() { Name = "Meal", Quantity = 1, UnitPrice = 10_000, TotalPrice = 10_000 }],
            Charges = [new() { Label = "Voucher", Amount = 1_000, Operation = ChargeOperation.Subtract },
                new() { Label = "Delivery", Amount = 500, Operation = ChargeOperation.Add }]
        });
        Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(expected, (await db.Transactions.AsNoTracking().SingleAsync()).GrandTotal);
    }

    [Fact]
    public async Task TransactionPagesKeepModeratorOwnershipScope()
    {
        db.Users.Add(new ApplicationUser { Id = "other", UserName = "other", DisplayName = "Other" });
        for (var index = 0; index < 24; index++)
            db.Transactions.Add(new BillTransaction { TransactionNumber = $"TX-PAGE-{index}", MerchantName = "Cafe", UploadedByUserId = index < 12 ? "owner" : "other" });
        await db.SaveChangesAsync();
        var controller = Controller(false, "owner");
        controller.Request.QueryString = new QueryString("?page=2");
        var model = Assert.IsType<TransactionListViewModel>(Assert.IsType<ViewResult>(await controller.Index()).Model);
        Assert.Equal(3, model.Transactions.Count);
        Assert.All(model.Transactions, x => Assert.Equal("owner", x.UploadedByUserId));
        Assert.Equal(13, Assert.IsType<PaginationViewModel>(controller.ViewData["Pagination:page"]).TotalItems);
        var admin = Controller(true, "admin");
        await admin.Index();
        Assert.Equal(25, Assert.IsType<PaginationViewModel>(admin.ViewData["Pagination:page"]).TotalItems);
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
            shareExportService: new TransactionShareExportService(new TestLocalizer()), currencyCatalog: new CurrencyCatalog())
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
