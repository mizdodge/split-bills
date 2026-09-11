using Splitbill.Models;

namespace Splitbill.Services;

public static class TransactionStatusService
{
    public static TransactionStatus Calculate(IEnumerable<ParticipantPaymentStatus> statuses)
    {
        var values = statuses.ToArray();
        if (values.Length == 0) return TransactionStatus.Draft;
        if (values.All(x => x == ParticipantPaymentStatus.Paid)) return TransactionStatus.Paid;
        if (values.Any(x => x == ParticipantPaymentStatus.Paid)) return TransactionStatus.Partial;
        return TransactionStatus.Unpaid;
    }
}
