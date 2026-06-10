using Floss.App.Config;

namespace Floss.App.Document;

/// <summary>
/// Resolves undo-history timeline labels from the active tool preset.
/// </summary>
public static class HistoryLabels
{
    public static string? FromPreset(ToolPreset? preset) =>
        string.IsNullOrWhiteSpace(preset?.Name) ? null : preset.Name.Trim();

    public static string FromPresetOrDefault(ToolPreset? preset, string fallback) =>
        FromPreset(preset) ?? fallback;
}
