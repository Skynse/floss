using System;
using Avalonia.Controls;
using Floss.App.Brushes;
using Floss.App.Input;

namespace Floss.App;

public partial class MainWindow
{
    private NodeGraphEditorPanel? _nodeGraphEditor;
    private string? _nodeGraphLoadedKey;
    private bool _nodeGraphCommitInProgress;

    private Control BuildNodeGraphDockerContent()
    {
        EnsureNodeGraphEditor();
        WireNodeGraphKeyboardSurface();
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
            SaveNodeGraphAsNewBrushPreset,
            docked: true,
            onClose: () => ToggleDockerVisibility("node-graph"));
        WireNodeGraphKeyboardSurface();
    }

    private void SetNodeGraphDockVisible(bool visible, bool reload = true)
    {
        var layout = App.Config.WorkspaceLayout;
        if (visible)
            layout.HiddenPanelIds.Remove("node-graph");
        else
            layout.HiddenPanelIds.Add("node-graph");

        RebuildDockers();
        SyncBottomDockVisibility();

        if (visible && reload)
            ReloadNodeGraphDock(force: true);
        else if (!visible)
        {
            _nodeGraphLoadedKey = null;
            _keyboardInputScope.Activate(KeyboardInputRegion.Canvas);
        }

        PersistWorkspaceLayout();
    }

    private string BuildNodeGraphBrushKey()
    {
        var assetId = _activeBrushAsset?.Id ?? "";
        var presetId = _activeToolGroup?.ActivePreset?.Id ?? "";
        return $"{assetId}|{presetId}";
    }

    private void InvalidateNodeGraphDockState()
    {
        _nodeGraphLoadedKey = null;
        SyncNodeGraphDockToActiveBrush(force: true);
    }

    private void SyncNodeGraphDockToActiveBrush(bool force = false)
    {
        if (!IsDockerVisible("node-graph") || _nodeGraphEditor == null || _nodeGraphCommitInProgress)
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
        _activePreset ??= _canvas.Brush;
        var tipsList = BrushMaterialTips.NormalizeLibrary(BrushMaterialTips.ForPreset(_activePreset));
        var graph = BrushMaterialTips.BindGraphToLibrary(GraphForBrushTip(_activePreset.Tip), tipsList);
        _nodeGraphEditor.SetImageSamplerOptions(tipsList);
        _nodeGraphEditor.LoadGraph(graph, _activePreset.Name);
    }

    private void RefreshNodeGraphImageOptions()
    {
        if (!IsDockerVisible("node-graph") || _nodeGraphEditor == null)
            return;
        _activePreset ??= _canvas.Brush;
        _nodeGraphEditor.SetImageSamplerOptions(BrushMaterialTips.ForPreset(_activePreset));
    }

    private void CommitNodeGraphFromDock(BrushTipNodeGraph graph)
    {
        if (graph.Validate().Count > 0)
            return;

        _nodeGraphCommitInProgress = true;
        try
        {
            var tips = BrushMaterialTips.NormalizeLibrary(BrushMaterialTips.ForPreset(_activePreset ?? _canvas.Brush));
            var clone = BrushMaterialTips.BindGraphToLibrary(graph.DeepClone(), tips);
            clone.BuiltInShape = null;
            var tip = new NodeBrushTip(clone);
            tip.BindMaterialTips(tips);
            UpdateCurrentBrush(p => p with { Tip = tip, Tips = tips });
        }
        finally
        {
            _nodeGraphCommitInProgress = false;
            _nodeGraphLoadedKey = BuildNodeGraphBrushKey();
        }
    }

    public void OpenBrushTipGraphEditor()
    {
        SetNodeGraphDockVisible(!IsDockerVisible("node-graph"));
    }
}
