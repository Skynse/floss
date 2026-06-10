using System.IO;
using System.Text.Json;

namespace Floss.App.Features.Plugins;

/// <summary>Optional sidecar manifest: <c>{AssemblyName}.floss-plugin.json</c> next to the plugin DLL.</summary>
public sealed class PluginManifest
{
    public string? Id { get; init; }

    public string? Name { get; init; }

    public string? MinAppVersion { get; init; }

    public static PluginManifest? TryLoad(string pluginDllPath)
    {
        var baseName = Path.GetFileNameWithoutExtension(pluginDllPath);
        var directory = Path.GetDirectoryName(pluginDllPath);
        if (string.IsNullOrEmpty(directory))
            return null;

        var candidates = new[]
        {
            Path.Combine(directory, $"{baseName}.floss-plugin.json"),
            Path.Combine(directory, "floss-plugin.json"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
                continue;

            try
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PluginManifest>(json, JsonOptions);
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}
