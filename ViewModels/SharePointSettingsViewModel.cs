using System.ComponentModel.DataAnnotations;

namespace Splitbill.ViewModels;

public sealed class SharePointSettingsViewModel
{
    public int Id { get; set; }
    public bool Enabled { get; set; }

    [Display(Name = "Tenant ID")]
    public string TenantId { get; set; } = string.Empty;

    [Display(Name = "Client ID")]
    public string ClientId { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    [Display(Name = "Client Secret")]
    public string? ClientSecret { get; set; }

    [Required, Url, MaxLength(500)]
    [Display(Name = "SharePoint Site URL")]
    public string SiteUrl { get; set; } = string.Empty;

    public string? SiteId { get; set; }
    public string? SiteDisplayName { get; set; }
    public string? SelectedListId { get; set; }
    public string? SelectedListDisplayName { get; set; }
    public string? SelectedListWebUrl { get; set; }
    public DateTimeOffset? LastTestAt { get; set; }
    public bool? LastTestSucceeded { get; set; }
    public string? LastError { get; set; }
    public bool HasStoredClientSecret { get; set; }
    public bool HasSuccessfulTest { get; set; }
    public string? TestedStateToken { get; set; }
    public IReadOnlyList<SharePointListOptionViewModel> Lists { get; set; } = [];
}

public sealed record SharePointListOptionViewModel(string Id, string DisplayName, string? WebUrl);

public sealed class SharePointConnectionTestViewModel
{
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }

    [Required, Url, MaxLength(500)]
    public string SiteUrl { get; set; } = string.Empty;
}

public sealed class SharePointSaveViewModel
{
    public bool Enabled { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string? ClientSecret { get; set; }
    public string SiteUrl { get; set; } = string.Empty;
    public string SiteId { get; set; } = string.Empty;
    public string ListId { get; set; } = string.Empty;
    public string? TestedStateToken { get; set; }
}
