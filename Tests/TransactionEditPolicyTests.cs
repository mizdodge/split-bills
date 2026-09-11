using Splitbill.Models;
using Splitbill.Services;
using Splitbill.Controllers;
using Microsoft.AspNetCore.Mvc;
using System.Reflection;

namespace Splitbill.Tests;

public sealed class TransactionEditPolicyTests
{
    [Fact]
    public void PickupRerollIsPostAndAntiforgeryProtected()
    {
        var method = typeof(TransactionsController).GetMethod(nameof(TransactionsController.RerollPickup));
        Assert.NotNull(method);
        Assert.NotNull(method!.GetCustomAttribute<HttpPostAttribute>());
        Assert.NotNull(method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>());
    }

    [Fact]
    public void DraftIsEditable()
    {
        var result = TransactionEditPolicy.Evaluate(new BillTransaction { Status = TransactionStatus.Draft });
        Assert.True(result.Allowed);
    }

    [Fact]
    public void UnpaidWithoutPaymentActivityIsEditable()
    {
        var result = TransactionEditPolicy.Evaluate(new BillTransaction
        {
            Status = TransactionStatus.Unpaid,
            Participants = [new TransactionParticipant { PaymentStatus = ParticipantPaymentStatus.Unpaid }]
        });
        Assert.True(result.Allowed);
    }

    [Fact]
    public void UnpaidWithRejectedAttemptIsLocked()
    {
        var result = TransactionEditPolicy.Evaluate(new BillTransaction
        {
            Status = TransactionStatus.Unpaid,
            Participants = [new TransactionParticipant
            {
                PaymentStatus = ParticipantPaymentStatus.Unpaid,
                PaymentApprovals = [new PaymentApproval { Status = PaymentApprovalStatus.Rejected }]
            }]
        });
        Assert.False(result.Allowed);
    }

    [Fact]
    public void DraftWithPaymentHistoryIsLocked()
    {
        var result = TransactionEditPolicy.Evaluate(new BillTransaction
        {
            Status = TransactionStatus.Draft,
            Participants = [new TransactionParticipant { PaymentHistories = [new PaymentHistory()] }]
        });
        Assert.False(result.Allowed);
    }
}
