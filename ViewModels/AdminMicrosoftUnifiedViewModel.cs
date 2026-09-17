namespace Splitbill.ViewModels;

public sealed class AdminMicrosoftUnifiedViewModel
{
    public string ActiveTab { get; set; } = "sso";
    public MicrosoftIntegrationSettingsViewModel Microsoft { get; set; } = new();
    public SharePointSettingsViewModel SharePoint { get; set; } = new();
    public string CallbackUrl { get; set; } = string.Empty;
    public int OutboxPendingCount { get; set; }
    public int OutboxDeliveredCount { get; set; }
    public int OutboxDeadLetterCount { get; set; }
}
