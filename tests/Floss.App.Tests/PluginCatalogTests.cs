using System.IO;
using Floss.App.Config;
using Floss.App.Features.Plugins;
using Xunit;

namespace Floss.App.Tests;

public class PluginCatalogTests
{
    [Fact]
    public void IsEnabled_DefaultsTrueWhenMissing()
    {
        var cfg = new AppConfig();
        Assert.True(PluginCatalog.IsEnabled("floss.anime-mask"));
    }

    [Fact]
    public void SetEnabled_PersistsInConfig()
    {
        var original = App.Config.PluginEnabled.TryGetValue("test.plugin", out _);
        try
        {
            PluginCatalog.SetEnabled("test.plugin", false);
            Assert.False(App.Config.PluginEnabled["test.plugin"]);
            Assert.False(PluginCatalog.IsEnabled("test.plugin"));
        }
        finally
        {
            if (!original)
                App.Config.PluginEnabled.Remove("test.plugin");
            else
                App.Config.PluginEnabled["test.plugin"] = true;
        }
    }

    [Fact]
    public void ResolveId_PrefersManifestId()
    {
        var manifest = new PluginManifest { Id = "custom.id", Name = "Custom" };
        Assert.Equal("custom.id", PluginCatalog.ResolveId("/tmp/Floss.Example.dll", manifest));
        Assert.Equal("Floss.Example", PluginCatalog.ResolveId("/tmp/Floss.Example.dll", null));
    }

    [Fact]
    public void DiscoverIn_FindsPluginsInSubfolders_IgnoresDependencyDlls()
    {
        var root = Path.Combine(Path.GetTempPath(), "floss-plugin-discover-" + Guid.NewGuid().ToString("N"));
        var pluginDir = Path.Combine(root, "Floss.ExamplePlugin");
        Directory.CreateDirectory(pluginDir);

        try
        {
            File.WriteAllText(Path.Combine(pluginDir, "Floss.ExamplePlugin.dll"), string.Empty);
            File.WriteAllText(Path.Combine(pluginDir, "Floss.ExamplePlugin.floss-plugin.json"),
                """{"id":"example.plugin","name":"Example"}""");
            File.WriteAllText(Path.Combine(pluginDir, "Microsoft.ML.OnnxRuntime.dll"), string.Empty);

            var found = PluginCatalog.DiscoverIn(root);
            Assert.Single(found);
            Assert.Equal("example.plugin", found[0].Id);
            Assert.EndsWith("Floss.ExamplePlugin/Floss.ExamplePlugin.dll", found[0].DllPath.Replace('\\', '/'));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DiscoverIn_SupportsLegacyFlatRootLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "floss-plugin-flat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            File.WriteAllText(Path.Combine(root, "Floss.LegacyPlugin.dll"), string.Empty);
            File.WriteAllText(Path.Combine(root, "Floss.LegacyPlugin.floss-plugin.json"),
                """{"id":"legacy.plugin","name":"Legacy"}""");

            var found = PluginCatalog.DiscoverIn(root);
            Assert.Single(found);
            Assert.Equal("legacy.plugin", found[0].Id);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
