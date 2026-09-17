using Microsoft.AspNetCore.Identity;
using System.ComponentModel.DataAnnotations;

namespace Splitbill.Models;

public sealed class ApplicationUser : IdentityUser
{
    // Email and NormalizedEmail are provided by IdentityUser and stored in
    // AspNetUsers. Keep the built-in fields so Identity normalization works.
    [MaxLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    // Microsoft identity is link-only; it is never used as an implicit account selector.
    [MaxLength(200)] public string? MicrosoftSubject { get; set; }
    [MaxLength(36)] public string? MicrosoftTenantId { get; set; }
    public DateTimeOffset? MicrosoftLinkedAt { get; set; }
    public DateTimeOffset? MicrosoftLastVerifiedAt { get; set; }
    public bool MicrosoftLinkRevoked { get; set; }
    public long? MicrosoftCredentialRevision { get; set; }
    [MaxLength(200)] public string? MicrosoftAccountDisplayName { get; set; }
    [MaxLength(320)] public string? MicrosoftAccountEmail { get; set; }
    public long MicrosoftLinkVersion { get; set; }

    public List<WebPushSubscription> WebPushSubscriptions { get; set; } = [];
}
