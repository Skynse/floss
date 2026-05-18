using System;
using System.IO;

namespace Floss.App;

public static class AppPaths
{
    public static string AppDirectory { get; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Floss");

    public static string BrushesDirectory { get; } =
        Path.Combine(AppDirectory, "Brushes");

    public static string BrushTipsDirectory { get; } =
        Path.Combine(AppDirectory, "Tips");

    public static string DocumentsDirectory { get; } =
        Path.Combine(AppDirectory, "Documents");

    public static string ConfigPath { get; } =
        Path.Combine(AppDirectory, "config.json");

    public static string ShortcutsConfigPath { get; } =
        Path.Combine(AppDirectory, "shortcuts.json");

    public static string BrushPaletteConfigPath { get; } =
        Path.Combine(AppDirectory, "brush-palette.json");

    public static string ToolGroupConfigPath { get; } =
        Path.Combine(AppDirectory, "tool-groups.json");

    public static string PresetsDatabasePath { get; } =
        Path.Combine(AppDirectory, "presets.flbr");

    public static string ModifierKeySettingsPath { get; } =
        Path.Combine(AppDirectory, "modifier-keys.json");

    public static string DocumentTemplatesPath { get; } =
        Path.Combine(AppDirectory, "document-templates.json");

    public static void EnsureDirectories()
    {
        Directory.CreateDirectory(AppDirectory);
        Directory.CreateDirectory(BrushesDirectory);
        Directory.CreateDirectory(BrushTipsDirectory);
        Directory.CreateDirectory(DocumentsDirectory);
    }
}
