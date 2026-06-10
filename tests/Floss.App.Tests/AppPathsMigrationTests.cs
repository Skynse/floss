using Floss.App.Config;
using Xunit;

namespace Floss.App.Tests;

public class AppPathsMigrationTests
{
    [Fact]
    public void OnLinux_ConfigAndData_UseXdgDirectories()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.Contains("/.config/Floss", AppPaths.ConfigDirectory.Replace('\\', '/'));
        Assert.Contains("/.local/share/Floss", AppPaths.DataDirectory.Replace('\\', '/'));
        Assert.NotEqual(
            Path.GetFullPath(AppPaths.ConfigDirectory),
            Path.GetFullPath(AppPaths.DataDirectory));
    }

    [Fact]
    public void SettingsLiveUnderConfig_DataLiveUnderData()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Assert.Contains(AppPaths.ConfigDirectory, AppPaths.ConfigPath);
        Assert.Contains(AppPaths.ConfigDirectory, AppPaths.ShortcutsConfigPath);
        Assert.Contains(AppPaths.DataDirectory, AppPaths.PluginsDirectory);
        Assert.Contains(AppPaths.DataDirectory, AppPaths.PresetsDatabasePath);
        Assert.Contains(AppPaths.DataDirectory, AppPaths.ModelsDirectory);
    }
}
