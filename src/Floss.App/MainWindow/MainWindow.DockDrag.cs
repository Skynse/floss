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
    private const double DropHysteresisPx = 12;
    private const double HorizontalSplitEdgeRatio = 0.35;

    private DockDropOverlay? _dockDropOverlay;
    private string? _dockDragPanelId;
    private Point _dockDragStart;
    private bool _dockDragActive;
    private int _dockDropColumn = -99;
    private DockDropKind _dockDropKind = DockDropKind.InsertRow;
    private int _dockDropRowIndex;
    private readonly List<DockPanelMetric> _dockPanelMetrics = [];
    private readonly List<DockRowContainerMetric> _dockRowMetrics = [];
    private readonly List<DockColumnEdgeMetric> _dockColumnEdgeMetrics = [];
    private readonly List<Rect> _dockColumnBounds = [];
    private DropTargetState? _stickyTarget;
    private bool _dockerDragWindowHandlersActive;

    private sealed record DockPanelMetric(
        int ColumnIndex,
        int RowIndex,
        string PanelId,
        Rect Bounds,
        bool IsTabGroup,
        string Label);

    private sealed record DockRowContainerMetric(
        int ColumnIndex,
        int RowIndex,
        Rect Bounds,
        bool IsTabGroup,
        string Label);

    private sealed record DockColumnEdgeMetric(
        DockZone Zone,
        int InsertColumnIndex,
        Rect Bounds,
        string Label,
        bool HorizontalInsertLine);

    private void EnsureDockDropOverlay()
    {
        if (_dockDropOverlay != null || _rootGrid == null) return;

        _dockDropOverlay = new DockDropOverlay();
        Grid.SetColumn(_dockDropOverlay, 0);
        Grid.SetColumnSpan(_dockDropOverlay, 6);
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

    private void OnTabDragStarted(string panelId, PointerPressedEventArgs e)
    {
        BeginDockerDrag(panelId, e.GetPosition(this));
    }

    private void OnTabDragMoved(string panelId, PointerEventArgs e)
    {
        if (_dockDragPanelId != panelId) return;
        ContinueDockerDrag(panelId, e.GetPosition(this), e);
    }

    private void OnTabDragEnded(string panelId, PointerReleasedEventArgs e)
    {
        if (_dockDragPanelId != panelId) return;
        FinishDockerDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void DockerHeaderPressed(string panelId, Control header, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) return;
        BeginDockerDrag(panelId, e.GetPosition(this));
        e.Pointer.Capture(header);
        e.Handled = true;
    }

    private void DockerHeaderMoved(string panelId, Control header, PointerEventArgs e)
    {
        if (_dockDragPanelId != panelId) return;
        ContinueDockerDrag(panelId, e.GetPosition(this), e);
        e.Handled = true;
    }

    private void DockerHeaderReleased(string panelId, Control header, PointerReleasedEventArgs e)
    {
        if (_dockDragPanelId != panelId) return;
        FinishDockerDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void BeginDockerDrag(string panelId, Point rootPt)
    {
        EnsureDockDropOverlay();
        _dockDragPanelId = panelId;
        _dockDragStart = rootPt;
        _dockDragActive = false;
        _dockDropColumn = -99;
        _dockDropKind = DockDropKind.InsertRow;
        _dockDropRowIndex = -1;
        _stickyTarget = null;
        _dockPanelMetrics.Clear();
        _dockRowMetrics.Clear();
        _dockColumnEdgeMetrics.Clear();
        _dockColumnBounds.Clear();
    }

    private void ContinueDockerDrag(string panelId, Point rootPt, PointerEventArgs e)
    {
        if (!_dockDragActive && DragDistance(rootPt, _dockDragStart) < DockDragThreshold)
            return;

        if (!_dockDragActive)
        {
            _dockDragActive = true;
            EnsureDockerDragWindowHandlers();
            CacheDockDropMetrics(panelId);
        }

        UpdateDockerDropPreview(panelId, rootPt);
        if (e != null && _dockDragActive)
            e.Handled = true;
    }

    private void EnsureDockerDragWindowHandlers()
    {
        if (_dockerDragWindowHandlersActive) return;
        _dockerDragWindowHandlersActive = true;
        AddHandler(PointerMovedEvent, OnDockerDragWindowPointerMoved,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnDockerDragWindowPointerReleased,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble);
    }

    private void RemoveDockerDragWindowHandlers()
    {
        if (!_dockerDragWindowHandlersActive) return;
        _dockerDragWindowHandlersActive = false;
        RemoveHandler(PointerMovedEvent, OnDockerDragWindowPointerMoved);
        RemoveHandler(PointerReleasedEvent, OnDockerDragWindowPointerReleased);
    }

    private void OnDockerDragWindowPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dockDragPanelId == null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            CancelDockerDrag();
            return;
        }

        ContinueDockerDrag(_dockDragPanelId, e.GetPosition(this), e);
    }

    private void OnDockerDragWindowPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dockDragPanelId == null) return;
        if (_dockDragActive)
            FinishDockerDrag();
        else
            CancelDockerDrag();
    }

    private void FinishDockerDrag()
    {
        if (_dockDragActive && _dockDragPanelId != null && _stickyTarget != null)
            CommitDockerDrop(_dockDragPanelId);
        CancelDockerDrag();
    }

    private void CancelDockerDrag()
    {
        RemoveDockerDragWindowHandlers();
        _dockDragPanelId = null;
        _dockDragActive = false;
        _dockDropColumn = -99;
        _dockDropRowIndex = -1;
        _stickyTarget = null;
        _dockPanelMetrics.Clear();
        _dockRowMetrics.Clear();
        _dockColumnEdgeMetrics.Clear();
        _dockColumnBounds.Clear();
        _dockDropOverlay?.Clear();
    }

    private void CommitDockerDrop(string panelId)
    {
        SaveWorkspaceLayoutFromUi();
        var layout = App.Config.WorkspaceLayout;

        DockLayoutOps.ApplyDrop(
            layout,
            panelId,
            _dockDropColumn,
            _dockDropRowIndex,
            _dockDropKind,
            mergeTabRowIndex: _dockDropRowIndex);

        foreach (var col in layout.RightColumns)
            DockLayoutOps.CompactTabGroups(col);
        foreach (var col in layout.LeftColumns)
            DockLayoutOps.CompactTabGroups(col);
        foreach (var col in layout.BottomColumns)
            DockLayoutOps.CompactTabGroups(col);

        RebuildDockers();
        App.Config.Save();
    }

    private void CacheDockDropMetrics(string movingId)
    {
        _dockPanelMetrics.Clear();
        _dockRowMetrics.Clear();
        _dockColumnBounds.Clear();
        if (_dockDropOverlay == null) return;

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

            var allRows = column.ResolvedRows().ToList();
            for (var ri = 0; ri < allRows.Count; ri++)
            {
                var row = allRows[ri];
                if (row.PanelIds.Count == 1 && row.PanelIds.Contains(movingId))
                    continue;
                var rowControl = FindRowContainer(grid, row);
                if (rowControl == null) continue;

                var rowRect = ControlRectInOverlay(rowControl);
                if (rowRect == null) continue;

                var isTabGroup = row.Orientation == DockOrientation.Vertical;
                var label = row.PanelIds.Count > 1
                    ? string.Join(" · ", row.PanelIds.Select(DockerTitle))
                    : DockerTitle(row.PanelIds[0]);

                _dockRowMetrics.Add(new DockRowContainerMetric(columnIndex, ri, rowRect.Value, isTabGroup, label));

                if (isTabGroup)
                    continue;

                foreach (var pid in row.PanelIds)
                {
                    if (string.Equals(pid, movingId, StringComparison.Ordinal))
                        continue;
                    if (!IsDockerVisible(pid) || IsDockerFloating(pid))
                        continue;

                    var section = FindPanelSection(rowControl, pid);
                    if (section == null) continue;

                    var panelRect = ControlRectInOverlay(section);
                    if (panelRect == null) continue;

                    _dockPanelMetrics.Add(new DockPanelMetric(columnIndex, ri, pid, panelRect.Value, false, DockerTitle(pid)));
                }
            }
        }

        CacheDockColumnBounds();
        CacheDockColumnEdgeMetrics();
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

    private bool IsOverDockColumn(Point rootPt)
        => _dockColumnBounds.Any(r => r.Contains(rootPt));

    private void CacheDockColumnEdgeMetrics()
    {
        _dockColumnEdgeMetrics.Clear();
        if (_dockDropOverlay == null || _rootGrid == null) return;

        const double edgeWidth = 28;
        var layout = App.Config.WorkspaceLayout;

        foreach (var child in _rootGrid.Children)
        {
            if (Grid.GetColumn(child) != RootColCenter || child is not Control center)
                continue;

            var centerRect = ControlRectInOverlay(center);
            if (centerRect == null) continue;

            var leftStrip = new Rect(centerRect.Value.Left - edgeWidth, centerRect.Value.Top, edgeWidth,
                centerRect.Value.Height);
            _dockColumnEdgeMetrics.Add(new DockColumnEdgeMetric(
                DockZone.Left,
                layout.LeftColumns.Count,
                leftStrip,
                "New left column (beside canvas)",
                HorizontalInsertLine: false));

            var rightStrip = new Rect(centerRect.Value.Right, centerRect.Value.Top, edgeWidth,
                centerRect.Value.Height);
            _dockColumnEdgeMetrics.Add(new DockColumnEdgeMetric(
                DockZone.Right,
                0,
                rightStrip,
                "New right column (beside canvas)",
                HorizontalInsertLine: false));
            break;
        }

        if (_workspaceViewport != null)
        {
            var viewportRect = ControlRectInOverlay(_workspaceViewport);
            if (viewportRect != null)
            {
                var bottomStrip = new Rect(
                    viewportRect.Value.Left,
                    viewportRect.Value.Bottom - edgeWidth,
                    viewportRect.Value.Width,
                    edgeWidth);
                _dockColumnEdgeMetrics.Add(new DockColumnEdgeMetric(
                    DockZone.Bottom,
                    layout.BottomColumns.Count,
                    bottomStrip,
                    "New bottom column (above footer)",
                    HorizontalInsertLine: true));
            }
        }

        if (_leftDockerHostGrid != null)
        {
            for (var i = 0; i < layout.LeftColumns.Count - 1; i++)
            {
                var splitCol = i * 2 + 1;
                if (splitCol >= _leftDockerHostGrid.ColumnDefinitions.Count) continue;
                var splitControl = _leftDockerHostGrid.Children.FirstOrDefault(c => Grid.GetColumn(c) == splitCol);
                if (splitControl == null) continue;
                var rect = ControlRectInOverlay(splitControl);
                if (rect == null) continue;
                var zone = new Rect(rect.Value.Left - edgeWidth * 0.5, rect.Value.Top, edgeWidth, rect.Value.Height);
                _dockColumnEdgeMetrics.Add(new DockColumnEdgeMetric(
                    DockZone.Left, i + 1, zone, "Split left column", false));
            }
        }

        if (_dockerHostGrid != null)
            AddHostSplitterEdgeMetrics(_dockerHostGrid, layout.RightColumns.Count, DockZone.Right);

        if (_bottomDockerHostGrid != null)
            AddHostSplitterEdgeMetrics(_bottomDockerHostGrid, layout.BottomColumns.Count, DockZone.Bottom);
    }

    private void AddHostSplitterEdgeMetrics(Grid hostGrid, int columnCount, DockZone zone)
    {
        const double edgeWidth = 28;
        for (var i = 0; i < columnCount - 1; i++)
        {
            var splitCol = i * 2 + 1;
            if (splitCol >= hostGrid.ColumnDefinitions.Count) continue;
            var splitControl = hostGrid.Children.FirstOrDefault(c => Grid.GetColumn(c) == splitCol);
            if (splitControl == null) continue;
            var rect = ControlRectInOverlay(splitControl);
            if (rect == null) continue;
            var hit = new Rect(rect.Value.Left - edgeWidth * 0.5, rect.Value.Top, edgeWidth, rect.Value.Height);
            var label = zone switch
            {
                DockZone.Bottom => "Split bottom column",
                DockZone.Left => "Split left column",
                _ => "Split right column"
            };
            _dockColumnEdgeMetrics.Add(new DockColumnEdgeMetric(zone, i + 1, hit, label, false));
        }
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

        var target = ResolveDropTarget(rootPt);
        if (target == null)
        {
            _dockDropColumn = -99;
            _dockDropRowIndex = -1;
            _stickyTarget = null;
            _dockDropOverlay.Clear();
            return;
        }

        if (_stickyTarget is { } sticky
            && target.Kind == sticky.Kind
            && target.ColumnIndex == sticky.ColumnIndex
            && target.InsertColumnIndex == sticky.InsertColumnIndex
            && target.RowIndex == sticky.RowIndex
            && Math.Abs(target.LinePos - sticky.LinePos) < DropHysteresisPx)
            target = sticky;
        else
            _stickyTarget = target;

        _dockDropColumn = target.ColumnIndex;
        _dockDropRowIndex = target.Kind == DockDropKind.InsertDockColumn
            ? target.InsertColumnIndex
            : target.RowIndex;
        _dockDropKind = target.Kind;

        switch (target.Kind)
        {
            case DockDropKind.MergeTab:
                _dockDropOverlay.ShowTabTarget(
                    target.Bounds.X, target.Bounds.Y, target.Bounds.Width, DockDropOverlay.TabStripHeight);
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

    private sealed record DropTargetState(
        int ColumnIndex,
        int InsertColumnIndex,
        int RowIndex,
        DockDropKind Kind,
        double LinePos,
        Rect Bounds,
        string Label,
        bool HorizontalInsertLine);

    private DropTargetState? ResolveDropTarget(Point rootPt)
    {
        var rowHit = _dockRowMetrics
            .Where(m => m.Bounds.Contains(rootPt))
            .OrderBy(m => m.Bounds.Width * m.Bounds.Height)
            .FirstOrDefault();

        if (rowHit != null)
        {
            if (rowHit.IsTabGroup)
            {
                var insertBand = Math.Min(20, rowHit.Bounds.Height * 0.12);
                if (rootPt.Y >= rowHit.Bounds.Top + insertBand
                    && rootPt.Y <= rowHit.Bounds.Bottom - insertBand)
                {
                    var zoneLabel = DockColumnIndices.ZoneOf(rowHit.ColumnIndex) switch
                    {
                        DockZone.Left => "left",
                        DockZone.Bottom => "bottom",
                        _ => "right"
                    };
                    return new DropTargetState(
                        rowHit.ColumnIndex,
                        0,
                        rowHit.RowIndex,
                        DockDropKind.MergeTab,
                        rowHit.Bounds.Top,
                        rowHit.Bounds,
                        $"Add tab ({zoneLabel}): {rowHit.Label}",
                        false);
                }
            }

            var aboveRow = rootPt.Y < rowHit.Bounds.Top + rowHit.Bounds.Height * 0.5;
            var lineY = aboveRow ? rowHit.Bounds.Top : rowHit.Bounds.Bottom;
            var insertIdx = aboveRow ? rowHit.RowIndex : rowHit.RowIndex + 1;
            return new DropTargetState(
                rowHit.ColumnIndex,
                0,
                insertIdx,
                DockDropKind.InsertRow,
                lineY,
                rowHit.Bounds,
                $"New row ({rowHit.Label})",
                false);
        }

        var panelHit = _dockPanelMetrics
            .Where(m => m.Bounds.Contains(rootPt))
            .OrderBy(m => m.Bounds.Width * m.Bounds.Height)
            .FirstOrDefault();

        if (panelHit != null)
        {
            if (panelHit.IsTabGroup && rootPt.Y < panelHit.Bounds.Top + DockDropOverlay.TabStripHeight)
            {
                return new DropTargetState(
                    panelHit.ColumnIndex,
                    0,
                    panelHit.RowIndex,
                    DockDropKind.MergeTab,
                    panelHit.Bounds.Top,
                    panelHit.Bounds,
                    $"Add to tab: {panelHit.Label}",
                    false);
            }

            var leftEdge = panelHit.Bounds.X + panelHit.Bounds.Width * HorizontalSplitEdgeRatio;
            var rightEdge = panelHit.Bounds.Right - panelHit.Bounds.Width * HorizontalSplitEdgeRatio;

            if (rootPt.X <= leftEdge || rootPt.X >= rightEdge)
            {
                var insertCol = ResolveInsertColumnIndex(panelHit.ColumnIndex, rootPt.X <= leftEdge);
                var lineX = rootPt.X <= leftEdge ? panelHit.Bounds.Left : panelHit.Bounds.Right;
                return new DropTargetState(
                    panelHit.ColumnIndex,
                    insertCol,
                    -1,
                    DockDropKind.InsertDockColumn,
                    lineX,
                    panelHit.Bounds,
                    "New dock column",
                    false);
            }

            var above = rootPt.Y < panelHit.Bounds.Top + panelHit.Bounds.Height * 0.5;
            var lineY = above ? panelHit.Bounds.Top : panelHit.Bounds.Bottom;
            var insertIdx = above ? panelHit.RowIndex : panelHit.RowIndex + 1;
            return new DropTargetState(
                panelHit.ColumnIndex,
                0,
                insertIdx,
                DockDropKind.InsertRow,
                lineY,
                panelHit.Bounds,
                $"New row ({panelHit.Label})",
                false);
        }

        if (!IsOverDockColumn(rootPt))
        {
            var edgeHit = _dockColumnEdgeMetrics
                .Where(m => m.Bounds.Contains(rootPt))
                .OrderBy(m => m.Bounds.Width * m.Bounds.Height)
                .FirstOrDefault();

            if (edgeHit != null)
            {
                if (edgeHit.HorizontalInsertLine)
                {
                    return new DropTargetState(
                        DockColumnIndices.Bottom(0),
                        edgeHit.InsertColumnIndex,
                        -1,
                        DockDropKind.InsertDockColumn,
                        edgeHit.Bounds.Top,
                        edgeHit.Bounds,
                        edgeHit.Label,
                        true);
                }

                var lineX = edgeHit.Zone == DockZone.Left
                    ? edgeHit.Bounds.Right
                    : edgeHit.Bounds.Left;
                return new DropTargetState(
                    DockColumnIndices.Encode(edgeHit.Zone, 0),
                    edgeHit.InsertColumnIndex,
                    -1,
                    DockDropKind.InsertDockColumn,
                    lineX,
                    edgeHit.Bounds,
                    edgeHit.Label,
                    false);
            }
        }

        var inColumn = _dockRowMetrics
            .GroupBy(m => m.ColumnIndex)
            .OrderBy(g => DistanceToColumn(g.Key, rootPt.X))
            .FirstOrDefault();

        if (inColumn == null) return null;

        var edgeRow = rootPt.Y < inColumn.Min(m => m.Bounds.Top)
            ? inColumn.OrderBy(m => m.Bounds.Top).First()
            : inColumn.OrderByDescending(m => m.Bounds.Bottom).First();

        var aboveEdge = rootPt.Y < edgeRow.Bounds.Top + edgeRow.Bounds.Height * 0.5;
        var edgeLineY = aboveEdge ? edgeRow.Bounds.Top : edgeRow.Bounds.Bottom;
        var edgeInsert = aboveEdge ? edgeRow.RowIndex : edgeRow.RowIndex + 1;
        return new DropTargetState(
            edgeRow.ColumnIndex,
            0,
            edgeInsert,
            DockDropKind.InsertRow,
            edgeLineY,
            edgeRow.Bounds,
            $"New row ({edgeRow.Label})",
            false);
    }

    private static int ResolveInsertColumnIndex(int columnIndex, bool insertBefore)
    {
        var leftIdx = DockColumnIndices.TryParseLeft(columnIndex);
        if (leftIdx != null)
            return insertBefore ? leftIdx.Value : leftIdx.Value + 1;

        var bottomIdx = DockColumnIndices.TryParseBottom(columnIndex);
        if (bottomIdx != null)
            return insertBefore ? bottomIdx.Value : bottomIdx.Value + 1;

        return insertBefore ? columnIndex : columnIndex + 1;
    }

    private double DistanceToColumn(int columnIndex, double x)
    {
        var rows = _dockRowMetrics.Where(m => m.ColumnIndex == columnIndex).ToList();
        if (rows.Count == 0) return double.MaxValue;
        var left = rows.Min(m => m.Bounds.Left);
        var right = rows.Max(m => m.Bounds.Right);
        if (x >= left && x <= right) return 0;
        return Math.Min(Math.Abs(x - left), Math.Abs(x - right));
    }

    private static Control? FindRowContainer(Grid hostGrid, ResolvedRow row)
    {
        foreach (var child in hostGrid.Children)
        {
            if (child is DockTabGroup tg && tg.PanelIds.SequenceEqual(row.PanelIds))
                return tg;

            if (row.Orientation == DockOrientation.Horizontal && child is Grid hg
                && RowContainsPanelSections(hg, row.PanelIds))
                return hg;

            if (row.PanelIds.Count == 1 && child is Border b && b.Tag is string t && t == row.PanelIds[0])
                return b;
        }

        return null;
    }

    private static bool RowContainsPanelSections(Grid grid, IReadOnlyList<string> panelIds)
        => panelIds.Any(id => grid.Children.OfType<Border>().Any(b => b.Tag is string t && t == id));

    private static Border? FindPanelSection(Control rowContainer, string panelId)
    {
        if (rowContainer is Border b && b.Tag is string t && t == panelId)
            return b;

        if (rowContainer is Grid g)
        {
            foreach (var child in g.Children.OfType<Border>())
            {
                if (child.Tag is string tag && tag == panelId)
                    return child;
            }
        }

        return null;
    }

    private static double DragDistance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
