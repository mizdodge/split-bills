using Splitbill.Models;
namespace Splitbill.ViewModels;
public sealed record AccountSettingsViewModel(ApplicationUser User, bool HasPassword, bool SsoEnabled)
{
    public IReadOnlyList<string> Roles { get; init; } = [];
}
public sealed class AccountProfileInput { public string? DisplayName { get; set; } }
public sealed class AccountPreferencesInput
{
    public string? Culture { get; set; }
    public bool TeamsNotificationsEnabled { get; set; }
    public bool BrowserNotificationsEnabled { get; set; }
}
public sealed class AccountPasswordInput
{
    public string? RequestId { get; set; }
    public string? Password { get; set; }
    public string? ConfirmPassword { get; set; }
}
