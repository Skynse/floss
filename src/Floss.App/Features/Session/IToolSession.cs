using System.Collections.Generic;
using Avalonia.Controls;
using Floss.App.Brushes;
using Floss.App.Tools;

namespace Floss.App.Features.Session;

/// <summary>Tool groups, preset activation, and tool-rail orchestration.</summary>
public interface IToolSession
{
    ToolGroup? ActiveToolGroup { get; set; }

    ToolGroup? RecordingToolGroup { get; set; }

    Button? RecordingToolGroupButton { get; set; }

    IReadOnlyList<BrushAsset> BrushAssets { get; }

    void ActivatePreset(ToolGroup group, ToolPreset preset);

    void CaptureBrushToPresetIfChanged(ToolPreset preset);

    void EnableCategoryPromoteDrop(Control target, ToolGroup? targetGroup);

    void RebuildToolRail();

    ITool ToolForPreset(ToolPreset preset);

    void InvalidatePresetToolCache(string? presetId = null);
}
