using System.Text.Json;
using System.Xml.Linq;

namespace Mahjong.Autotable.Api.Tests.Build;

public sealed class ReleaseVersionTests
{
    [Fact]
    public void ReleaseVersion_MatchesMobileAssemblyAndChangelog()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Dockerfile")))
            root = root.Parent;
        Assert.NotNull(root);

        var project = XDocument.Load(Path.Combine(root.FullName, "src", "backend", "src",
            "Mahjong.Autotable.Api", "Mahjong.Autotable.Api.csproj"));
        var versionText = Assert.Single(project.Descendants("Version")).Value;
        Assert.Equal("0.32.1", versionText);
        var version = Version.Parse(versionText);
        var assemblyVersion = typeof(Mahjong.Autotable.Api.Changsha.ChangshaGameState).Assembly.GetName().Version;
        Assert.Equal(new Version(version.Major, version.Minor, version.Build, 0), assemblyVersion);

        using var package = JsonDocument.Parse(File.ReadAllText(Path.Combine(root.FullName, "mobile", "package.json")));
        Assert.Equal(versionText, package.RootElement.GetProperty("version").GetString());
        Assert.Contains($"## [{versionText}]", File.ReadAllText(Path.Combine(root.FullName, "CHANGELOG.md")));
    }
}
