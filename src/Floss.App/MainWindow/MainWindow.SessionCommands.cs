using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Floss.App.Brushes;
using Floss.App.Features;
using Floss.App.Features.Dock.Panels;
using Floss.App.Features.Session;
using Floss.App.Input;
using Floss.App.Tools;
using Floss.App.Windows;

namespace Floss.App;

public partial class MainWindow :
    ISessionShell,
    ILayerCommands,
    IColorCommands,
    IToolSession,
    IBrushSession,
    IDockLayoutCommands
{
    void RegisterSessionCommands(FeatureServices services)
    {
        services.Register<ISessionShell>(this);
        services.Register<ILayerCommands>(this);
        services.Register<IColorCommands>(this);
        services.Register<IToolSession>(this);
        services.Register<IBrushSession>(this);
        services.Register<IDockLayoutCommands>(this);
    }

    Window ISessionShell.Owner => this;
    KeyboardInputScope ISessionShell.KeyboardInputScope => _keyboardInputScope;
    IStorageProvider ISessionShell.StorageProvider => StorageProvider;
    TextBlock ISessionShell.FooterStatusText => _footerStatusText;
    ToolPropertiesWindow? ISessionShell.ToolPropertiesWindow
    {
        get => _toolPropsWindow;
        set => _toolPropsWindow = value;
    }
    BusyScope ISessionShell.BeginBusy(string message) => BeginBusy(message);
    Task ISessionShell.ShowMessageAsync(string title, string message) => MessageDialog.ShowAsync(this, title, message);
    void ISessionShell.UpdateStatus() => UpdateStatus();

    void ILayerCommands.RefreshLayerProperties() => RefreshLayerProperties();
    void ILayerCommands.ShowPaperColorPicker() => ShowPaperColorPicker();
    Task ILayerCommands.ApplyBlurFilter() => ApplyBlurFilter();
    Task ILayerCommands.ApplySharpenFilter() => ApplySharpenFilter();
    Task ILayerCommands.ApplyNoiseFilter() => ApplyNoiseFilter();
    Task ILayerCommands.ApplyChromaticAberrationFilter() => ApplyChromaticAberrationFilter();
    Task ILayerCommands.ApplyRemoveDustFilter() => ApplyRemoveDustFilter();
    Task ILayerCommands.ApplyLevelsFilter() => ApplyLevelsFilter();
    Task ILayerCommands.ApplyColorCurvesFilter() => ApplyColorCurvesFilter();
    void ILayerCommands.ApplyInvertFilter() => ApplyInvertFilter();
    void ILayerCommands.ApplyDesaturateFilter() => ApplyDesaturateFilter();
    Task ILayerCommands.ApplyBrightnessContrastFilter() => ApplyBrightnessContrastFilter();
    Task ILayerCommands.ApplyExposureGammaFilter() => ApplyExposureGammaFilter();
    Task ILayerCommands.ApplyHueSaturationFilter() => ApplyHueSaturationFilter();
    Task ILayerCommands.ApplySepiaFilter() => ApplySepiaFilter();
    Task ILayerCommands.ApplyThresholdFilter() => ApplyThresholdFilter();
    Task ILayerCommands.ApplyPosterizeFilter() => ApplyPosterizeFilter();
    Task ILayerCommands.ApplyPixelateFilter() => ApplyPixelateFilter();
    Task ILayerCommands.ApplyVignetteFilter() => ApplyVignetteFilter();
    Task ILayerCommands.ApplyBloomFilter() => ApplyBloomFilter();
    Task ILayerCommands.ApplyMotionBlurFilter() => ApplyMotionBlurFilter();
    Task ILayerCommands.ApplyEmbossFilter() => ApplyEmbossFilter();
    Task ILayerCommands.ApplyEdgeDetectFilter() => ApplyEdgeDetectFilter();

    ReadOnlySpan<Color> IColorCommands.Swatches => _swatches.AsSpan();
    void IColorCommands.SetColor(Color color, bool syncPicker) => SetColor(color, syncPicker);
    void IColorCommands.SyncPickerFromColor(Color color) => SyncPickerFromColor(color);
    void IColorCommands.RefreshColorSliders() => RefreshColorSliders();
    void IColorCommands.CycleColor() => CycleColor();

    ToolGroup? IToolSession.ActiveToolGroup
    {
        get => _activeToolGroup;
        set => _activeToolGroup = value;
    }
    ToolGroup? IToolSession.RecordingToolGroup
    {
        get => _recordingToolGroup;
        set => _recordingToolGroup = value;
    }
    Button? IToolSession.RecordingToolGroupButton
    {
        get => _recordingToolGroupButton;
        set => _recordingToolGroupButton = value;
    }
    IReadOnlyList<BrushAsset> IToolSession.BrushAssets => _brushAssets;
    void IToolSession.ActivatePreset(ToolGroup group, ToolPreset preset) => ActivatePreset(group, preset);
    void IToolSession.CaptureBrushToPresetIfChanged(ToolPreset preset) => CaptureBrushToPresetIfChanged(preset);
    void IToolSession.EnableCategoryPromoteDrop(Control target, ToolGroup? targetGroup)
        => EnableCategoryPromoteDrop(target, targetGroup);
    void IToolSession.RebuildToolRail() => BuildToolRail();
    ITool IToolSession.ToolForPreset(ToolPreset preset) => ToolForPreset(preset);
    void IToolSession.InvalidatePresetToolCache(string? presetId) => InvalidatePresetToolCache(presetId);

    BrushPreset? IBrushSession.ActivePreset
    {
        get => _activePreset;
        set => _activePreset = value;
    }
    BrushAsset? IBrushSession.ActiveBrushAsset
    {
        get => _activeBrushAsset;
        set => _activeBrushAsset = value;
    }
    string? IBrushSession.SelectedCategory
    {
        get => _selectedCategory;
        set => _selectedCategory = value;
    }
    IReadOnlyList<BrushAsset> IBrushSession.BrushAssets
    {
        get => _brushAssets;
        set => _brushAssets = value;
    }
    void IBrushSession.UpdateCurrentBrush(Func<BrushPreset, BrushPreset> update) => UpdateCurrentBrush(update);
    void IBrushSession.RefreshToolProperties() => RefreshToolProperties();
    void IBrushSession.InvalidateNodeGraphDockState() => InvalidateNodeGraphDockState();
    void IBrushSession.SyncNodeGraphDockToActiveBrush(bool force) => SyncNodeGraphDockToActiveBrush(force);
    void IBrushSession.OpenBrushTipGraphEditor() => OpenBrushTipGraphEditor();
    void IBrushSession.SaveNodeGraphAsNewBrushPreset(BrushTipNodeGraph graph, string name)
        => GetBrushPanel().SaveNodeGraphAsNewBrushPreset(graph, name);
    BrushTipNodeGraph IBrushSession.GraphForBrushTip(IBrushTip tip) => BrushDockPanel.GraphForBrushTip(tip);
    void IBrushSession.SyncBrushSizeLimits() => SyncBrushSizeLimits();
    void IBrushSession.RefreshNodeGraphImageOptions() => GetNodeGraphPanel().RefreshImageOptions();

    bool IDockLayoutCommands.IsDockerVisible(string id) => IsDockerVisible(id);
    void IDockLayoutCommands.ToggleDockerVisibility(string id) => ToggleDockerVisibility(id);
    void IDockLayoutCommands.RebuildDockers() => RebuildDockers();
    void IDockLayoutCommands.SyncBottomDockVisibility() => SyncBottomDockVisibility();
    void IDockLayoutCommands.PersistWorkspaceLayout() => PersistWorkspaceLayout();
}
