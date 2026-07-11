using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Floss.App.Controls;
using Floss.App.Docking;

namespace Floss.App;

public partial class MainWindow
{
    private const int DockDragThreshold = 6;
    private const double DropEdgeInsetPx = 16;
    private const double TopBottomInsertReachPx = 64;
    private const double HorizontalSplitEdgeRatio = 0.28;
    private const double VerticalBandRatio = 0.14;
    private const double MinRowGapHitHeightPx = 40;
    private const double ColumnEdgeHitWidthPx = 72;
    private const double ColumnSplitHitWidthPx = 72;

    private DockDropOverlay? _dockDropOverlay;
    private string? _dockDragPanelId;
    private Point _dockDragStart;
    private Point _dockDragPointerRoot;
    private bool _dockDragActive;
    private int _dockDropColumn = -99;
    private DockDropKind _dockDropKind = DockDropKind.InsertRow;
    private int _dockDropRowIndex;
    private int _dockDropInsertColumnIndex;
    private readonly List<DockDropZone> _dockDropZones = [];
    private readonly List<Rect> _dockColumnBounds = [];
    private DropTargetState? _resolvedDropTarget;
    private DropTargetState? _stickyDropTarget;

    private enum DropZonePriority
    {
        /// <summary>Outer edge of a dock rail (far left / beside canvas).</summary>
        ColumnRail = 0,
        /// <summary>Between dock columns or panel left/right split.</summary>
        ColumnSplit = 1,
        /// <summary>Between tabs in a tab strip (insert at index).</summary>
        TabInsert = 2,
        /// <summary>Between two rows, or above-first / below-last.</summary>
        RowGap = 3,
        /// <summary>Per-row body edge band (top/bottom of body for insert above/below).</summary>
        RowBodyEdge = 4,
        TabStrip = 5,
        RowBody = 6,
    }

    private sealed record DockDropZone(
        DropZonePriority Priority,
        int ColumnIndex,
        int LayoutRowIndex,
        int InsertColumnIndex,
        DockDropKind Kind,
        Rect Bounds,
        double LinePos,
        string Label,
        bool HorizontalInsertLine,
        IReadOnlyList<string> RowPanelIds,
        int TabInsertIndex = -1,
        Rect? RowRect = null);

    private sealed record DropTargetState(
        int ColumnIndex,
        int InsertColumnIndex,
        int RowIndex,
        DockDropKind Kind,
        double LinePos,
        Rect Bounds,
        string Label,
        bool HorizontalInsertLine,
        IReadOnlyList<string>? MergeTargetPanelIds = null,
        int TabInsertIndex = -1);

    private void EnsureDockDropOverlay()
    {
        if (_dockDropOverlay != null || _rootGrid == null) return;

        _dockDropOverlay = new DockDropOverlay();
        Grid.SetColumn(_dockDropOverlay, 0);
        Grid.SetColumnSpan(_dockDropOverlay, 5);
        Grid.SetRow(_dockDropOverlay, 0);
        _dockDropOverlay.ZIndex = 5000;
        _rootGrid.Children.Add(_dockDropOverlay);
    }

    private void WireDockerHeaderDrag(Border section, string panelId)
    {
        section.Tag = panelId;
        if (section.Child is not Grid grid || grid.Children.Count == 0)
            return;
        if (grid.Children[0] is not Border header)
            return;

        header.PointerPressed += (_, e) => DockerHeaderPressed(panelId, header, e);
        header.PointerMoved += (_, e) => DockerHeaderMoved(panelId, header, e);
        header.PointerReleased += (_, e) => DockerHeaderReleased(panelId, header, e);
        header.PointerCaptureLost += (_, _) => CancelDockerDrag();
    }

    private void WireDockTabGroupDrag(DockTabGroup tabGroup)
    {
        tabGroup.TabDragStarted += OnTabDragStarted;
        tabGroup.TabDragMoved += OnTabDragMoved;
        tabGroup.TabDragEnded += OnTabDragEnded;
    }

    private Point PointerPositionInDockOverlay(PointerEventArgs e)
        => _dockDropOverlay != null
            ? e.GetPosition(_dockDropOverlay)
            : e.GetPosition(this);

    private void OnTabDragStarted(string panelId, PointerPressedEventArgs e)
    {
        BeginDockerDrag(panelId, PointerPositionInDockOverlay(e));
    }

    private void OnTabDragMoved(string panelId, PointerEventArgs e)
    {
        if (_dockDragPanelId != panelId) return;
        ContinueDockerDrag(panelId, PointerPositionInDockOverlay(e), e);
    }

    private void OnTabDragEnded(string panelId, PointerReleasedEventArgs e)
    {
        if (_dockDragPanelId != panelId) return;
        FinalizeDockerDragAt(PointerPositionInDockOverlay(e));
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void DockerHeaderPressed(string panelId, Control header, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
        BeginDockerDrag(panelId, PointerPositionInDockOverlay(e));
        e.Pointer.Capture(header);
        e.Handled = true;
    }

    private void DockerHeaderMoved(string panelId, Control header, PointerEventArgs e)
    {
        if (_dockDragPanelId != panelId) return;
        ContinueDockerDrag(panelId, PointerPositionInDockOverlay(e), e);
        e.Handled = true;
    }

    private void DockerHeaderReleased(string panelId, Control header, PointerReleasedEventArgs e)
    {
        if (_dockDragPanelId != panelId) return;
        FinalizeDockerDragAt(PointerPositionInDockOverlay(e));
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void BeginDockerDrag(string panelId, Point rootPt)
    {
        EnsureDockDropOverlay();
        _dockDragPanelId = panelId;
        _dockDragStart = rootPt;
        _dockDragPointerRoot = rootPt;
        _dockDragActive = false;
        _dockDropColumn = -99;
        _dockDropKind = DockDropKind.InsertRow;
        _dockDropRowIndex = -1;
        _resolvedDropTarget = null;
        _stickyDropTarget = null;
        _dockDropZones.Clear();
        _dockColumnBounds.Clear();
    }

    private void ContinueDockerDrag(string panelId, Point rootPt, PointerEventArgs e)
    {
        _dockDragPointerRoot = rootPt;

        if (!_dockDragActive && DragDistance(rootPt, _dockDragStart) < DockDragThreshold)
            return;

        _dockDragActive = true;

        RebuildDropZones(panelId);
        UpdateDockerDropPreview(panelId, rootPt);
        if (e != null && _dockDragActive)
            e.Handled = true;
    }

    private void FinalizeDockerDragAt(Point rootPt)
    {
        if (_dockDragPanelId == null || !_dockDragActive)
        {
            CancelDockerDrag();
            return;
        }

        // Keep the last valid target from pointer-move; release position is often off the line.
        _resolvedDropTarget = _stickyDropTarget;
        if (_resolvedDropTarget == null)
        {
            _dockDragPointerRoot = rootPt;
            RebuildDropZones(_dockDragPanelId);
            _resolvedDropTarget = ResolveDropTarget(rootPt, _dockDragPanelId);
        }

        FinishDockerDrag();
    }

    private void FinishDockerDrag()
    {
        var target = _resolvedDropTarget ?? _stickyDropTarget;
        if (_dockDragActive && _dockDragPanelId != null && target != null)
            CommitDockerDrop(_dockDragPanelId, target);

        CancelDockerDrag();
    }

    private void CancelDockerDrag()
    {
        _dockDragPanelId = null;
        _dockDragActive = false;
        _dockDropColumn = -99;
        _dockDropRowIndex = -1;
        _resolvedDropTarget = null;
        _stickyDropTarget = null;
        _dockDropZones.Clear();
        _dockColumnBounds.Clear();
        _dockDropOverlay?.Clear();
    }

    private void CommitDockerDrop(string panelId, DropTargetState target)
    {
        SaveWorkspaceLayoutFromUi();
        var layout = App.Config.WorkspaceLayout;

        DockLayoutOps.ApplyDrop(
            layout,
            panelId,
            target.ColumnIndex,
            target.Kind == DockDropKind.InsertDockColumn ? target.InsertColumnIndex : target.RowIndex,
            target.Kind,
            mergeTabRowIndex: target.RowIndex,
            mergeTargetRowPanelIds: target.MergeTargetPanelIds,
            mergeTabInsertIndex: target.TabInsertIndex);

        RebuildDockers();
        App.Config.Save();
    }

    private void RebuildDropZones(string movingId)
    {
        _dockDropZones.Clear();
        _dockColumnBounds.Clear();
        if (_dockDropOverlay == null) return;

        AddRailOuterEdgeZones();
        AddHostColumnSplitterZones(_leftDockerHostGrid, DockZone.Left, DockColumnIndices.Left);
        AddHostColumnSplitterZones(_dockerHostGrid, DockZone.Right, DockColumnIndices.Right);
        AddHostColumnSplitterZones(_bottomDockerHostGrid, DockZone.Bottom, DockColumnIndices.Bottom);
        AddCanvasEdgeColumnZones();

        var layout = App.Config.WorkspaceLayout;
        var columnEntries = layout.LeftColumns
            .Select((c, i) => (ColumnIndex: DockColumnIndices.Left(i), Column: c))
            .Concat(layout.RightColumns.Select((c, i) => (ColumnIndex: DockColumnIndices.Right(i), Column: c)))
            .Concat(layout.BottomColumns.Select((c, i) => (ColumnIndex: DockColumnIndices.Bottom(i), Column: c)))
            .ToList();

        foreach (var (columnIndex, column) in columnEntries)
        {
            var grid = ColumnGrid(columnIndex);
            if (grid == null) continue;

            var visualRows = CollectVisualRows(grid, column, columnIndex, movingId);
            var columnRect = ControlRectInOverlay(grid);

            for (var i = 0; i < visualRows.Count; i++)
            {
                var row = visualRows[i];
                var rowTitle = DockerTitle(row.PanelIds[0]);

                if (i == 0 && row.RowRect is { } firstRect)
                {
                    var stripTop = row.TabStripRect?.Top ?? firstRect.Top;
                    var reachTop = columnRect?.Top ?? stripTop - TopBottomInsertReachPx;
                    var zoneTop = Math.Min(reachTop, stripTop - TopBottomInsertReachPx);
                    var aboveH = stripTop - zoneTop;
                    if (aboveH >= 8)
                    {
                        _dockDropZones.Add(new DockDropZone(
                            DropZonePriority.RowGap,
                            columnIndex,
                            0,
                            0,
                            DockDropKind.InsertRow,
                            new Rect(firstRect.X, zoneTop, firstRect.Width, aboveH),
                            stripTop,
                            $"Insert above {rowTitle}",
                            false,
                            row.PanelIds));
                    }
                }

                if (row.TabStripRect is { } tabStrip)
                {
                    AddTabInsertZones(columnIndex, row, tabStrip, row.TabGroup);
                    _dockDropZones.Add(new DockDropZone(
                        DropZonePriority.TabStrip,
                        columnIndex,
                        row.LayoutRowIndex,
                        0,
                        DockDropKind.MergeTab,
                        tabStrip,
                        tabStrip.Top,
                        $"Tab with {rowTitle}",
                        false,
                        row.PanelIds,
                        TabInsertIndex: -1));
                }

                if (row.RowRect is { } panelRect)
                    AddPanelColumnEdgeZones(columnIndex, panelRect, row.Label);

                if (row.BodyRect is { Height: > 36 } body)
                    AddRowBodyBands(columnIndex, row, body, rowTitle);

                if (i + 1 == visualRows.Count && row.RowRect is { } lastRect)
                {
                    var zoneBottom = columnRect?.Bottom ?? lastRect.Bottom + TopBottomInsertReachPx;
                    var reachBottom = Math.Max(zoneBottom, lastRect.Bottom + TopBottomInsertReachPx);
                    var belowTop = lastRect.Bottom;
                    var belowH = reachBottom - belowTop;
                    if (belowH >= 8)
                    {
                        _dockDropZones.Add(new DockDropZone(
                            DropZonePriority.RowGap,
                            columnIndex,
                            row.LayoutRowIndex + 1,
                            0,
                            DockDropKind.InsertRow,
                            new Rect(lastRect.X, belowTop, lastRect.Width, belowH),
                            lastRect.Bottom,
                            $"Insert below {rowTitle}",
                            false,
                            row.PanelIds));
                    }
                }

                if (i <= 0) continue;

                var prev = visualRows[i - 1];
                if (prev.RowRect is not { } prevRect || row.RowRect is not { } nextRect)
                    continue;

                var gapCenterY = (prevRect.Bottom + nextRect.Top) * 0.5;
                var gapHeight = Math.Max(
                    MinRowGapHitHeightPx,
                    nextRect.Top - prevRect.Bottom + DropEdgeInsetPx * 2);
                var gapRect = new Rect(
                    Math.Min(prevRect.X, nextRect.X),
                    gapCenterY - gapHeight * 0.5,
                    Math.Max(prevRect.Width, nextRect.Width),
                    gapHeight);

                _dockDropZones.Add(new DockDropZone(
                    DropZonePriority.RowGap,
                    columnIndex,
                    row.LayoutRowIndex,
                    0,
                    DockDropKind.InsertRow,
                    gapRect,
                    nextRect.Top,
                    $"Insert between {DockerTitle(prev.PanelIds[0])} and {DockerTitle(row.PanelIds[0])}",
                    false,
                    row.PanelIds));
            }
        }

        CacheDockColumnBounds();
    }

    private void AddRowBodyBands(int columnIndex, VisualRowInfo row, Rect body, string rowTitle)
    {
        var edgeW = DockDropBands.BandSize(body.Width, HorizontalSplitEdgeRatio, 40, 0.45);
        var edgeH = DockDropBands.BandSize(body.Height, VerticalBandRatio, 20, 0.35);
        if (edgeW <= 0 || edgeH <= 0)
            return;

        var topBand = new Rect(body.X, body.Y, body.Width, edgeH);
        _dockDropZones.Add(new DockDropZone(
            DropZonePriority.RowBodyEdge,
            columnIndex,
            row.LayoutRowIndex,
            0,
            DockDropKind.InsertRow,
            topBand,
            body.Top,
            $"Insert above {rowTitle}",
            false,
            row.PanelIds,
            RowRect: row.RowRect));

        var bottomBand = new Rect(body.X, body.Bottom - edgeH, body.Width, edgeH);
        _dockDropZones.Add(new DockDropZone(
            DropZonePriority.RowBodyEdge,
            columnIndex,
            row.LayoutRowIndex + 1,
            0,
            DockDropKind.InsertRow,
            bottomBand,
            body.Bottom,
            $"Insert below {rowTitle}",
            false,
            row.PanelIds,
            RowRect: row.RowRect));

        var centerW = Math.Max(32, body.Width - edgeW * 2);
        var centerH = Math.Max(32, body.Height - edgeH * 2);
        if (centerW <= 0 || centerH <= 0)
            return;

        var center = new Rect(body.X + edgeW, body.Y + edgeH, centerW, centerH);
        _dockDropZones.Add(new DockDropZone(
            DropZonePriority.RowBody,
            columnIndex,
            row.LayoutRowIndex,
            0,
            DockDropKind.InsertRow,
            center,
            center.Top,
            rowTitle,
            false,
            row.PanelIds,
            RowRect: row.RowRect));
    }

    private void AddRailOuterEdgeZones()
    {
        if (_leftPanel is { IsVisible: true })
        {
            var rail = ControlRectInOverlay(_leftPanel);
            if (rail is { Width: > 0, Height: > 0 } r)
            {
                var w = ColumnEdgeHitWidthPx;
                var count = App.Config.WorkspaceLayout.LeftColumns.Count;
                AddColumnZone(
                    DropZonePriority.ColumnRail,
                    DockColumnIndices.Left(0),
                    0,
                    new Rect(r.X, r.Y, w, r.Height),
                    r.X,
                    "New column (far left)");
                AddColumnZone(
                    DropZonePriority.ColumnRail,
                    DockColumnIndices.Left(0),
                    count,
                    new Rect(r.Right - w, r.Y, w, r.Height),
                    r.Right,
                    "New left column (beside canvas)");
            }
        }

        if (_rightPanel is { IsVisible: true })
        {
            var rail = ControlRectInOverlay(_rightPanel);
            if (rail is { Width: > 0, Height: > 0 } r)
            {
                var w = ColumnEdgeHitWidthPx;
                AddColumnZone(
                    DropZonePriority.ColumnRail,
                    DockColumnIndices.Right(0),
                    0,
                    new Rect(r.X, r.Y, w, r.Height),
                    r.X,
                    "New right column (beside canvas)");
                AddColumnZone(
                    DropZonePriority.ColumnRail,
                    DockColumnIndices.Right(0),
                    App.Config.WorkspaceLayout.RightColumns.Count,
                    new Rect(r.Right - w, r.Y, w, r.Height),
                    r.Right,
                    "New right column (outer edge)");
            }
        }
    }

    private void AddCanvasEdgeColumnZones()
    {
        if (_rootGrid == null) return;

        foreach (var child in _rootGrid.Children)
        {
            if (Grid.GetColumn(child) != RootColCenter || child is not Control center)
                continue;

            var centerRect = ControlRectInOverlay(center);
            if (centerRect == null) break;

            var w = ColumnEdgeHitWidthPx;
            var r = centerRect.Value;
            var leftCount = App.Config.WorkspaceLayout.LeftColumns.Count;

            AddColumnZone(
                DropZonePriority.ColumnRail,
                DockColumnIndices.Left(0),
                leftCount,
                new Rect(r.Left - w, r.Top, w, r.Height),
                r.Left,
                "New left column (beside canvas)");

            AddColumnZone(
                DropZonePriority.ColumnRail,
                DockColumnIndices.Right(0),
                0,
                new Rect(r.Right, r.Top, w, r.Height),
                r.Right,
                "New right column (beside canvas)");
            break;
        }

        if (_workspaceViewport == null) return;
        var viewportRect = ControlRectInOverlay(_workspaceViewport);
        if (viewportRect == null) return;

        var bottomCount = App.Config.WorkspaceLayout.BottomColumns.Count;
        var vr = viewportRect.Value;
        var h = ColumnEdgeHitWidthPx;
        AddColumnZone(
            DropZonePriority.ColumnRail,
            DockColumnIndices.Bottom(0),
            bottomCount,
            new Rect(vr.Left, vr.Bottom - h, vr.Width, h),
            vr.Bottom,
            "New bottom column",
            horizontalLine: true);
    }

    private void AddHostColumnSplitterZones(
        Grid? hostGrid,
        DockZone zone,
        Func<int, int> encodeColumn)
    {
        if (hostGrid == null) return;

        var columnCount = zone switch
        {
            DockZone.Left => App.Config.WorkspaceLayout.LeftColumns.Count,
            DockZone.Right => App.Config.WorkspaceLayout.RightColumns.Count,
            DockZone.Bottom => App.Config.WorkspaceLayout.BottomColumns.Count,
            _ => 0
        };

        for (var i = 0; i < columnCount - 1; i++)
        {
            var splitCol = i * 2 + 1;
            if (splitCol >= hostGrid.ColumnDefinitions.Count) continue;
            var splitControl = hostGrid.Children.FirstOrDefault(c => Grid.GetColumn(c) == splitCol) as Control;
            if (splitControl == null) continue;

            var rect = ControlRectInOverlay(splitControl);
            if (rect is not { Width: > 0, Height: > 0 } split) continue;

            var hitW = Math.Max(ColumnSplitHitWidthPx, split.Width + 48);
            var lineX = split.X + split.Width * 0.5;
            var hit = new Rect(lineX - hitW * 0.5, split.Y, hitW, split.Height);
            var label = zone switch
            {
                DockZone.Left => "New left column here",
                DockZone.Bottom => "New bottom column here",
                _ => "New column here"
            };

            AddColumnZone(
                DropZonePriority.ColumnSplit,
                encodeColumn(i),
                i + 1,
                hit,
                lineX,
                label);
        }
    }

    private void AddColumnZone(
        DropZonePriority priority,
        int columnIndex,
        int insertColumnIndex,
        Rect bounds,
        double linePos,
        string label,
        bool horizontalLine = false)
    {
        _dockDropZones.Add(new DockDropZone(
            priority,
            columnIndex,
            -1,
            insertColumnIndex,
            DockDropKind.InsertDockColumn,
            bounds,
            linePos,
            label,
            horizontalLine,
            Array.Empty<string>()));
    }

    private sealed record VisualRowInfo(
        IReadOnlyList<string> PanelIds,
        int LayoutRowIndex,
        string Label,
        Rect? RowRect,
        Rect? TabStripRect,
        Rect? BodyRect,
        DockTabGroup? TabGroup);

    private List<VisualRowInfo> CollectVisualRows(
        Grid hostGrid,
        DockColumnLayout column,
        int columnIndex,
        string movingId)
    {
        var layoutRows = column.ResolvedRows();
        var result = new List<VisualRowInfo>();

        foreach (var child in hostGrid.Children.OrderBy(c => Grid.GetRow(c)))
        {
            if (child is GridSplitter or Border { Height: <= 1 })
                continue;

            var panelIds = RowPanelIdsFromControl(child);
            if (panelIds == null || panelIds.Count == 0)
                continue;

            if (panelIds.Count == 1 && string.Equals(panelIds[0], movingId, StringComparison.Ordinal))
                continue;

            if (!panelIds.Any(id => IsDockerVisible(id) && !IsDockerFloating(id)))
                continue;

            var layoutRowIndex = FindLayoutRowIndex(column, panelIds);
            if (layoutRowIndex < 0)
                layoutRowIndex = result.Count;

            var rowControl = child as Control;
            if (rowControl == null) continue;

            var rowRect = ControlRectInOverlay(rowControl);
            Rect? tabStripRect = null;
            Rect? bodyRect = rowRect;
            DockTabGroup? tabGroup = null;
            if (rowRect is { } rr)
            {
                if (child is DockTabGroup tg)
                {
                    tabGroup = tg;
                    var measured = ControlRectInOverlay(tabGroup.TabStrip);
                    var stripH = measured is { Height: > 0 } m
                        ? m.Height
                        : Math.Min(DockDropOverlay.TabStripHeight, Math.Max(20, rr.Height * 0.22));
                    var stripMax = Math.Min(DockDropOverlay.TabStripHeight + 4, rr.Height);
                    if (stripMax > 0)
                        stripH = Math.Clamp(stripH, Math.Min(20, stripMax), stripMax);
                    tabStripRect = new Rect(rr.X, rr.Y, rr.Width, stripH);
                    var bodyTop = tabStripRect.Value.Bottom;
                    bodyRect = new Rect(rr.X, bodyTop, rr.Width, Math.Max(0, rr.Bottom - bodyTop));
                }
            }

            var label = panelIds.Count > 1
                ? string.Join(" · ", panelIds.Select(DockerTitle))
                : DockerTitle(panelIds[0]);

            result.Add(new VisualRowInfo(panelIds, layoutRowIndex, label, rowRect, tabStripRect, bodyRect, tabGroup));
        }

        return result;
    }

    private static IReadOnlyList<string>? RowPanelIdsFromControl(Control child)
    {
        switch (child)
        {
            case DockTabGroup tg:
                return tg.PanelIds.ToList();
            case Border b when b.Tag is string soloId:
                return [soloId];
            case Grid hg:
            {
                var ids = hg.Children.OfType<Border>()
                    .Select(b => b.Tag as string)
                    .Where(id => id != null)
                    .Cast<string>()
                    .ToList();
                return ids.Count > 0 ? ids : null;
            }
            default:
                return null;
        }
    }

    private static int FindLayoutRowIndex(DockColumnLayout column, IReadOnlyList<string> panelIds)
    {
        var rows = column.ResolvedRows();
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].PanelIds.SequenceEqual(panelIds))
                return i;
        }

        for (var i = 0; i < rows.Count; i++)
        {
            if (panelIds.All(p => rows[i].PanelIds.Contains(p))
                && panelIds.Count == rows[i].PanelIds.Count)
                return i;
        }

        return -1;
    }

    private void CacheDockColumnBounds()
    {
        _dockColumnBounds.Clear();
        if (_dockDropOverlay == null) return;

        AddRailBounds(_leftPanel);
        AddRailBounds(_rightPanel);
        if (_bottomDockerHostGrid != null)
            AddRailBounds(_bottomDockerHostGrid);

        var layout = App.Config.WorkspaceLayout;
        var columnEntries = layout.LeftColumns
            .Select((c, i) => DockColumnIndices.Left(i))
            .Concat(layout.RightColumns.Select((_, i) => DockColumnIndices.Right(i)))
            .Concat(layout.BottomColumns.Select((_, i) => DockColumnIndices.Bottom(i)));

        foreach (var columnIndex in columnEntries)
        {
            var grid = ColumnGrid(columnIndex);
            if (grid == null) continue;
            var rect = ControlRectInOverlay(grid);
            if (rect is { Width: > 0, Height: > 0 })
                _dockColumnBounds.Add(rect.Value);
        }
    }

    private void AddRailBounds(Control? control)
    {
        if (control == null || !control.IsVisible) return;
        var rect = ControlRectInOverlay(control);
        if (rect is { Width: > 0, Height: > 0 })
            _dockColumnBounds.Add(rect.Value);
    }

    private Grid? ColumnGrid(int columnIndex)
    {
        var leftIdx = DockColumnIndices.TryParseLeft(columnIndex);
        if (leftIdx != null)
            return FindLeftColumnGrid(leftIdx.Value);

        var bottomIdx = DockColumnIndices.TryParseBottom(columnIndex);
        if (bottomIdx != null)
            return FindBottomColumnGrid(bottomIdx.Value);

        if (DockColumnIndices.IsRight(columnIndex))
            return FindRightColumnGrid(columnIndex);

        return null;
    }

    private Grid? FindLeftColumnGrid(int columnIndex)
    {
        if (_leftDockerHostGrid == null) return null;
        foreach (var child in _leftDockerHostGrid.Children)
        {
            if (child is Grid g && Grid.GetColumn(g) == columnIndex * 2)
                return g;
        }

        return _leftDockerHostGrid.Children.OfType<Grid>().ElementAtOrDefault(columnIndex);
    }

    private Rect? ControlRectInOverlay(Control control)
    {
        if (_dockDropOverlay == null) return null;
        var bounds = control.Bounds;
        var matrix = control.TransformToVisual(_dockDropOverlay);
        if (matrix == null) return null;
        var topLeft = matrix.Value.Transform(new Point(0, 0));
        var rect = new Rect(topLeft, bounds.Size);
        return rect.Width > 0 && rect.Height > 0 ? rect : null;
    }

    private Grid? FindRightColumnGrid(int columnIndex)
    {
        if (_dockerHostGrid == null) return null;
        foreach (var child in _dockerHostGrid.Children)
        {
            if (child is Grid g && Grid.GetColumn(g) == columnIndex * 2)
                return g;
        }

        return _dockerHostGrid.Children.OfType<Grid>().ElementAtOrDefault(columnIndex);
    }

    private Grid? FindBottomColumnGrid(int columnIndex)
    {
        if (_bottomDockerHostGrid == null) return null;
        foreach (var child in _bottomDockerHostGrid.Children)
        {
            if (child is Grid g && Grid.GetColumn(g) == columnIndex * 2)
                return g;
        }

        return _bottomDockerHostGrid.Children.OfType<Grid>().ElementAtOrDefault(columnIndex);
    }

    private void UpdateDockerDropPreview(string movingId, Point rootPt)
    {
        if (_dockDropOverlay == null)
            return;

        var target = ResolveDropTarget(rootPt, movingId);
        _resolvedDropTarget = target;
        if (target != null)
            _stickyDropTarget = target;
        else
            _stickyDropTarget = null;

        if (target == null)
        {
            _dockDropColumn = -99;
            _dockDropRowIndex = -1;
            _dockDropOverlay.Clear();
            return;
        }

        _dockDropColumn = target.ColumnIndex;
        _dockDropInsertColumnIndex = target.InsertColumnIndex;
        _dockDropRowIndex = target.RowIndex;
        _dockDropKind = target.Kind;

        switch (target.Kind)
        {
            case DockDropKind.MergeTab:
                if (target.TabInsertIndex >= 0)
                    _dockDropOverlay.ShowInsertLineVertical(target.LinePos, target.Bounds.Y, target.Bounds.Height);
                else
                    _dockDropOverlay.ShowTabTarget(
                        target.Bounds.X, target.Bounds.Y, target.Bounds.Width, target.Bounds.Height);
                break;
            case DockDropKind.InsertDockColumn:
                if (target.HorizontalInsertLine)
                    _dockDropOverlay.ShowInsertLine(target.Bounds.X, target.LinePos, target.Bounds.Width);
                else
                    _dockDropOverlay.ShowInsertLineVertical(target.LinePos, target.Bounds.Y, target.Bounds.Height);
                break;
            default:
                _dockDropOverlay.ShowInsertLine(target.Bounds.X, target.LinePos, target.Bounds.Width);
                break;
        }

        _dockDropOverlay.ShowHint(target.Label, rootPt.X, rootPt.Y);
    }

    private DropTargetState? ResolveDropTarget(Point rootPt, string movingId)
    {
        var sourcePlacement = DockLayoutOps.FindPlacement(App.Config.WorkspaceLayout, movingId);

        var zoneHit = _dockDropZones
            .Where(z => z.Bounds.Contains(rootPt))
            .Where(z => IsZoneAllowed(z, movingId, sourcePlacement, rootPt))
            .OrderBy(z => z.Priority)
            .ThenBy(z => z.Bounds.Width * z.Bounds.Height)
            .FirstOrDefault();

        if (zoneHit != null)
        {
            return ZoneToTarget(zoneHit, rootPt);
        }

        var columnRows = _dockDropZones
            .Where(z => z.Priority == DropZonePriority.RowBody)
            .GroupBy(z => z.ColumnIndex)
            .OrderBy(g => DistanceToColumn(g.Key, rootPt.X))
            .FirstOrDefault();

        if (columnRows == null) return null;

        var orderedBodies = columnRows.OrderBy(z => z.Bounds.Top).ToList();
        var topRow = orderedBodies[0];
        var topFull = topRow.RowRect ?? topRow.Bounds;
        if (rootPt.Y < topFull.Top && rootPt.X >= topFull.Left && rootPt.X <= topFull.Right)
        {
            return new DropTargetState(
                topRow.ColumnIndex,
                0,
                topRow.LayoutRowIndex,
                DockDropKind.InsertRow,
                topFull.Top,
                topFull,
                $"Insert above {topRow.Label}",
                false);
        }

        var bottomRow = orderedBodies[^1];
        var bottomFull = bottomRow.RowRect ?? bottomRow.Bounds;
        if (rootPt.Y > bottomFull.Bottom && rootPt.X >= bottomFull.Left && rootPt.X <= bottomFull.Right)
        {
            return new DropTargetState(
                bottomRow.ColumnIndex,
                0,
                bottomRow.LayoutRowIndex + 1,
                DockDropKind.InsertRow,
                bottomFull.Bottom,
                bottomFull,
                $"Insert below {bottomRow.Label}",
                false);
        }

        var nearestRow = orderedBodies
            .OrderBy(z => DistanceToRect(z.Bounds, rootPt))
            .First();

        var nearFull = nearestRow.RowRect ?? nearestRow.Bounds;
        var above = rootPt.Y < nearFull.Top + nearFull.Height * 0.5;
        var lineY = above ? nearFull.Top : nearFull.Bottom;
        var insertIdx = above ? nearestRow.LayoutRowIndex : nearestRow.LayoutRowIndex + 1;
        return new DropTargetState(
            nearestRow.ColumnIndex,
            0,
            insertIdx,
            DockDropKind.InsertRow,
            lineY,
            nearFull,
            above ? $"Insert above {nearestRow.Label}" : $"Insert below {nearestRow.Label}",
            false);
    }

    private bool IsZoneAllowed(
        DockDropZone zone,
        string movingId,
        DockPlacement? sourcePlacement,
        Point rootPt)
    {
        if (zone.Kind != DockDropKind.MergeTab)
            return true;

        if (zone.Priority == DropZonePriority.TabInsert)
            return true;

        var onSourceRow = sourcePlacement != null
                          && sourcePlacement.ColumnIndex == zone.ColumnIndex
                          && sourcePlacement.RowIndex == zone.LayoutRowIndex
                          && zone.RowPanelIds.Contains(movingId);

        if (!onSourceRow)
            return true;

        // Whole-strip fallback on own row → insert row, not re-merge at end.
        return false;
    }

    private void AddPanelColumnEdgeZones(int columnIndex, Rect panelRect, string rowLabel)
    {
        if (panelRect.Width < 96 || panelRect.Height < 32)
            return;

        var edgeW = panelRect.Width * HorizontalSplitEdgeRatio;
        var zone = DockColumnIndices.ZoneOf(columnIndex);

        if (zone == DockZone.Left && DockColumnIndices.TryParseLeft(columnIndex) is { } leftIdx)
        {
            AddPanelColumnEdgeZone(
                columnIndex,
                leftIdx,
                new Rect(panelRect.X, panelRect.Y, edgeW, panelRect.Height),
                panelRect.X,
                $"New left column (left of {rowLabel})");

            AddPanelColumnEdgeZone(
                columnIndex,
                leftIdx + 1,
                new Rect(panelRect.Right - edgeW, panelRect.Y, edgeW, panelRect.Height),
                panelRect.Right,
                leftIdx + 1 >= App.Config.WorkspaceLayout.LeftColumns.Count
                    ? "New left column (beside canvas)"
                    : $"Split left column at {rowLabel}");
        }
        else if (zone == DockZone.Right && DockColumnIndices.IsRight(columnIndex))
        {
            AddPanelColumnEdgeZone(
                columnIndex,
                columnIndex,
                new Rect(panelRect.X, panelRect.Y, edgeW, panelRect.Height),
                panelRect.X,
                columnIndex == 0 ? "New right column (beside canvas)" : "Split right column");

            AddPanelColumnEdgeZone(
                columnIndex,
                columnIndex + 1,
                new Rect(panelRect.Right - edgeW, panelRect.Y, edgeW, panelRect.Height),
                panelRect.Right,
                "Split right column");
        }
    }

    private void AddPanelColumnEdgeZone(
        int columnIndex,
        int insertColumnIndex,
        Rect bounds,
        double linePos,
        string label)
    {
        AddColumnZone(
            DropZonePriority.ColumnSplit,
            columnIndex,
            insertColumnIndex,
            bounds,
            linePos,
            label);
    }

    private DropTargetState ZoneToTarget(DockDropZone zone, Point rootPt)
    {
        switch (zone.Kind)
        {
            case DockDropKind.InsertDockColumn:
                return new DropTargetState(
                    zone.ColumnIndex,
                    zone.InsertColumnIndex,
                    -1,
                    DockDropKind.InsertDockColumn,
                    zone.LinePos,
                    zone.Bounds,
                    zone.Label,
                    zone.HorizontalInsertLine);

            case DockDropKind.MergeTab:
            {
                var insertIndex = zone.TabInsertIndex;
                if (insertIndex < 0 && zone.RowPanelIds.Count > 0)
                    insertIndex = DockLayoutOps.ResolveTabInsertIndex(
                        zone.RowPanelIds
                            .Select((_, i) =>
                            {
                                var w = zone.Bounds.Width / zone.RowPanelIds.Count;
                                var left = zone.Bounds.X + i * w;
                                return (left, left + w);
                            })
                            .ToList(),
                        rootPt.X);

                var lineX = zone.LinePos;
                if (insertIndex >= 0 && zone.Priority == DropZonePriority.TabInsert)
                    lineX = zone.LinePos;
                else if (insertIndex >= 0 && insertIndex <= zone.RowPanelIds.Count)
                    lineX = ResolveTabInsertLineX(zone, insertIndex);

                return new DropTargetState(
                    zone.ColumnIndex,
                    0,
                    zone.LayoutRowIndex,
                    DockDropKind.MergeTab,
                    lineX,
                    zone.Bounds,
                    insertIndex >= 0
                        ? $"Insert tab at position {insertIndex + 1}"
                        : zone.Label,
                    false,
                    zone.RowPanelIds.ToList(),
                    insertIndex);
            }

            case DockDropKind.InsertRow when zone.Priority is DropZonePriority.RowGap or DropZonePriority.RowBodyEdge:
                return new DropTargetState(
                    zone.ColumnIndex,
                    0,
                    zone.LayoutRowIndex,
                    DockDropKind.InsertRow,
                    zone.LinePos,
                    zone.Bounds,
                    zone.Label,
                    false,
                    null);

            default:
            {
                var above = rootPt.Y < zone.Bounds.Top + zone.Bounds.Height * 0.5;
                var lineY = above ? zone.Bounds.Top : zone.Bounds.Bottom;
                var insertIdx = above ? zone.LayoutRowIndex : zone.LayoutRowIndex + 1;
                var name = zone.Label;
                return new DropTargetState(
                    zone.ColumnIndex,
                    0,
                    insertIdx,
                    DockDropKind.InsertRow,
                    lineY,
                    zone.Bounds,
                    above ? $"Insert above {name}" : $"Insert below {name}",
                    false);
            }
        }
    }

    private static double DistanceToRect(Rect rect, Point pt)
    {
        if (rect.Contains(pt)) return 0;
        var dx = pt.X < rect.Left ? rect.Left - pt.X : pt.X > rect.Right ? pt.X - rect.Right : 0;
        var dy = pt.Y < rect.Top ? rect.Top - pt.Y : pt.Y > rect.Bottom ? pt.Y - rect.Bottom : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }

    private double DistanceToColumn(int columnIndex, double x)
    {
        var rows = _dockDropZones
            .Where(z => z.ColumnIndex == columnIndex && z.Priority == DropZonePriority.RowBody)
            .ToList();
        if (rows.Count == 0) return double.MaxValue;
        var left = rows.Min(m => m.Bounds.Left);
        var right = rows.Max(m => m.Bounds.Right);
        if (x >= left && x <= right) return 0;
        return Math.Min(Math.Abs(x - left), Math.Abs(x - right));
    }

    private void AddTabInsertZones(
        int columnIndex,
        VisualRowInfo row,
        Rect tabStrip,
        DockTabGroup? tabGroup)
    {
        if (_dockDropOverlay == null || tabGroup == null || row.PanelIds.Count == 0)
            return;

        var tabBounds = tabGroup.GetTabBoundsRelativeTo(_dockDropOverlay);
        if (tabBounds.Count == 0)
            return;

        const double hitHalfWidth = 10;

        for (var i = 0; i <= tabBounds.Count; i++)
        {
            double lineX;
            Rect hit;
            if (i == 0)
            {
                lineX = tabBounds[0].Bounds.Left;
                hit = new Rect(
                    lineX - hitHalfWidth,
                    tabStrip.Y,
                    hitHalfWidth * 2,
                    tabStrip.Height);
            }
            else if (i == tabBounds.Count)
            {
                lineX = tabBounds[^1].Bounds.Right;
                hit = new Rect(
                    lineX - hitHalfWidth,
                    tabStrip.Y,
                    hitHalfWidth * 2,
                    tabStrip.Height);
            }
            else
            {
                lineX = (tabBounds[i - 1].Bounds.Right + tabBounds[i].Bounds.Left) * 0.5;
                hit = new Rect(
                    lineX - hitHalfWidth,
                    tabStrip.Y,
                    hitHalfWidth * 2,
                    tabStrip.Height);
            }

            var title = i < tabBounds.Count
                ? DockerTitle(tabBounds[i].PanelId)
                : DockerTitle(tabBounds[^1].PanelId);
            _dockDropZones.Add(new DockDropZone(
                DropZonePriority.TabInsert,
                columnIndex,
                row.LayoutRowIndex,
                0,
                DockDropKind.MergeTab,
                hit,
                lineX,
                i < tabBounds.Count ? $"Insert before {title}" : $"Insert after {title}",
                false,
                row.PanelIds,
                TabInsertIndex: i));
        }
    }

    private static double ResolveTabInsertLineX(DockDropZone zone, int insertIndex)
    {
        if (insertIndex <= 0)
            return zone.Bounds.X;

        if (insertIndex >= zone.RowPanelIds.Count)
            return zone.Bounds.Right;

        var w = zone.Bounds.Width / zone.RowPanelIds.Count;
        return zone.Bounds.X + insertIndex * w;
    }

    private static double DragDistance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
