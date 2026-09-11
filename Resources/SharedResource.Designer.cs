using System.Globalization;
using System.Resources;

namespace Splitbill;

public sealed partial class SharedResource
{
    private static readonly ResourceManager ResourceManager = new("Splitbill.SharedResource", typeof(SharedResource).Assembly);

    private static string Get(string key) => ResourceManager.GetString(key, CultureInfo.CurrentUICulture) ?? key;

    public static string UsernameRequired => Get(nameof(UsernameRequired));
    public static string PasswordRequired => Get(nameof(PasswordRequired));
    public static string CurrentPasswordRequired => Get(nameof(CurrentPasswordRequired));
    public static string NewPasswordRequired => Get(nameof(NewPasswordRequired));
    public static string PasswordMinLength => Get(nameof(PasswordMinLength));
    public static string ConfirmPasswordRequired => Get(nameof(ConfirmPasswordRequired));
    public static string PasswordMismatch => Get(nameof(PasswordMismatch));
}
