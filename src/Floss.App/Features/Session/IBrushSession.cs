using System;
using System.Collections.Generic;
using Floss.App.Brushes;

namespace Floss.App.Features.Session;

/// <summary>Active brush state and brush-library operations.</summary>
public interface IBrushSession
{
    BrushPreset? ActivePreset { get; set; }

    BrushAsset? ActiveBrushAsset { get; set; }

    string? SelectedCategory { get; set; }

    IReadOnlyList<BrushAsset> BrushAssets { get; set; }

    void UpdateCurrentBrush(Func<BrushPreset, BrushPreset> update);

    void RefreshToolProperties();

    void InvalidateNodeGraphDockState();

    void SyncNodeGraphDockToActiveBrush(bool force = false);

    void OpenBrushTipGraphEditor();

    void SaveNodeGraphAsNewBrushPreset(BrushTipNodeGraph graph, string name);

    BrushTipNodeGraph GraphForBrushTip(IBrushTip tip);

    void SyncBrushSizeLimits();

    void RefreshNodeGraphImageOptions();
}
