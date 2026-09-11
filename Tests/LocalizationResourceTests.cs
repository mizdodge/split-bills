using System.Globalization;
using System.Resources;
using Splitbill;

namespace Splitbill.Tests;

public sealed class LocalizationResourceTests
{
    [Fact]
    public void IndonesianAndEnglishResourceKeysStayInSync()
    {
        var manager = new ResourceManager("Splitbill.SharedResource", typeof(SharedResource).Assembly);
        var baseKeys = Keys(manager.GetResourceSet(new CultureInfo("id-ID"), true, true));
        var englishKeys = Keys(manager.GetResourceSet(new CultureInfo("en-US"), true, true));
        Assert.Equal(baseKeys, englishKeys);
        Assert.NotEmpty(baseKeys);
        Assert.Equal("Dashboard", manager.GetString("Dashboard", new CultureInfo("id-ID")));
        Assert.Equal("Dashboard", manager.GetString("Dashboard", new CultureInfo("en-US")));
        Assert.Equal("Upload struk", manager.GetString("UploadReceipt", new CultureInfo("id-ID")));
        Assert.Equal("Upload receipt", manager.GetString("UploadReceipt", new CultureInfo("en-US")));
    }

    private static string[] Keys(ResourceSet? set) =>
        set is null ? [] : set.Cast<System.Collections.DictionaryEntry>().Select(x => x.Key.ToString()!).OrderBy(x => x).ToArray();
}
