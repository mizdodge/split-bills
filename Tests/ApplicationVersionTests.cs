using System.Xml.Linq;
using Splitbill.Services;

namespace Splitbill.Tests;

public sealed class ApplicationVersionTests
{
    private static string Root => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../.."));

    [Fact]
    public void RuntimeVersionMatchesAuthoritativeProjectVersion()
    {
        var project = XDocument.Load(Path.Combine(Root, "Splitbill.csproj"));
        var projectVersion = project.Descendants("Version").Select(x => x.Value).Single();
        var runtime = ApplicationVersionProvider.Read(typeof(Program).Assembly);

        Assert.Equal("1.0.0", projectVersion);
        Assert.Equal(projectVersion, runtime.SemanticVersion);
        Assert.Equal($"v{projectVersion}", runtime.DisplayVersion);
        Assert.Equal("1.0.0.0", runtime.AssemblyVersion);
        Assert.True(runtime.SourceRevision is null or { Length: <= 12 });
    }

    [Fact]
    public void ReleasePipelineAndUpdateCenterUseTheSameVersionSource()
    {
        var releaseScript = File.ReadAllText(Path.Combine(Root, "build-release.ps1"));
        var systemView = File.ReadAllText(Path.Combine(Root, "Views", "AdminSystem", "Index.cshtml"));
        var layout = File.ReadAllText(Path.Combine(Root, "Views", "Shared", "_Layout.cshtml"));

        Assert.Contains("$projectXml.Project.PropertyGroup", releaseScript);
        Assert.Contains("SplitBill-v$appVersion-Server-$stamp", releaseScript);
        Assert.Contains("VERSION.txt", releaseScript);
        Assert.Contains("RELEASE_VERSION=v$appVersion", releaseScript);
        Assert.Contains("AppVersion.Current.DisplayVersion", systemView);
        Assert.Contains("AppVersion.Current.SourceRevision", systemView);
        Assert.Contains("sidebar-version", layout);
        Assert.Contains("mobile-app-version", layout);
    }
}
