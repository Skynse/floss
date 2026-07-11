using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Floss.App.Config;

namespace Floss.App.Features.Plugins;

/// <summary>Discovers plugin DLLs and tracks per-plugin enablement in <see cref="AppConfig.PluginEnabled"/>.</summary>
public static class PluginCatalog
{
    public static bool IsEnabled(string pluginId)
    {
        if (string.IsNullOrEmpty(pluginId))
            return false;

        if (App.Config.PluginEnabled.TryGetValue(pluginId, out var enabled))
            return enabled;

        return true;
    }

    public static void SetEnabled(string pluginId, bool enabled)
    {
        ArgumentException.ThrowIfNullOrEmpty(pluginId);
        App.Config.PluginEnabled[pluginId] = enabled;
        App.Config.Save();
    }

    public static string ResolveId(string dllPath, PluginManifest? manifest)
        => manifest?.Id ?? Path.GetFileNameWithoutExtension(dllPath);

    public static IReadOnlyList<PluginDescriptor> Discover()
    {
        if (IsTestHost())
            return [];

        return DiscoverIn(AppPaths.PluginsDirectory);
    }

    /// <summary>
    /// Finds plugin entry DLLs: manifest sidecars at any depth, plus legacy flat <c>Floss.*Plugin.dll</c> at the plugins root.
    /// Dependency DLLs without a manifest are never treated as plugins.
    /// </summary>
    internal static IReadOnlyList<PluginDescriptor> DiscoverIn(string directory)
    {
        if (!Directory.Exists(directory))
            return [];

        var dllPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestPath in Directory.EnumerateFiles(directory, "*.floss-plugin.json", SearchOption.AllDirectories))
        {
            var pluginDir = Path.GetDirectoryName(manifestPath);
            if (string.IsNullOrEmpty(pluginDir))
                continue;

            var manifestFileName = Path.GetFileName(manifestPath);
            if (!manifestFileName.EndsWith(".floss-plugin.json", StringComparison.OrdinalIgnoreCase))
                continue;

            var manifestStem = manifestFileName[..^".floss-plugin.json".Length];
            if (manifestStem.Equals("floss-plugin", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var dll in Directory.EnumerateFiles(pluginDir, "Floss.*Plugin.dll", SearchOption.TopDirectoryOnly))
                    dllPaths.Add(dll);
                continue;
            }

            var dllPath = Path.Combine(pluginDir, manifestStem + ".dll");
            if (File.Exists(dllPath))
                dllPaths.Add(dllPath);
        }

        // Legacy flat layout: plugin DLLs dropped directly in the plugins root.
        foreach (var dll in Directory.EnumerateFiles(directory, "Floss.*Plugin.dll", SearchOption.TopDirectoryOnly))
            dllPaths.Add(dll);

        return dllPaths
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(BuildDescriptor)
            .ToArray();
    }

    private static PluginDescriptor BuildDescriptor(string dllPath)
    {
        var manifest = PluginManifest.TryLoad(dllPath);
        var id = ResolveId(dllPath, manifest);
        var name = manifest?.Name ?? Path.GetFileNameWithoutExtension(dllPath);
        var enabled = IsEnabled(id);

        string? skipReason = null;
        if (!enabled)
            skipReason = "disabled in settings";
        else if (manifest?.MinAppVersion is { } minVersion && !AppVersion.IsAtLeast(minVersion))
            skipReason = $"requires Floss {minVersion} (current {AppVersion.Current})";
        else if (manifest is { IsApiVersionCompatible: false })
            skipReason = $"requires plugin API {manifest.ApiVersion} (current {PluginManifest.CurrentApiVersion})";

        return new PluginDescriptor
        {
            Id = id,
            Name = name,
            DllPath = dllPath,
            Manifest = manifest,
            IsEnabled = enabled,
            SkipReason = skipReason,
        };
    }

    private static bool IsTestHost()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => string.Equals(a.GetName().Name, "Floss.App.Tests", StringComparison.Ordinal));

    internal static void ResetForTests() { }
}
