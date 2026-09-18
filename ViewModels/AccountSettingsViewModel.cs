using Splitbill.Models;
namespace Splitbill.ViewModels;
public sealed record AccountSettingsViewModel(ApplicationUser User, bool HasPassword, bool SsoEnabled);
