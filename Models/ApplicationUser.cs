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

    public List<WebPushSubscription> WebPushSubscriptions { get; set; } = [];
}
