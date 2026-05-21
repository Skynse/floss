using System;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Brushes;

namespace Floss.App;

using static AppColors;

public partial class MainWindow
{
    private NodeGraphEditorPanel? _nodeGraphEditor;
    private GridSplitter? _nodeGraphDockSplitter;
    private RowDefinition? _nodeGraphDockRow;
    private RowDefinition? _nodeGraphSplitterRow;
    private bool _nodeGraphDockVisible;
    private string? _nodeGraphLoadedKey;
    private bool _nodeGraphCommitInProgress;

    private void EnsureNodeGraphDock()
    {
        if (_nodeGraphEditor != null)
            return;

        _nodeGraphEditor = new NodeGraphEditorPanel(
            BrushTipNodeGraph.FromProceduralShape(BrushTipShape.Circle, 1f),
            CommitNodeGraphFromDock,
            SaveNodeGraphAsNewBrushPreset,
            docked: true,
            onClose: () => SetNodeGraphDockVisible(false));

        _nodeGraphDockSplitter = new GridSplitter
        {
            Height = 3,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            ResizeDirection = GridResizeDirection.Rows,
            Background = new SolidColorBrush(Color.Parse(Stroke)),
            IsVisible = false
        };
        _nodeGraphDockSplitter.DragCompleted += (_, _) =>
        {
            var h = _nodeGraphDockRow?.ActualHeight ?? 0;
            if (h > 0)
                App.Config.NodeGraphDockHeight = h;
            PersistWorkspaceLayout();
        };
    }

    private void AttachNodeGraphDockToCenter(Grid centerArea)
    {
        EnsureNodeGraphDock();

        centerArea.RowDefinitions.Clear();
        centerArea.RowDefinitions.Add(new RowDefinition(26, GridUnitType.Pixel));
        centerArea.RowDefinitions.Add(new RowDefinition(22, GridUnitType.Pixel));
        centerArea.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
        _nodeGraphSplitterRow = new RowDefinition(0, GridUnitType.Pixel);
        centerArea.RowDefinitions.Add(_nodeGraphSplitterRow);
        _nodeGraphDockRow = new RowDefinition(0, GridUnitType.Pixel)
        {
            MinHeight = 0,
            MaxHeight = 600
        };
        centerArea.RowDefinitions.Add(_nodeGraphDockRow);
        centerArea.RowDefinitions.Add(new RowDefinition(20, GridUnitType.Pixel));

        foreach (var child in centerArea.Children)
        {
            var row = Grid.GetRow(child);
            if (row >= 3)
                Grid.SetRow(child, row + 2);
        }

        Grid.SetRow(_nodeGraphDockSplitter!, 3);
        Grid.SetRow(_nodeGraphEditor!, 4);
        centerArea.Children.Add(_nodeGraphDockSplitter!);
        centerArea.Children.Add(_nodeGraphEditor!);

        SetNodeGraphDockVisible(App.Config.ShowNodeGraphDock, reload: false);
    }

    private void SetNodeGraphDockVisible(bool visible, bool reload = true)
    {
        EnsureNodeGraphDock();
        _nodeGraphDockVisible = visible;
        App.Config.ShowNodeGraphDock = visible;
        _nodeGraphEditor!.IsVisible = visible;
        _nodeGraphDockSplitter!.IsVisible = visible;
        if (_nodeGraphDockRow != null)
        {
            if (visible)
            {
                _nodeGraphDockRow.MinHeight = 160;
                _nodeGraphDockRow.Height = new GridLength(Math.Clamp(App.Config.NodeGraphDockHeight, 180, 600));
            }
            else
            {
                _nodeGraphDockRow.MinHeight = 0;
                _nodeGraphDockRow.Height = new GridLength(0);
            }
        }

        if (_nodeGraphSplitterRow != null)
            _nodeGraphSplitterRow.Height = visible ? new GridLength(3) : new GridLength(0);

        if (visible && reload)
            ReloadNodeGraphDock(force: true);
        else if (!visible)
            _nodeGraphLoadedKey = null;

        if (visible)
            _nodeGraphEditor.Focus();

        PersistWorkspaceLayout();
    }

    private string BuildNodeGraphBrushKey()
    {
        var assetId = _activeBrushAsset?.Id ?? "";
        var presetId = _activeToolGroup?.ActivePreset?.Id ?? "";
        _activePreset ??= _canvas.Brush;
        var tipKey = GraphForBrushTip(_activePreset.Tip).CacheKey();
        return $"{assetId}|{presetId}|{tipKey}";
    }

    private void InvalidateNodeGraphDockState()
    {
        _nodeGraphLoadedKey = null;
        SyncNodeGraphDockToActiveBrush(force: true);
    }

    private void SyncNodeGraphDockToActiveBrush(bool force = false)
    {
        if (!_nodeGraphDockVisible || _nodeGraphEditor == null || _nodeGraphCommitInProgress)
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
        var graph = GraphForBrushTip(_activePreset.Tip);
        _nodeGraphEditor.SetImageSamplerOptions(BrushMaterialTips.ForPreset(_activePreset));
        _nodeGraphEditor.LoadGraph(graph, _activePreset.Name);
    }

    private void RefreshNodeGraphImageOptions()
    {
        if (!_nodeGraphDockVisible || _nodeGraphEditor == null)
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
            var clone = graph.DeepClone();
            clone.BuiltInShape = null;
            var tip = (IBrushTip)new NodeBrushTip(clone);
            UpdateCurrentBrush(p => p with
            {
                Tip = tip,
                Tips = BrushMaterialTips.PreserveForPreset(p)
            });
        }
        finally
        {
            _nodeGraphCommitInProgress = false;
            _nodeGraphLoadedKey = BuildNodeGraphBrushKey();
        }
    }

    public void OpenBrushTipGraphEditor()
    {
        if (_nodeGraphDockVisible)
        {
            SetNodeGraphDockVisible(false);
            return;
        }

        SetNodeGraphDockVisible(true);
    }
}
