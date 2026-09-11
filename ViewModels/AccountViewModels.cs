using System.ComponentModel.DataAnnotations;
using Splitbill;

namespace Splitbill.ViewModels;

public sealed class LoginViewModel
{
    [Required(ErrorMessageResourceType = typeof(SharedResource), ErrorMessageResourceName = nameof(SharedResource.UsernameRequired))]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(SharedResource), ErrorMessageResourceName = nameof(SharedResource.PasswordRequired))]
    [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
}

public sealed class ChangePasswordViewModel
{
    [Required(ErrorMessageResourceType = typeof(SharedResource), ErrorMessageResourceName = nameof(SharedResource.CurrentPasswordRequired))]
    [DataType(DataType.Password)]
    public string CurrentPassword { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(SharedResource), ErrorMessageResourceName = nameof(SharedResource.NewPasswordRequired))]
    [StringLength(100, MinimumLength = 6, ErrorMessageResourceType = typeof(SharedResource), ErrorMessageResourceName = nameof(SharedResource.PasswordMinLength))]
    [DataType(DataType.Password)]
    public string NewPassword { get; set; } = string.Empty;

    [Required(ErrorMessageResourceType = typeof(SharedResource), ErrorMessageResourceName = nameof(SharedResource.ConfirmPasswordRequired))]
    [Compare(nameof(NewPassword), ErrorMessageResourceType = typeof(SharedResource), ErrorMessageResourceName = nameof(SharedResource.PasswordMismatch))]
    [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class FirstAdminSetupViewModel
{
    [Required] public string BootstrapCode { get; set; } = string.Empty;
    [Required] public string Username { get; set; } = string.Empty;
    [Required] public string DisplayName { get; set; } = string.Empty;
    [EmailAddress] public string? Email { get; set; }
    [Required, StringLength(100, MinimumLength = 6)] [DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;
    [Required, Compare(nameof(Password))] [DataType(DataType.Password)]
    public string ConfirmPassword { get; set; } = string.Empty;
}
