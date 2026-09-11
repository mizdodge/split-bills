using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Splitbill.Data;
using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class PaymentWorkflowServiceTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly ApplicationDbContext db;
    private readonly long participantId;

    public PaymentWorkflowServiceTests()
    {
        connection.Open();
        db = new ApplicationDbContext(new DbContextOptionsBuilder<ApplicationDbContext>().UseSqlite(connection).Options);
        db.Database.EnsureCreated();
        db.Users.AddRange(
            new ApplicationUser { Id = "owner", UserName = "owner" },
            new ApplicationUser { Id = "member", UserName = "member" },
            new ApplicationUser { Id = "other", UserName = "other" });
        var participant = new TransactionParticipant
        {
            Name = "Member", Amount = 25_000,
            AccountLink = new ParticipantAccountLink { UserId = "member", LinkedAt = DateTimeOffset.UtcNow }
        };
        var transaction = new BillTransaction
        {
            TransactionNumber = "TEST-PAYMENT", MerchantName = "Cafe", UploadedByUserId = "owner",
            GrandTotal = 25_000, Status = TransactionStatus.Unpaid, Participants = [participant]
        };
        db.Transactions.Add(transaction);
        db.SaveChanges();
        participantId = participant.Id;
        db.ChangeTracker.Clear();
    }

    [Fact]
    public async Task MemberClaim_BecomesAwaitingAndNotifiesOwner()
    {
        var result = await Service().SubmitClaimAsync(participantId, "member");

        Assert.True(result.Succeeded);
        var participant = await db.TransactionParticipants.Include(x => x.PaymentApprovals).Include(x => x.PaymentHistories).SingleAsync();
        Assert.Equal(ParticipantPaymentStatus.AwaitingConfirmation, participant.PaymentStatus);
        Assert.Single(participant.PaymentApprovals, x => x.Status == PaymentApprovalStatus.Pending);
        Assert.Contains(participant.PaymentHistories, x => x.ActionType == PaymentActionType.MemberSubmitted);
        Assert.Contains(await db.UserNotifications.ToListAsync(), x => x.UserId == "owner" && x.Type == NotificationType.PaymentSubmitted);
    }

    [Fact]
    public async Task OwnerConfirm_MarksPaidAndNotifiesMember()
    {
        await Service().SubmitClaimAsync(participantId, "member");
        var result = await Service().ConfirmAsync(participantId, "owner", false);

        Assert.True(result.Succeeded);
        var participant = await db.TransactionParticipants.Include(x => x.PaymentApprovals).Include(x => x.PaymentHistories).SingleAsync();
        Assert.Equal(ParticipantPaymentStatus.Paid, participant.PaymentStatus);
        Assert.Equal(TransactionStatus.Paid, await db.Transactions.Select(x => x.Status).SingleAsync());
        Assert.Single(participant.PaymentApprovals, x => x.Status == PaymentApprovalStatus.Approved);
        Assert.Contains(participant.PaymentHistories, x => x.ActionType == PaymentActionType.ModeratorConfirmed);
        Assert.Contains(await db.UserNotifications.ToListAsync(), x => x.UserId == "member" && x.Type == NotificationType.PaymentApproved);
    }

    [Fact]
    public async Task OwnerReject_ReturnsUnpaidAndNotifiesMember()
    {
        await Service().SubmitClaimAsync(participantId, "member");
        var result = await Service().RejectAsync(participantId, "owner", false, "Belum masuk");

        Assert.True(result.Succeeded);
        var participant = await db.TransactionParticipants.Include(x => x.PaymentApprovals).Include(x => x.PaymentHistories).SingleAsync();
        Assert.Equal(ParticipantPaymentStatus.Unpaid, participant.PaymentStatus);
        Assert.Single(participant.PaymentApprovals, x => x.Status == PaymentApprovalStatus.Rejected && x.Note == "Belum masuk");
        Assert.Contains(participant.PaymentHistories, x => x.ActionType == PaymentActionType.ModeratorRejected && x.Note == "Belum masuk");
        Assert.Contains(await db.UserNotifications.ToListAsync(), x => x.UserId == "member" && x.Type == NotificationType.PaymentRejected);
    }

    [Fact]
    public async Task OwnerCanMarkUnpaidDirectly()
    {
        var result = await Service().MarkPaidAsync(participantId, "owner", false);

        Assert.True(result.Succeeded);
        var participant = await db.TransactionParticipants.Include(x => x.PaymentHistories).SingleAsync();
        Assert.Equal(ParticipantPaymentStatus.Paid, participant.PaymentStatus);
        Assert.Contains(participant.PaymentHistories, x => x.ActionType == PaymentActionType.ModeratorMarkedPaid);
    }

    [Fact]
    public async Task OtherModeratorCannotManageTheParticipant()
    {
        var result = await Service().MarkPaidAsync(participantId, "other", false);

        Assert.False(result.Succeeded);
        Assert.Equal(ParticipantPaymentStatus.Unpaid, await db.TransactionParticipants.Select(x => x.PaymentStatus).SingleAsync());
    }

    [Fact]
    public async Task MemberCannotClaimAnotherUserParticipant()
    {
        var result = await Service().SubmitClaimAsync(participantId, "other");

        Assert.False(result.Succeeded);
        Assert.Empty(await db.PaymentApprovals.ToListAsync());
    }

    private PaymentWorkflowService Service() => new(db);

    public void Dispose()
    {
        db.Dispose();
        connection.Dispose();
    }
}
