using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class TransactionAccessServiceTests
{
    [Fact]
    public void Admin_CanManageAnotherUsersTransaction() =>
        Assert.True(TransactionAccessService.CanManage(true, "admin-id", "fifi-id"));

    [Fact]
    public void Moderator_CanManageOwnTransaction() =>
        Assert.True(TransactionAccessService.CanManage(false, "fifi-id", "fifi-id"));

    [Fact]
    public void Moderator_CannotManageAnotherUsersTransaction() =>
        Assert.False(TransactionAccessService.CanManage(false, "fifi-id", "other-id"));
}
