using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Floss.App.Config;
using Floss.App.Features;

namespace Floss.App.Features.Plugins;

/// <summary>Loads <see cref="IFeatureModule"/> implementations from <see cref="AppPaths.PluginsDirectory"/>.</summary>
public static class FeaturePluginLoader
{
    private static readonly List<PluginLoadContext> _loadContexts = [];

    public static IReadOnlyList<IFeatureModule> LoadAll()
        => LoadResults().SelectMany(static r => r.Modules).ToArray();

    public static IReadOnlyList<PluginLoadResult> LoadResults()
    {
        if (IsTestHost())
            return [];

        var directory = AppPaths.PluginsDirectory;
        if (!Directory.Exists(directory))
        {
            Trace.WriteLine($"[Floss] Plugins directory not found: {directory}");
            return [];
        }

        var results = new List<PluginLoadResult>();
        foreach (var descriptor in PluginCatalog.Discover())
        {
            try
            {
                results.Add(LoadFrom(descriptor));
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Floss] Plugin load failed ({descriptor.DllPath}): {ex}");
                results.Add(new PluginLoadResult
                {
                    DllPath = descriptor.DllPath,
                    Manifest = descriptor.Manifest,
                    Modules = [],
                    SkipReason = ex.Message,
                });
            }
        }

        return results;
    }

    private static PluginLoadResult LoadFrom(PluginDescriptor descriptor)
    {
        var pluginPath = descriptor.DllPath;
        var manifest = descriptor.Manifest;
        var displayName = descriptor.Name;
        var id = descriptor.Id;

        if (descriptor.SkipReason is { } skip)
        {
            Trace.WriteLine($"[Floss] Plugin skipped ({displayName}): {skip}");
            return new PluginLoadResult
            {
                DllPath = pluginPath,
                Manifest = manifest,
                Modules = [],
                SkipReason = skip,
            };
        }

        var modules = LoadModulesFrom(pluginPath).ToArray();
        if (modules.Length == 0)
        {
            Trace.WriteLine($"[Floss] Plugin DLL has no IFeatureModule types: {pluginPath}");
            return new PluginLoadResult
            {
                DllPath = pluginPath,
                Manifest = manifest,
                Modules = [],
                SkipReason = "no IFeatureModule types",
            };
        }

        Trace.WriteLine($"[Floss] Loaded plugin {id} ({modules.Length} module(s)) from {pluginPath}");
        return new PluginLoadResult
        {
            DllPath = pluginPath,
            Manifest = manifest,
            Modules = modules,
        };
    }

    /// <summary>Release collectible plugin load contexts (tests).</summary>
    public static void ResetForTests()
    {
        _loadContexts.Clear();
    }

    private static IEnumerable<IFeatureModule> LoadModulesFrom(string pluginPath)
    {
        var context = new PluginLoadContext(pluginPath);
        _loadContexts.Add(context);
        var assembly = context.LoadFromAssemblyPath(pluginPath);

        foreach (var type in assembly.GetExportedTypes())
        {
            if (type.IsAbstract || type.IsInterface || !typeof(IFeatureModule).IsAssignableFrom(type))
                continue;

            if (Activator.CreateInstance(type) is not IFeatureModule module)
                continue;

            yield return module;
        }
    }

    private static bool IsTestHost()
        => AppDomain.CurrentDomain.GetAssemblies()
            .Any(a => string.Equals(a.GetName().Name, "Floss.App.Tests", StringComparison.Ordinal));
}
