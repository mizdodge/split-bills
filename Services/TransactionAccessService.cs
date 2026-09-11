namespace Splitbill.Services;

public static class TransactionAccessService
{
    public static bool CanManage(bool isAdmin, string? currentUserId, string? uploaderUserId) =>
        isAdmin || (!string.IsNullOrWhiteSpace(currentUserId) &&
                    string.Equals(currentUserId, uploaderUserId, StringComparison.Ordinal));
}
