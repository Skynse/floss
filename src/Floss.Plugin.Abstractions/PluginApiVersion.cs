namespace Floss.App.Features;

/// <summary>
/// Plugin API version contract. Plugins declare their required API version
/// in their manifest. The host checks compatibility at load time.
/// </summary>
public static class PluginApiVersion
{
    /// <summary>Current API version. Increment on breaking changes.</summary>
    public const string Current = "1.0";

    /// <summary>Check if a plugin's declared API version is compatible with this host.</summary>
    public static bool IsCompatible(string? pluginApiVersion)
    {
        if (string.IsNullOrEmpty(pluginApiVersion))
            return true; // Pre-versioning plugins are allowed (with a warning)

        return pluginApiVersion == Current;
    }
}
