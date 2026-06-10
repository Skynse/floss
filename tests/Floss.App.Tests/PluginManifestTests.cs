using Floss.App.Config;
using Floss.App.Features.Plugins;
using Xunit;

namespace Floss.App.Tests;

public class PluginManifestTests
{
    [Fact]
    public void IsAtLeast_ComparesMajorMinorBuild()
    {
        Assert.True(AppVersion.IsAtLeast("0.1.0"));
        Assert.True(AppVersion.IsAtLeast("0.0.0"));
        Assert.False(AppVersion.IsAtLeast("99.0.0"));
    }

    [Fact]
    public void TryLoad_ReadsSidecarJson()
    {
        var dir = Path.Combine(Path.GetTempPath(), "floss-plugin-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var dll = Path.Combine(dir, "Sample.Plugin.dll");
            File.WriteAllText(dll, string.Empty);
            File.WriteAllText(
                Path.Combine(dir, "Sample.Plugin.floss-plugin.json"),
                """
                {
                  "id": "sample.plugin",
                  "name": "Sample Plugin",
                  "minAppVersion": "0.1.0"
                }
                """);

            var manifest = PluginManifest.TryLoad(dll);
            Assert.NotNull(manifest);
            Assert.Equal("sample.plugin", manifest!.Id);
            Assert.Equal("Sample Plugin", manifest.Name);
            Assert.Equal("0.1.0", manifest.MinAppVersion);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
