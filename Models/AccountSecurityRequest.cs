namespace Splitbill.Models;

// A public nonce alone grants nothing: requests also require the current local
// account, security stamp, browser binding and freshly verified Microsoft identity.
public sealed class AccountSecurityRequest
{
    public string Id { get; set; } = string.Empty;
    public string UserId { get; set; } = string.Empty;
    public ApplicationUser? User { get; set; }
    public string Purpose { get; set; } = string.Empty;
    public string BindingHash { get; set; } = string.Empty;
    public string StampHash { get; set; } = string.Empty;
    public long CredentialRevision { get; set; }
    public long LinkVersion { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
