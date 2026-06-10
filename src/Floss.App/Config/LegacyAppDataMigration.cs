using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Floss.App.Config;

/// <summary>
/// Normalizes legacy monolithic <c>~/.config/Floss</c> or <c>~/.local/share/Floss</c> trees
/// into XDG config + data directories.
/// </summary>
internal static class LegacyAppDataMigration
{
    private const string MarkerFileName = ".xdg-layout-v1";

    private static readonly HashSet<string> ConfigFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "config.json",
        "shortcuts.json",
        "tool-groups.json",
        "modifier-keys.json",
        "pen-pressure.json",
        "document-templates.json",
        "brush-palette.json",
    };

    private static readonly HashSet<string> DataDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Brushes",
        "Tips",
        "Documents",
        "Timelapses",
        "models",
        "Plugins",
        "crash-reports",
    };

    public static void MigrateIfNeeded()
    {
        if (!OperatingSystem.IsLinux())
            return;

        Directory.CreateDirectory(AppPaths.ConfigDirectory);
        Directory.CreateDirectory(AppPaths.DataDirectory);

        var marker = Path.Combine(AppPaths.ConfigDirectory, MarkerFileName);
        if (File.Exists(marker))
            return;

        try
        {
            var legacyConfig = Path.Combine(AppPaths.ResolveLinuxConfigHome(), "Floss");
            var legacyData = Path.Combine(AppPaths.ResolveLinuxDataHome(), "Floss");

            if (Directory.Exists(legacyConfig))
                SplitMonolithicTree(legacyConfig);

            if (Directory.Exists(legacyData)
                && !string.Equals(Path.GetFullPath(legacyData), Path.GetFullPath(legacyConfig), StringComparison.Ordinal))
            {
                SplitMonolithicTree(legacyData);
            }

            File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
            Trace.WriteLine(
                $"[Floss] XDG layout: config={AppPaths.ConfigDirectory}, data={AppPaths.DataDirectory}");
            TryRemoveEmptyDirectories(legacyConfig);
        }
        catch (Exception ex)
        {
            Trace.WriteLine($"[Floss] Legacy app data migration failed: {ex}");
        }
    }

    private static void SplitMonolithicTree(string sourceRoot)
    {
        if (!Directory.Exists(sourceRoot))
            return;

        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceRoot))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.'))
                continue;

            if (Directory.Exists(entry))
            {
                if (!DataDirectoryNames.Contains(name))
                    continue;

                var destDir = Path.Combine(AppPaths.DataDirectory, name);
                Directory.CreateDirectory(destDir);
                MergeDirectory(entry, destDir, mergeExistingFiles: false);
                TryRemoveEmptyDirectories(entry);
                continue;
            }

            if (!File.Exists(entry))
                continue;

            if (ConfigFileNames.Contains(name))
            {
                MoveFile(entry, Path.Combine(AppPaths.ConfigDirectory, name));
                continue;
            }

            if (IsDataFile(name))
                MoveFile(entry, Path.Combine(AppPaths.DataDirectory, name));
        }
    }

    private static bool IsDataFile(string name) =>
        string.Equals(name, "presets.flbr", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("presets.flbr-", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "crash.log", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "render-telemetry.log", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "last-crash.json", StringComparison.OrdinalIgnoreCase)
        || string.Equals(name, "model_consent", StringComparison.OrdinalIgnoreCase);

    private static void MergeDirectory(string sourceDir, string destDir, bool mergeExistingFiles)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(sourceDir))
        {
            var name = Path.GetFileName(entry);
            if (string.IsNullOrEmpty(name))
                continue;

            var destPath = Path.Combine(destDir, name);
            if (Directory.Exists(entry))
            {
                Directory.CreateDirectory(destPath);
                MergeDirectory(entry, destPath, mergeExistingFiles);
                TryRemoveEmptyDirectories(entry);
                continue;
            }

            Directory.CreateDirectory(destDir);
            if (File.Exists(destPath) && !mergeExistingFiles)
                continue;

            MoveFile(entry, destPath);
        }
    }

    private static void MoveFile(string source, string dest)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        if (File.Exists(dest))
            File.Delete(dest);
        File.Move(source, dest);
    }

    private static void TryRemoveEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
            return;

        foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
        {
            try
            {
                if (Directory.EnumerateFileSystemEntries(dir).GetEnumerator().MoveNext())
                    continue;
                Directory.Delete(dir);
            }
            catch { /* best effort */ }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(root).GetEnumerator().MoveNext())
                Directory.Delete(root);
        }
        catch { /* best effort */ }
    }
}
