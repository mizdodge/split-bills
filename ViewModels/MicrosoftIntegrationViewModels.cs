using System.ComponentModel.DataAnnotations;

namespace Splitbill.ViewModels;

public sealed class MicrosoftIntegrationSettingsViewModel
{
    [Required, MaxLength(36)] public string TenantId { get; set; } = string.Empty;
    [Required, MaxLength(36)] public string ClientId { get; set; } = string.Empty;
    [DataType(DataType.Password)] public string? ClientSecret { get; set; }
    public bool MicrosoftIntegrationEnabled { get; set; }
    public bool MicrosoftLoginEnabled { get; set; }
    public bool AllowAutoRegistration { get; set; }
    public bool HasStoredClientSecret { get; set; }
    public bool LegacyMigrationAttempted { get; set; }
    public bool LegacyMigrationCompleted { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
    public string? LastError { get; set; }
}

public sealed class MicrosoftIntegrationSaveViewModel
{
    [Required, MaxLength(36)] public string TenantId { get; set; } = string.Empty;
    [Required, MaxLength(36)] public string ClientId { get; set; } = string.Empty;
    [DataType(DataType.Password)] public string? ClientSecret { get; set; }
    public bool MicrosoftIntegrationEnabled { get; set; }
    public bool MicrosoftLoginEnabled { get; set; }
    public bool AllowAutoRegistration { get; set; }
}

public sealed record MicrosoftConnectionResult(bool Succeeded, string? Error, string? AccountDisplayName = null);
