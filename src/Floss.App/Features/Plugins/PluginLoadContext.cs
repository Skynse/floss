using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace Floss.App.Features.Plugins;

internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private readonly AssemblyDependencyResolver _resolver;
    private readonly string _pluginDirectory;

    public PluginLoadContext(string pluginPath) : base(isCollectible: true)
    {
        _pluginDirectory = Path.GetDirectoryName(pluginPath) ?? pluginPath;
        _resolver = new AssemblyDependencyResolver(pluginPath);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        var name = assemblyName.Name;
        if (name is null)
            return null;

        // Never shadow the host app or UI stack from stray copies in the plugins folder.
        if (string.Equals(name, "Floss", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Avalonia", StringComparison.Ordinal))
            return null;

        try
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }
        catch (InvalidOperationException)
        {
            // Collectible context was unloaded — host should keep PluginLoadContext alive.
            return null;
        }
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        try
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            if (path != null && File.Exists(path))
                return LoadUnmanagedDllFromPath(path);
        }
        catch (InvalidOperationException)
        {
            return IntPtr.Zero;
        }

        var fallback = TryResolveNativeLibrary(unmanagedDllName);
        return fallback != null ? LoadUnmanagedDllFromPath(fallback) : IntPtr.Zero;
    }

    private string? TryResolveNativeLibrary(string unmanagedDllName)
    {
        var baseName = unmanagedDllName;
        if (baseName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
            || baseName.EndsWith(".so", StringComparison.OrdinalIgnoreCase)
            || baseName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase))
        {
            baseName = Path.GetFileNameWithoutExtension(baseName);
        }

        if (baseName.StartsWith("lib", StringComparison.Ordinal))
            baseName = baseName[3..];

        var rid = RuntimeInformation.RuntimeIdentifier;
        if (string.IsNullOrEmpty(rid))
            return null;

        var candidates = new[]
        {
            Path.Combine(_pluginDirectory, "runtimes", rid, "native", unmanagedDllName),
            Path.Combine(_pluginDirectory, "runtimes", rid, "native", $"lib{baseName}.so"),
            Path.Combine(_pluginDirectory, "runtimes", rid, "native", $"{baseName}.dll"),
            Path.Combine(_pluginDirectory, "runtimes", rid, "native", $"lib{baseName}.dylib"),
            // Legacy flat deploy (pre-runtimes/ fix)
            Path.Combine(_pluginDirectory, rid, "native", $"lib{baseName}.so"),
            Path.Combine(_pluginDirectory, rid, "native", $"{baseName}.dll"),
        };

        foreach (var candidate in candidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }
}
