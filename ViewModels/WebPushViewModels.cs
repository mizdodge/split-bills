namespace Splitbill.ViewModels;

public sealed class WebPushStatusViewModel
{
    public bool Enabled { get; set; }
    public string PublicKey { get; set; } = string.Empty;
    public int ActiveSubscriptions { get; set; }
}

public sealed class WebPushSubscribeRequest
{
    public string? Endpoint { get; set; }
    public string? P256dh { get; set; }
    public string? Auth { get; set; }
    public string? InstallationId { get; set; }
    public string? Culture { get; set; }
    public string? BrowserLabel { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
}

public sealed class WebPushHeartbeatRequest
{
    public string? InstallationId { get; set; }
}

public sealed class WebPushUnsubscribeRequest
{
    public string? Endpoint { get; set; }
    public string? InstallationId { get; set; }
}
