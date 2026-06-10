namespace Floss.App.Features.Plugins;

/// <summary>Discovered plugin DLL on disk (loaded or not).</summary>
public sealed class PluginDescriptor
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string DllPath { get; init; }

    public PluginManifest? Manifest { get; init; }

    public bool IsEnabled { get; init; }

    public string? SkipReason { get; init; }
}
