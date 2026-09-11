using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Splitbill.Controllers;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;
using Splitbill.ViewModels;

namespace Splitbill.Tests;

public sealed class DraftDeletionTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly string root = Path.Combine(Path.GetTempPath(), "Splitbill-delete-" + Guid.NewGuid().ToString("N"));
    private string ReceiptPath => Path.Combine(root, "App_Data", "receipts", "receipt.png");

    public DraftDeletionTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        Directory.CreateDirectory(Path.GetDirectoryName(ReceiptPath)!);
        File.WriteAllText(ReceiptPath, "test receipt");
        db.Users.Add(new ApplicationUser { Id = "owner", UserName = "owner", DisplayName = "Owner" });
        db.SaveChanges();
    }

    [Theory]
    [InlineData(true, "other")]
    [InlineData(false, "owner")]
    public async Task AuthorizedDraftDelete_RemovesEntireGraphAndReceipt(bool isAdmin, string userId)
    {
        var id = await Seed(TransactionStatus.Draft);
        var result = Assert.IsType<RedirectToActionResult>(await Controller(isAdmin, userId).Delete(id));
        Assert.Equal("Index", result.ActionName);
        Assert.False(await db.Transactions.AnyAsync());
        Assert.False(await db.TransactionItems.AnyAsync());
        Assert.False(await db.TransactionReceiptImages.AnyAsync());
        Assert.False(await db.TransactionCharges.AnyAsync());
        Assert.False(await db.TransactionParticipants.AnyAsync());
        Assert.False(await db.ParticipantItemAllocations.AnyAsync());
        Assert.False(await db.PaymentHistories.AnyAsync());
        Assert.False(await db.ReceiptProcessingLogs.AnyAsync());
        Assert.False(File.Exists(ReceiptPath));
        Assert.True(await db.Users.AnyAsync());
    }

    [Fact]
    public async Task ModeratorCannotDeleteAnotherUsersDraft()
    {
        var id = await Seed(TransactionStatus.Draft);
        Assert.IsType<NotFoundResult>(await Controller(false, "other").Delete(id));
        Assert.True(await db.Transactions.AnyAsync(x => x.Id == id));
        Assert.True(await db.ReceiptProcessingLogs.AnyAsync());
        Assert.True(File.Exists(ReceiptPath));
    }

    [Theory]
    [InlineData(TransactionStatus.Unpaid)]
    [InlineData(TransactionStatus.Partial)]
    [InlineData(TransactionStatus.Paid)]
    public async Task NonDraftCannotBeDeletedEvenByAdmin(TransactionStatus status)
    {
        var id = await Seed(status);
        var result = Assert.IsType<RedirectToActionResult>(await Controller(true, "other").Delete(id));
        if (status == TransactionStatus.Unpaid)
        {
            Assert.Equal("Index", result.ActionName);
            Assert.False(await db.Transactions.AnyAsync(x => x.Id == id));
            Assert.False(File.Exists(ReceiptPath));
        }
        else
        {
            Assert.Equal("Details", result.ActionName);
            Assert.True(await db.Transactions.AnyAsync(x => x.Id == id && x.Status == status));
            Assert.True(await db.TransactionParticipants.AnyAsync());
            Assert.True(File.Exists(ReceiptPath));
        }
    }

    [Fact]
    public async Task MissingReceiptDoesNotPreventDraftDeletion()
    {
        var id = await Seed(TransactionStatus.Draft);
        File.Delete(ReceiptPath);
        var result = Assert.IsType<RedirectToActionResult>(await Controller(false, "owner").Delete(id));
        Assert.Equal("Index", result.ActionName);
        Assert.False(await db.Transactions.AnyAsync());
    }

    [Fact]
    public async Task MissingDraftReturnsNotFound()
    {
        Assert.IsType<NotFoundResult>(await Controller(true, "other").Delete(999));
        Assert.True(File.Exists(ReceiptPath));
    }

    [Fact]
    public async Task ZeroTotalDraftCanReprocessItsStoredImagesAndReplaceOldExtraction()
    {
        var id = await Seed(TransactionStatus.Draft, 0);
        var ai = new TestAiReceiptService();
        var result = Assert.IsType<RedirectToActionResult>(await Controller(false, "owner", ai).Reprocess(id, default));

        Assert.Equal("Review", result.ActionName);
        Assert.Equal(1, ai.ImageCount);
        var transaction = await db.Transactions.Include(x => x.Items).Include(x => x.Charges).SingleAsync(x => x.Id == id);
        Assert.Equal(22_000m, transaction.GrandTotal);
        Assert.Equal("Reprocessed cafe", transaction.MerchantName);
        Assert.Single(transaction.Items);
        Assert.Equal("Tea", transaction.Items[0].Name);
        Assert.Single(transaction.Charges);
        Assert.Equal(AiProcessingStatus.Succeeded, await db.ReceiptProcessingLogs.Where(x => x.TransactionId == id).OrderByDescending(x => x.Id).Select(x => x.Status).FirstAsync());
    }

    [Fact]
    public async Task SplitPage_LoadsAvailableUsersWithSqliteDateTimeOffsetLockoutCheck()
    {
        var id = await Seed(TransactionStatus.Draft, withPaymentHistory: false);

        var result = Assert.IsType<ViewResult>(await Controller(false, "owner").Split(id));
        var model = Assert.IsType<SplitTransactionViewModel>(result.Model);

        Assert.Contains(model.AvailableUsers, user => user.Id == "owner");
    }

    [Fact]
    public async Task SplitPost_AcceptsCamelCaseParticipantJsonFromBrowser()
    {
        var id = await Seed(TransactionStatus.Draft, withPaymentHistory: false);
        var itemId = await db.TransactionItems.Where(x => x.TransactionId == id).Select(x => x.Id).SingleAsync();
        var model = new SplitTransactionViewModel
        {
            TransactionId = id,
            SplitMethod = SplitMethod.ByItem,
            ParticipantNames = "Yasmin\nOwner",
            ParticipantsJson = """[{"clientKey":"guest:yasmin","userId":null,"name":"Yasmin"},{"clientKey":"user:owner","userId":"owner","name":"Owner"}]""",
            AssignmentsJson = $$"""{"{{itemId}}":["guest:yasmin","user:owner"]}"""
        };

        var result = Assert.IsType<RedirectToActionResult>(await Controller(false, "owner").Split(model));

        Assert.Equal("Details", result.ActionName);
        var participants = await db.TransactionParticipants.Include(x => x.AccountLink)
            .Where(x => x.TransactionId == id).OrderBy(x => x.Name).ToListAsync();
        Assert.Equal(2, participants.Count);
        Assert.Contains(participants, x => x.Name == "Owner" && x.AccountLink!.UserId == "owner");
        Assert.Contains(participants, x => x.Name == "Yasmin" && x.AccountLink == null);
    }

    [Fact]
    public async Task SplitPost_AssignsEligibleParticipantAsPickupPerson()
    {
        var id = await Seed(TransactionStatus.Draft, withPaymentHistory: false);
        db.FoodPickupConfigurations.Add(new FoodPickupConfiguration { Id = 1, Enabled = true, UpdatedAt = DateTimeOffset.UtcNow });
        db.FoodPickupEligibleUsers.Add(new FoodPickupEligibleUser { UserId = "owner", EnabledAt = DateTimeOffset.UtcNow, EnabledByUserId = "owner" });
        await db.SaveChangesAsync();

        var model = new SplitTransactionViewModel
        {
            TransactionId = id,
            SplitMethod = SplitMethod.Equal,
            ParticipantNames = "Owner",
            ParticipantsJson = "[{\"clientKey\":\"user:owner\",\"userId\":\"owner\",\"name\":\"Owner\"}]"
        };
        var result = Assert.IsType<RedirectToActionResult>(await Controller(false, "owner", pickup: new FoodPickupRotationService(db, new FixedRandomSource(0d))).Split(model));

        Assert.Equal("Details", result.ActionName);
        var assignment = await db.FoodPickupAssignments.SingleAsync(x => x.TransactionId == id);
        Assert.Equal("owner", assignment.SelectedUserId);
        Assert.Single(await db.FoodPickupDrawHistories.Where(x => x.TransactionId == id).ToListAsync());
    }

    [Fact]
    public async Task Details_LoadsParticipantOrderBreakdownWithPersistedItemAllocations()
    {
        var id = await Seed(TransactionStatus.Unpaid);

        var result = Assert.IsType<ViewResult>(await Controller(true, "other").Details(id));
        var model = Assert.IsType<TransactionDetailsViewModel>(result.Model);
        var participant = Assert.Single(model.Transaction.Participants);
        var breakdown = Assert.Single(model.ParticipantBreakdowns).Value;

        Assert.Equal(participant.Id, breakdown.ParticipantId);
        var item = Assert.Single(breakdown.Items);
        Assert.Equal("Coffee", item.Name);
        Assert.Equal(1m, item.QuantityShare);
        Assert.Equal(10_000m, item.ParticipantAmount);
        var charge = Assert.Single(breakdown.Adjustments);
        Assert.Equal("PB1", charge.Label);
        Assert.Equal(1_000m, charge.ParticipantAmount);
        Assert.Equal(11_000m, breakdown.FinalAmount);
    }

    private async Task<long> Seed(TransactionStatus status, decimal grandTotal = 11_000, bool withPaymentHistory = true)
    {
        var item = new TransactionItem { Name = "Coffee", Quantity = 1, UnitPrice = 10000, TotalPrice = 10000 };
        var participant = new TransactionParticipant
        {
            Name = "Test", Amount = 11000,
            ItemAllocations = [new ParticipantItemAllocation { Item = item, QuantityShare = 1, Amount = 10000 }]
        };
        if (withPaymentHistory) participant.PaymentHistories.Add(new PaymentHistory { ChangedByUserId = "owner" });
        var transaction = new BillTransaction
        {
            TransactionNumber = "TEST-DELETE", MerchantName = "Test", UploadedByUserId = "owner",
            ReceiptImagePath = "receipt.png", Status = status, GrandTotal = grandTotal,
            Items = [item], Participants = [participant],
            ReceiptImages = [new TransactionReceiptImage { FileName = "receipt.png", ContentType = "image/png" }],
            Charges = [new TransactionCharge { Label = "PB1", Amount = 1000 }],
            ProcessingLogs = [new ReceiptProcessingLog { Status = AiProcessingStatus.Failed }]
        };
        db.Transactions.Add(transaction);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        return transaction.Id;
    }

    private TransactionsController Controller(bool isAdmin, string userId, IAiReceiptService? ai = null, IFoodPickupRotationService? pickup = null)
    {
        var users = new UserManager<ApplicationUser>(new UserStore<ApplicationUser>(db),
            Options.Create(new IdentityOptions()), new PasswordHasher<ApplicationUser>(), [], [],
            new UpperInvariantLookupNormalizer(), new IdentityErrorDescriber(), null!,
            NullLogger<UserManager<ApplicationUser>>.Instance);
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, userId),
                new Claim(ClaimTypes.Role, isAdmin ? DatabaseSeeder.AdminRole : DatabaseSeeder.ModeratorRole)
            }, "Test"))
        };
        return new TransactionsController(db, users, ai!, new SplitBillCalculator(), new TestEnvironment { ContentRootPath = root }, pickupRotationService: pickup)
        {
            ControllerContext = new ControllerContext { HttpContext = context },
            TempData = new TempDataDictionary(context, new TestTempDataProvider())
        };
    }

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
        File.Delete(ReceiptPath);
        Directory.Delete(Path.GetDirectoryName(ReceiptPath)!);
        Directory.Delete(Path.Combine(root, "App_Data"));
        Directory.Delete(root);
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) => new Dictionary<string, object>();
        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }

    private sealed class TestAiReceiptService : IAiReceiptService
    {
        public int ImageCount { get; private set; }

        public Task<(ReceiptAiResult Result, string RawResponse)> AnalyzeAsync(
            IReadOnlyList<ReceiptImageInput> images, CancellationToken cancellationToken = default)
        {
            ImageCount = images.Count;
            var result = new ReceiptAiResult
            {
                SchemaVersion = "2.0", MerchantName = "Reprocessed cafe", Currency = "IDR",
                Subtotal = 20_000, GrandTotal = 22_000, Confidence = .95m,
                Items = [new ReceiptItemAiResult { LineNumber = 1, Name = "Tea", Quantity = 1, UnitPrice = 20_000, TotalPrice = 20_000, Confidence = .95m }],
                Charges = [new ReceiptChargeAiResult { Label = "PB1", Amount = 2_000, Operation = "add" }]
            };
            return Task.FromResult((result, "{}"));
        }

        public Task TestConnectionAsync(AiConfiguration configuration, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
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

    private sealed class FixedRandomSource(double value) : IFoodPickupRandomSource
    {
        public double NextUnitInterval() => value;
    }
}
