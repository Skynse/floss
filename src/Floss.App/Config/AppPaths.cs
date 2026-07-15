using System;
using System.IO;

namespace Floss.App.Config;

/// <summary>
/// XDG-style layout on Linux ():
/// <list type="bullet">
/// <item><description>Settings — <c>~/.config/Floss/</c></description></item>
/// <item><description>Application data — <c>~/.local/share/Floss/</c></description></item>
/// </list>
/// </summary>
public static class AppPaths
{
    public static string ConfigDirectory { get; } = ResolveConfigDirectory();

    public static string DataDirectory { get; } = ResolveDataDirectory();

    /// <summary>Alias for <see cref="DataDirectory"/> (resources, plugins, models).</summary>
    public static string AppDirectory => DataDirectory;

    public static string BrushesDirectory { get; } =
        Path.Combine(DataDirectory, "Brushes");

    public static string BrushTipsDirectory { get; } =
        Path.Combine(DataDirectory, "Tips");

    public static string DocumentsDirectory { get; } =
        Path.Combine(DataDirectory, "Documents");

    public static string TimelapsesDirectory { get; } =
        Path.Combine(DataDirectory, "Timelapses");

    public static string ConfigPath { get; } =
        Path.Combine(ConfigDirectory, "config.json");

    public static string ShortcutsConfigPath { get; } =
        Path.Combine(ConfigDirectory, "shortcuts.json");

    public static string ToolGroupConfigPath { get; } =
        Path.Combine(ConfigDirectory, "tool-groups.json");

    public static string PresetsDatabasePath { get; } =
        Path.Combine(DataDirectory, "presets.flbr");

    public static string ModifierKeySettingsPath { get; } =
        Path.Combine(ConfigDirectory, "modifier-keys.json");

    public static string PenPressureSettingsPath { get; } =
        Path.Combine(ConfigDirectory, "pen-pressure.json");

    public static string DocumentTemplatesPath { get; } =
        Path.Combine(ConfigDirectory, "document-templates.json");

    public static string CrashReportsDirectory { get; } =
        Path.Combine(DataDirectory, "crash-reports");

    public static string ModelsDirectory { get; } =
        Path.Combine(DataDirectory, "models");

    public static string PluginsDirectory { get; } =
        Path.Combine(DataDirectory, "Plugins");

    public static string AnimeSegModelPath { get; } =
        Path.Combine(ModelsDirectory, "isnetis.onnx");

    public static string CrashLogPath { get; } =
        Path.Combine(DataDirectory, "crash.log");

    public static string RenderTelemetryLogPath { get; } =
        Path.Combine(DataDirectory, "render-telemetry.log");

    public static string LastCrashReportPath { get; } =
        Path.Combine(DataDirectory, "last-crash.json");

    public static string ModelConsentPath { get; } =
        Path.Combine(DataDirectory, "model_consent");

    public static void EnsureDirectories()
    {
        LegacyAppDataMigration.MigrateIfNeeded();
        Directory.CreateDirectory(ConfigDirectory);
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(BrushesDirectory);
        Directory.CreateDirectory(BrushTipsDirectory);
        Directory.CreateDirectory(DocumentsDirectory);
        Directory.CreateDirectory(TimelapsesDirectory);
        Directory.CreateDirectory(ModelsDirectory);
        Directory.CreateDirectory(PluginsDirectory);
        Directory.CreateDirectory(CrashReportsDirectory);
    }

    private static string ResolveConfigDirectory()
    {
        if (OperatingSystem.IsLinux())
            return Path.Combine(ResolveLinuxConfigHome(), "Floss");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Floss");
    }

    private static string ResolveDataDirectory()
    {
        if (OperatingSystem.IsLinux())
            return Path.Combine(ResolveLinuxDataHome(), "Floss");

        if (OperatingSystem.IsWindows())
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Floss");

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Floss");
    }

    internal static string ResolveLinuxConfigHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return xdg;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".config");
    }

    internal static string ResolveLinuxDataHome()
    {
        var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (!string.IsNullOrWhiteSpace(xdg))
            return xdg;

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local",
            "share");
    }
}
