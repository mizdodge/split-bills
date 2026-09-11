using System.ComponentModel.DataAnnotations;

namespace Splitbill.ViewModels;

public sealed class AdminUserIndexViewModel
{
    public string? Search { get; set; }
    public string? Role { get; set; }
    public string? Status { get; set; }
    public IReadOnlyList<AdminUserRowViewModel> Users { get; set; } = [];
}

public sealed record AdminUserRowViewModel(
    string Id,
    string Username,
    string DisplayName,
    string? Email,
    string Role,
    bool IsDisabled,
    DateTimeOffset CreatedAt);

public sealed class AdminUserCreateViewModel
{
    [Required, StringLength(100)] public string Username { get; set; } = string.Empty;
    [Required, StringLength(100)] public string DisplayName { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
    [Required] public string Role { get; set; } = "Member";
    [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class AdminUserEditViewModel
{
    [Required] public string Id { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    [Required, StringLength(100)] public string DisplayName { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
    [Required] public string Role { get; set; } = "Member";
    public bool IsDisabled { get; set; }
}

public sealed class AdminUserResetPasswordViewModel
{
    [Required] public string UserId { get; set; } = string.Empty;
    [Required, DataType(DataType.Password)] public string Password { get; set; } = string.Empty;
    [Required, DataType(DataType.Password), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty;
}
