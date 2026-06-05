using System;
using System.IO;
using System.Text.Json;
using Avalonia.Platform;

namespace Floss.App.Docking;

/// <summary>
/// Shipped workspace layouts under <c>Assets/workspace-*.json</c> (Avalonia resources).
/// </summary>
public static class BundledWorkspaceLayouts
{
    public const string DefaultPresetName = "default";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static WorkspaceLayout? TryLoad(string fileName)
    {
        try
        {
            var uri = new Uri($"avares://Floss/Assets/{fileName}");
            using var stream = AssetLoader.Open(uri);
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<WorkspaceLayout>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Floss] Could not load bundled workspace '{fileName}': {ex.Message}");
            return null;
        }
    }
}
