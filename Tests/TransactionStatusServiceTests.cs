using Splitbill.Models;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class TransactionStatusServiceTests
{
    [Fact]
    public void NoParticipants_IsDraft() =>
        Assert.Equal(TransactionStatus.Draft, TransactionStatusService.Calculate([]));

    [Fact]
    public void EveryParticipantUnpaid_IsUnpaid() =>
        Assert.Equal(TransactionStatus.Unpaid, TransactionStatusService.Calculate([ParticipantPaymentStatus.Unpaid, ParticipantPaymentStatus.Unpaid]));

    [Fact]
    public void AwaitingConfirmation_RemainsOutstandingAndUnpaidHeader() =>
        Assert.Equal(TransactionStatus.Unpaid, TransactionStatusService.Calculate([ParticipantPaymentStatus.AwaitingConfirmation]));

    [Fact]
    public void PaymentStatusNumericValues_PreserveExistingSqliteRows()
    {
        Assert.Equal(0, (int)ParticipantPaymentStatus.Unpaid);
        Assert.Equal(1, (int)ParticipantPaymentStatus.Paid);
        Assert.Equal(2, (int)ParticipantPaymentStatus.AwaitingConfirmation);
    }

    [Fact]
    public void MixedPayments_IsPartial() =>
        Assert.Equal(TransactionStatus.Partial, TransactionStatusService.Calculate([ParticipantPaymentStatus.Paid, ParticipantPaymentStatus.Unpaid]));

    [Fact]
    public void EveryParticipantPaid_IsPaid() =>
        Assert.Equal(TransactionStatus.Paid, TransactionStatusService.Calculate([ParticipantPaymentStatus.Paid, ParticipantPaymentStatus.Paid]));
}
