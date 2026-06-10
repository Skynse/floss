using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Floss.App.Brushes;
using Floss.App.Features;
using Floss.App.Features.Dock.Panels;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App;

public partial class MainWindow
{
    private NodeGraphDockPanel GetNodeGraphPanel() => EnsureFeatureServices().Get<NodeGraphDockPanel>();
    private LayerPropertiesDockPanel GetLayerPropertiesPanel() => EnsureFeatureServices().Get<LayerPropertiesDockPanel>();
    private ColorSlidersDockPanel GetColorSlidersPanel() => EnsureFeatureServices().Get<ColorSlidersDockPanel>();
    private ColorDockPanel GetColorPanel() => EnsureFeatureServices().Get<ColorDockPanel>();
    private ToolsDockPanel GetToolsPanel() => EnsureFeatureServices().Get<ToolsDockPanel>();
    private ToolPropertiesDockPanel GetToolPropertiesPanel() => EnsureFeatureServices().Get<ToolPropertiesDockPanel>();
    private BrushDockPanel GetBrushPanel() => EnsureFeatureServices().Get<BrushDockPanel>();
    private LayersDockPanel GetLayersPanel() => EnsureFeatureServices().Get<LayersDockPanel>();

    private void BuildLayerList() => GetLayersPanel().Rebuild();

    private void ScheduleLayerListRebuild() => GetLayersPanel().ScheduleRebuild();

    private void RefreshLayerProperties() => GetLayerPropertiesPanel().Refresh();

    private void RefreshColorSliders() => GetColorSlidersPanel().Refresh();

    private void RefreshToolProperties() => GetToolPropertiesPanel().Refresh();

    private void BuildToolRail() => GetToolsPanel().RebuildToolRail();

    internal void CommitToolGroupShortcut(Input.KeyBinding kb)
        => GetToolsPanel().CommitToolGroupShortcut(kb);

    internal void CancelToolGroupShortcutRecording()
        => GetToolsPanel().CancelToolGroupShortcutRecording();

    internal void RefreshGroupPresets() => GetBrushPanel().RefreshGroupPresets();

    internal void EnableCategoryPromoteDrop(Avalonia.Controls.Control target, ToolGroup? targetGroup)
        => GetBrushPanel().EnableCategoryPromoteDrop(target, targetGroup);

    internal void ApplyBrushSettings(BrushPreset preset, bool syncSliders)
        => GetBrushPanel().ApplyBrushSettings(preset, syncSliders);

    internal void CaptureActiveBrushToPresetIfChanged()
        => GetBrushPanel().CaptureActiveBrushToPresetIfChanged();

    internal void CaptureBrushToPresetIfChanged(ToolPreset preset)
        => GetBrushPanel().CaptureBrushToPresetIfChanged(preset);

    private void InvalidateNodeGraphDockState() => GetNodeGraphPanel().InvalidateState();

    private void SyncNodeGraphDockToActiveBrush(bool force = false)
        => GetNodeGraphPanel().SyncToActiveBrush(force);

    public void OpenBrushTipGraphEditor() => GetNodeGraphPanel().OpenEditor();

    private void ToggleActiveLayerColor() => GetLayerPropertiesPanel().ToggleActiveLayerColor();

    private void CycleColor() => GetColorPanel().CycleColor();

    private Avalonia.Controls.Control GetToolPropertiesContent()
        => GetToolPropertiesPanel().GetToolPropertiesContent();

    private void ScheduleToolGroupsSave() => GetBrushPanel().ScheduleToolGroupsSave();

    private void SaveActiveToolSelection() => GetBrushPanel().SaveActiveToolSelection();

    private void ScheduleBrushPresetAutosave() => GetBrushPanel().ScheduleBrushPresetAutosave();

    private void ExpandAndScrollToLayers(IReadOnlyList<int> foundIndices)
        => GetLayersPanel().ExpandAndScrollToLayers(foundIndices);

    private void PruneLayerSelection() => GetLayersPanel().PruneLayerSelection();

    private void UpdateLayerRow(int index) => GetLayersPanel().UpdateLayerRow(index);

    private void InvalidateLayerListCache() => GetLayersPanel().InvalidateLayerListCache();

    private void LoadBrushAssets() => GetBrushPanel().LoadBrushAssets();

    private void SelectInitialTool() => GetBrushPanel().SelectInitialTool();

    private void PreviewCurrentBrush(Func<BrushPreset, BrushPreset> update)
        => GetBrushPanel().PreviewCurrentBrush(update);

    private void UpdateCurrentBrush(Func<BrushPreset, BrushPreset> update)
        => GetBrushPanel().UpdateCurrentBrush(update);

    private static string? ResolveStartupCategory(ToolGroup group, string? preferredCategory, ToolPreset preset)
        => BrushDockPanel.ResolveStartupCategory(group, preferredCategory, preset);

    private void FlushToolGroupsSave() => GetBrushPanel().FlushToolGroupsSave();

    private void FlushBrushPresetAutosave() => GetBrushPanel().FlushBrushPresetAutosave();

    private DockPanelSync DockSync => EnsureFeatureServices().Get<DockPanelSync>();

    private void DeleteSelectedLayers() => GetLayersPanel().DeleteSelectedLayers();

    private void SaveActiveBrush() => GetBrushPanel().SaveActiveBrush();

    private void DuplicateActiveBrush() => GetBrushPanel().DuplicateActiveBrush();

    private Task ImportBrushTipPngAsync() => GetBrushPanel().ImportBrushTipPngAsync();

    private Task ImportAbrAsync() => GetBrushPanel().ImportAbrAsync();

    private IReadOnlyCollection<int> SelectedLayerIndices => GetLayersPanel().SelectedLayerIndices;
}
