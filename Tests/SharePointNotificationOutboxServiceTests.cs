using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class SharePointNotificationOutboxServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;

    public SharePointNotificationOutboxServiceTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Users.AddRange(
            new ApplicationUser { Id = "owner", UserName = "owner", DisplayName = "Owner", Email = "owner@example.com" },
            new ApplicationUser { Id = "member", UserName = "member", DisplayName = "Member", Email = "member@example.com" });
        db.SharePointConfigurations.Add(new SharePointConfiguration
        {
            Id = 1, Enabled = true, LastTestSucceeded = true, SiteId = "site", ListId = "list",
            TenantId = Guid.NewGuid().ToString(), ClientId = Guid.NewGuid().ToString(),
            ProtectedClientSecret = "protected", SiteUrl = "https://tenant.sharepoint.com/sites/test",
            SiteDisplayName = "Test", ListDisplayName = "SplitBill"
        });
        db.SaveChanges();
    }

    [Fact]
    public async Task BillAssignmentCreatesOnePendingRowForLinkedAccount()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("id-ID");
        try
        {
        var participant = new TransactionParticipant
        {
            Id = 42,
            Name = "Ragil", Amount = 24_000,
            AccountLink = new ParticipantAccountLink { UserId = "member" }
        };
        var transaction = new BillTransaction
        {
            Id = 9, TransactionNumber = "TRX-9", MerchantName = "Warung & Cafe", UploadedByUserId = "owner",
            Participants = [participant]
        };

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("192.0.2.10", 8080);
        await new SharePointNotificationOutboxService(db, new HttpContextAccessor { HttpContext = httpContext })
            .EnqueueBillAssignmentsAsync(transaction, "owner");
        await db.SaveChangesAsync();

        var row = await db.SharePointNotificationOutbox.SingleAsync();
        Assert.Equal(SharePointNotificationEventType.BillAssigned, row.EventType);
        Assert.Equal("member@example.com", row.RecipientEmail);
        Assert.Contains("Rp 24.000", row.Description);
        Assert.Contains("Halo Member,<br><br>Tagihan baru", row.Description);
        Assert.Contains("Merchant: Warung &amp; Cafe<br>Nominal: Rp 24.000<br>Dibuat oleh: Owner", row.Description);
        Assert.Equal("http://192.0.2.10:8080", transaction.NotificationBaseUrl);
        Assert.Contains("href=\"http://192.0.2.10:8080/MyBills/Details/42\"", row.Description);
        Assert.Equal(SharePointNotificationStatus.Pending, row.Status);
        }
        finally { CultureInfo.CurrentUICulture = originalCulture; }
    }

    [Fact]
    public async Task LaterPaymentEventsReuseUploaderOrigin()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("en-US");
        try
        {
            var participant = new TransactionParticipant
            {
                Id = 43, Name = "Ragil", Amount = 24_000,
                Transaction = new BillTransaction
                {
                    Id = 11, TransactionNumber = "TRX-11", MerchantName = "Warung",
                    UploadedByUserId = "owner", NotificationBaseUrl = "https://splitbill.example.com/SplitBill"
                },
                AccountLink = new ParticipantAccountLink { UserId = "member" }
            };
            var approval = new PaymentApproval { Id = 8, Note = "Receipt is unclear" };
            var httpContext = new DefaultHttpContext();
            httpContext.Request.Scheme = "http";
            httpContext.Request.Host = new HostString("member-device", 8080);
            var service = new SharePointNotificationOutboxService(db, new HttpContextAccessor { HttpContext = httpContext });

            await service.EnqueuePaymentApprovalRequestedAsync(participant, approval, "member");
            await service.EnqueuePaymentRejectedAsync(participant, approval, "owner");
            await db.SaveChangesAsync();

            var rows = await db.SharePointNotificationOutbox.OrderBy(x => x.EventType).ToListAsync();
            Assert.Equal(2, rows.Count);
            Assert.Contains("href=\"https://splitbill.example.com/SplitBill/Payments/Approvals\"", rows[0].Description);
            Assert.Contains("href=\"https://splitbill.example.com/SplitBill/MyBills/Details/43\"", rows[1].Description);
            Assert.DoesNotContain("member-device", string.Join("\n", rows.Select(x => x.Description)));
        }
        finally { CultureInfo.CurrentUICulture = originalCulture; }
    }

    [Fact]
    public async Task DisabledIntegrationDoesNotCreateRows()
    {
        (await db.SharePointConfigurations.SingleAsync()).Enabled = false;
        await db.SaveChangesAsync();
        var transaction = new BillTransaction
        {
            Id = 10, TransactionNumber = "TRX-10", MerchantName = "Warung", UploadedByUserId = "owner",
            Participants = [new TransactionParticipant { Name = "Guest", Amount = 1_000 }]
        };

        await new SharePointNotificationOutboxService(db).EnqueueBillAssignmentsAsync(transaction, "owner");
        await db.SaveChangesAsync();

        Assert.Empty(await db.SharePointNotificationOutbox.ToListAsync());
    }

    [Fact]
    public async Task FoodPickupWinnerCreatesStableLocalizedRowWithSafeDeepLink()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("id-ID");
        try
        {
            var participant = new TransactionParticipant
            {
                Id = 44, Name = "Ragil <test>", Amount = 24_000,
                AccountLink = new ParticipantAccountLink { UserId = "member" }
            };
            var transaction = new BillTransaction
            {
                Id = 12, TransactionNumber = "TRX-PICKUP-12", MerchantName = "Warung & Cafe",
                UploadedByUserId = "owner", NotificationBaseUrl = "https://split.example/SplitBill",
                Participants = [participant]
            };
            var assignment = new FoodPickupAssignment
            {
                TransactionId = transaction.Id, SelectedUserId = "member", SelectedParticipantId = participant.Id,
                RecordedProbability = 2m / 3m, DrawKind = FoodPickupDrawKind.Initial,
                SelectedAt = DateTimeOffset.UtcNow
            };
            var history = new FoodPickupDrawHistory { Id = 77, TransactionId = transaction.Id, SelectedUserId = "member", SelectedParticipantId = participant.Id, CandidateSnapshotJson = "[]" };
            var service = new SharePointNotificationOutboxService(db);

            await service.EnqueueFoodPickupSelectedAsync(transaction, assignment, history, "owner");
            await service.EnqueueFoodPickupSelectedAsync(transaction, assignment, history, "owner");
            await db.SaveChangesAsync();

            var row = await db.SharePointNotificationOutbox.SingleAsync(x => x.EventType == SharePointNotificationEventType.FoodPickupSelected);
            Assert.Equal("food-pickup:77", row.EventId);
            Assert.Equal("member@example.com", row.RecipientEmail);
            Assert.Contains("Selamat", row.Description);
            Assert.Contains("Peluang saat diundi: 66,67%", row.Description);
            Assert.Contains("Warung &amp; Cafe", row.Description);
            Assert.Contains("Selamat, Member!", row.Description);
            Assert.Contains("<br>", row.Description);
            Assert.Contains("href=\"https://split.example/SplitBill/MyBills/Details/44\"", row.Description);
            Assert.Equal(24_000m, row.Amount);
            Assert.Equal(1, await db.SharePointNotificationOutbox.CountAsync(x => x.EventId == "food-pickup:77"));
        }
        finally { CultureInfo.CurrentUICulture = originalCulture; }
    }

    [Fact]
    public async Task RoundRobinPickupNotificationDescribesTurnWithoutFakeProbability()
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = new CultureInfo("id-ID");
        try
        {
            var participant = new TransactionParticipant
            {
                Id = 45, Name = "Ragil", Amount = 24_000,
                AccountLink = new ParticipantAccountLink { UserId = "member" }
            };
            var transaction = new BillTransaction
            {
                Id = 13, TransactionNumber = "TRX-ROUND-13", MerchantName = "Warung",
                UploadedByUserId = "owner", Participants = [participant]
            };
            var assignment = new FoodPickupAssignment
            {
                TransactionId = transaction.Id, SelectedUserId = "member", SelectedParticipantId = participant.Id,
                Strategy = FoodPickupSelectionStrategy.RoundRobin, RecordedProbability = 1m
            };
            var history = new FoodPickupDrawHistory { Id = 78, TransactionId = transaction.Id, SelectedUserId = "member", SelectedParticipantId = participant.Id };

            await new SharePointNotificationOutboxService(db)
                .EnqueueFoodPickupSelectedAsync(transaction, assignment, history, "owner");
            await db.SaveChangesAsync();

            var row = await db.SharePointNotificationOutbox.SingleAsync(x => x.EventId == "food-pickup:78");
            Assert.Contains("Metode: Round robin berdasarkan urutan history", row.Description);
            Assert.DoesNotContain("Peluang saat diundi", row.Description);
        }
        finally { CultureInfo.CurrentUICulture = originalCulture; }
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
