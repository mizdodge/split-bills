using Splitbill.Models;

namespace Splitbill.Services;

public sealed record TransactionEditDecision(bool Allowed, string MessageKey = "EditLocked");

public static class TransactionEditPolicy
{
    public static TransactionEditDecision Evaluate(BillTransaction transaction)
    {
        var hasActivity = transaction.Participants.Any(participant =>
            participant.PaymentApprovals.Count > 0 || participant.PaymentHistories.Count > 0 || participant.PaymentStatus != ParticipantPaymentStatus.Unpaid);
        if (hasActivity) return new(false);
        if (transaction.Status == TransactionStatus.Draft) return new(true);
        if (transaction.Status != TransactionStatus.Unpaid) return new(false);
        return new(true);
    }
}
