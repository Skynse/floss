using System;
using Avalonia.Controls;
using Floss.App.Brushes;
using Floss.App.Input;

using Floss.App.Features;
using Floss.App.Features.Session;

namespace Floss.App.Features.Dock.Panels;

public sealed class NodeGraphDockPanel : ContentControl
{
    private readonly PanelSession _ps;
    private NodeGraphEditorPanel? _nodeGraphEditor;
    private string? _nodeGraphLoadedKey;
    private bool _nodeGraphCommitInProgress;

    public NodeGraphDockPanel(IFeatureSession session)
    {
        _ps = new PanelSession(session);
        EnsureNodeGraphEditor();
        Content = _nodeGraphEditor;
    }

    public NodeGraphEditorPanel? Editor => _nodeGraphEditor;

    public void OnVisibilityChanged(bool visible) => HandleNodeGraphDockVisibility(visible);

    public void InvalidateState()
    {
        _nodeGraphLoadedKey = null;
        SyncToActiveBrush(force: true);
    }

    public void SyncToActiveBrush(bool force = false) => SyncNodeGraphDockToActiveBrush(force);

    public void RefreshImageOptions() => RefreshNodeGraphImageOptions();

    public void WireKeyboardSurface()
    {
        if (_nodeGraphEditor == null)
            return;

        KeyboardSurface.Wire(_nodeGraphEditor, _ps.Shell.KeyboardInputScope, KeyboardInputRegion.NodeGraph);
        KeyboardSurface.Wire(_nodeGraphEditor.GraphView, _ps.Shell.KeyboardInputScope, KeyboardInputRegion.NodeGraph);
    }

    public void OpenEditor()
    {
        SetNodeGraphDockVisible(!_ps.DockLayout.IsDockerVisible("node-graph"));
    }

    private Control BuildNodeGraphDockerContent()
    {
        EnsureNodeGraphEditor();
        WireKeyboardSurface();
        var editor = _nodeGraphEditor!;
        if (editor.Parent is Panel parent)
            parent.Children.Remove(editor);
        return editor;
    }

    private void EnsureNodeGraphEditor()
    {
        if (_nodeGraphEditor != null)
            return;

        _nodeGraphEditor = new NodeGraphEditorPanel(
            BrushTipNodeGraph.SimpleCircle(),
            CommitNodeGraphFromDock,
            _ps.Brush.SaveNodeGraphAsNewBrushPreset,
            docked: true,
            onClose: () => _ps.DockLayout.ToggleDockerVisibility("node-graph"));
        WireKeyboardSurface();
    }

    private void HandleNodeGraphDockVisibility(bool visible)
    {
        if (visible)
            ReloadNodeGraphDock(force: true);
        else
        {
            _nodeGraphLoadedKey = null;
            _ps.Shell.KeyboardInputScope.Activate(KeyboardInputRegion.Canvas);
        }
    }

    private void SetNodeGraphDockVisible(bool visible, bool reload = true)
    {
        var layout = _ps.Config.WorkspaceLayout;
        if (visible)
            layout.HiddenPanelIds.Remove("node-graph");
        else
            layout.HiddenPanelIds.Add("node-graph");

        _ps.DockLayout.RebuildDockers();
        _ps.DockLayout.SyncBottomDockVisibility();

        if (visible && reload)
            ReloadNodeGraphDock(force: true);
        else if (!visible)
            HandleNodeGraphDockVisibility(false);

        _ps.DockLayout.PersistWorkspaceLayout();
    }

    private string BuildNodeGraphBrushKey()
    {
        var assetId = _ps.Brush.ActiveBrushAsset?.Id ?? "";
        var presetId = _ps.Tools.ActiveToolGroup?.ActivePreset?.Id ?? "";
        return $"{assetId}|{presetId}";
    }

    private void SyncNodeGraphDockToActiveBrush(bool force = false)
    {
        if (!_ps.DockLayout.IsDockerVisible("node-graph") || _nodeGraphEditor == null || _nodeGraphCommitInProgress)
            return;

        var key = BuildNodeGraphBrushKey();
        if (!force && key == _nodeGraphLoadedKey)
            return;

        ReloadNodeGraphDock(force: true);
    }

    private void ReloadNodeGraphDock(bool force = false)
    {
        if (_nodeGraphEditor == null)
            return;

        var key = BuildNodeGraphBrushKey();
        if (!force && key == _nodeGraphLoadedKey)
            return;

        _nodeGraphLoadedKey = key;
        var preset = _ps.Brush.ActivePreset ?? _ps.Canvas.Brush;
        var tipsList = BrushMaterialTips.NormalizeLibrary(BrushMaterialTips.ForPreset(preset));
        var graph = BrushMaterialTips.BindGraphToLibrary(_ps.Brush.GraphForBrushTip(preset.Tip), tipsList);
        _nodeGraphEditor.SetImageSamplerOptions(tipsList);
        _nodeGraphEditor.LoadGraph(graph, preset.Name);
    }

    private void RefreshNodeGraphImageOptions()
    {
        if (!_ps.DockLayout.IsDockerVisible("node-graph") || _nodeGraphEditor == null)
            return;
        var preset = _ps.Brush.ActivePreset ?? _ps.Canvas.Brush;
        _nodeGraphEditor.SetImageSamplerOptions(BrushMaterialTips.ForPreset(preset));
    }

    private void CommitNodeGraphFromDock(BrushTipNodeGraph graph)
    {
        if (graph.Validate().Count > 0)
            return;

        _nodeGraphCommitInProgress = true;
        try
        {
            var preset = _ps.Brush.ActivePreset ?? _ps.Canvas.Brush;
            var tips = BrushMaterialTips.NormalizeLibrary(BrushMaterialTips.ForPreset(preset));
            var clone = BrushMaterialTips.BindGraphToLibrary(graph.DeepClone(), tips);
            clone.BuiltInShape = null;
            var tip = new NodeBrushTip(clone);
            tip.BindMaterialTips(tips);
            _ps.Brush.UpdateCurrentBrush(p => p with { Tip = tip, Tips = tips });
        }
        finally
        {
            _nodeGraphCommitInProgress = false;
            _nodeGraphLoadedKey = BuildNodeGraphBrushKey();
        }
    }
}
