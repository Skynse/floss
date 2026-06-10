using System.Collections.Generic;

namespace Floss.App.Features.Plugins;

public sealed class PluginLoadResult
{
    public required string DllPath { get; init; }

    public PluginManifest? Manifest { get; init; }

    public required IReadOnlyList<IFeatureModule> Modules { get; init; }

    public string? SkipReason { get; init; }
}
