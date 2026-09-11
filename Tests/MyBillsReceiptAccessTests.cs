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
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Splitbill.Controllers;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class MyBillsReceiptAccessTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly UserManager<ApplicationUser> users;
    private readonly string root = Path.Combine(Path.GetTempPath(), "Splitbill-member-receipt-" + Guid.NewGuid().ToString("N"));

    public MyBillsReceiptAccessTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser>(db), Options.Create(new IdentityOptions()),
            new PasswordHasher<ApplicationUser>(), [], [], new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(),
            null!, NullLogger<UserManager<ApplicationUser>>.Instance);
        Directory.CreateDirectory(Path.Combine(root, "App_Data", "receipts"));
        db.Users.AddRange(new ApplicationUser { Id = "member", UserName = "member" }, new ApplicationUser { Id = "other", UserName = "other" });
        db.SaveChanges();
    }

    [Fact]
    public async Task ReceiptImage_AllowsOwnParticipantAndDeniesCrossUser()
    {
        var transaction = new BillTransaction { TransactionNumber = "TRX-RECEIPT-1", MerchantName = "Cafe", UploadedByUserId = "member", ReceiptImagePath = "legacy.jpg" };
        var own = new TransactionParticipant { Name = "Member", Amount = 1000, AccountLink = new ParticipantAccountLink { UserId = "member" } };
        var other = new TransactionParticipant { Name = "Other", Amount = 1000, AccountLink = new ParticipantAccountLink { UserId = "other" } };
        transaction.Participants.Add(own);
        transaction.Participants.Add(other);
        transaction.ReceiptImages.Add(new TransactionReceiptImage { FileName = "receipt.png", ContentType = "image/png", SortOrder = 0 });
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        await File.WriteAllBytesAsync(Path.Combine(root, "App_Data", "receipts", "receipt.png"), [0x89, 0x50, 0x4e, 0x47]);

        var controller = Controller("member");
        var result = Assert.IsType<PhysicalFileResult>(await controller.ReceiptImage(own.Id));
        Assert.Equal("image/png", result.ContentType);
        Assert.Equal("nosniff", controller.Response.Headers["X-Content-Type-Options"]);

        var denied = await Controller("other").ReceiptImage(own.Id);
        Assert.IsType<NotFoundResult>(denied);
    }

    [Fact]
    public async Task ReceiptImage_UsesLegacyFallbackAndRejectsOutOfRange()
    {
        var transaction = new BillTransaction { TransactionNumber = "TRX-RECEIPT-2", MerchantName = "Cafe", UploadedByUserId = "member", ReceiptImagePath = "legacy.jpg" };
        var own = new TransactionParticipant { Name = "Member", Amount = 1000, AccountLink = new ParticipantAccountLink { UserId = "member" } };
        transaction.Participants.Add(own);
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        await File.WriteAllBytesAsync(Path.Combine(root, "App_Data", "receipts", "legacy.jpg"), [0xff, 0xd8, 0xff]);

        var result = Assert.IsType<PhysicalFileResult>(await Controller("member").ReceiptImage(own.Id));
        Assert.Equal("image/jpeg", result.ContentType);
        Assert.IsType<NotFoundResult>(await Controller("member").ReceiptImage(own.Id, 1));
    }

    [Fact]
    public async Task DetailsProjectsPickupWinnerMetadataForSelectedMember()
    {
        var transaction = new BillTransaction
        {
            TransactionNumber = "TRX-PICKUP-1", MerchantName = "Cafe", UploadedByUserId = "member",
            Status = TransactionStatus.Unpaid, Subtotal = 10_000, GrandTotal = 10_000,
            Items = [new TransactionItem { LineNumber = 1, Name = "Nasi", Quantity = 1, UnitPrice = 10_000, TotalPrice = 10_000 }]
        };
        var participant = new TransactionParticipant
        {
            Name = "Member", Amount = 10_000,
            AccountLink = new ParticipantAccountLink { UserId = "member" }
        };
        transaction.Participants.Add(participant);
        transaction.PickupAssignment = new FoodPickupAssignment
        {
            SelectedUserId = "member", SelectedParticipant = participant,
            RecordedProbability = 1m, DrawKind = FoodPickupDrawKind.Initial,
            SelectedAt = DateTimeOffset.UtcNow
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        transaction.PickupAssignment!.SelectedParticipantId = participant.Id;
        await db.SaveChangesAsync();
        Assert.Equal(participant.Id, transaction.PickupAssignment.SelectedParticipantId);

        var result = Assert.IsType<ViewResult>(await Controller("member").Details(participant.Id, CancellationToken.None));
        var model = Assert.IsType<Splitbill.ViewModels.MyBillDetailsViewModel>(result.Model);
        Assert.True(model.IsPickupPerson);
        Assert.Equal("Member", model.PickupPersonName);
        Assert.Equal(1m, model.PickupProbability);
        Assert.Equal(FoodPickupDrawKind.Initial, model.PickupDrawKind);
    }

    private MyBillsController Controller(string userId)
    {
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")) };
        return new MyBillsController(db, users, new PaymentWorkflowService(db), new PaymentProofStorageService(new TestEnvironment { ContentRootPath = root }), new TestLocalizer(), new SplitBillCalculator(), new TestEnvironment { ContentRootPath = root })
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
    }

    public void Dispose()
    {
        users.Dispose();
        db.Dispose();
        connection.Dispose();
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }

    private sealed class TestLocalizer : IStringLocalizer<SharedResource>
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, string.Format(CultureInfo.InvariantCulture, name, arguments));
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
        public IStringLocalizer WithCulture(CultureInfo culture) => this;
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
}
