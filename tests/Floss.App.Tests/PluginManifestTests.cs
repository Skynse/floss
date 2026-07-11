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

    [Fact]
    public void TryLoad_ReadsApiVersion()
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
                  "apiVersion": "1.0",
                  "version": "2.0.0",
                  "author": "Tester",
                  "description": "Test plugin"
                }
                """);

            var manifest = PluginManifest.TryLoad(dll);
            Assert.NotNull(manifest);
            Assert.Equal("1.0", manifest!.ApiVersion);
            Assert.Equal("2.0.0", manifest.Version);
            Assert.Equal("Tester", manifest.Author);
            Assert.True(manifest.IsApiVersionCompatible);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void IsApiVersionCompatible_MissingVersionIsTrue()
    {
        var manifest = new PluginManifest();
        Assert.True(manifest.IsApiVersionCompatible);
    }

    [Fact]
    public void IsApiVersionCompatible_MismatchedVersionIsFalse()
    {
        var manifest = new PluginManifest { ApiVersion = "99.0" };
        Assert.False(manifest.IsApiVersionCompatible);
    }
}
