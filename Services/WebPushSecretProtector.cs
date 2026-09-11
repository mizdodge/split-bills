using Microsoft.AspNetCore.DataProtection;

namespace Splitbill.Services;

public sealed class WebPushSecretProtector(IDataProtectionProvider dataProtectionProvider)
{
    private readonly IDataProtector vapidPrivateKeyProtector =
        dataProtectionProvider.CreateProtector(WebPushProtection.VapidPrivateKeyPurpose);
    private readonly IDataProtector subscriptionProtector =
        dataProtectionProvider.CreateProtector(WebPushProtection.SubscriptionPurpose);

    public string ProtectVapidPrivateKey(string value) => vapidPrivateKeyProtector.Protect(value);

    public string UnprotectVapidPrivateKey(string value) => vapidPrivateKeyProtector.Unprotect(value);

    public string ProtectSubscriptionValue(string value) => subscriptionProtector.Protect(value);

    public string UnprotectSubscriptionValue(string value) => subscriptionProtector.Unprotect(value);

    public string Hash(string value) => WebPushProtection.Hash(value);
}
