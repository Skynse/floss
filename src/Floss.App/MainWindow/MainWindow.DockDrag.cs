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

    private DockDropOverlay? _dockDropOverlay;
    private string? _dockDragPanelId;
    private Point _dockDragStart;
    private bool _dockDragActive;
    private int _dockDropColumn = -99;
    private int _dockDropRow = -1;
    private readonly List<DockRowMetric> _dockRowMetrics = [];
    private DropTargetState? _stickyTarget;

    private sealed record DockRowMetric(
        int ColumnIndex,
        int RowIndex,
        int RowCode,
        Rect Bounds,
        bool IsTabGroup,
        string Label);

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
        _dockDropRow = -1;
        _stickyTarget = null;
        _dockRowMetrics.Clear();
    }

    private void ContinueDockerDrag(string panelId, Point rootPt, PointerEventArgs e)
    {
        if (!_dockDragActive && DragDistance(rootPt, _dockDragStart) < DockDragThreshold)
            return;

        if (!_dockDragActive)
        {
            _dockDragActive = true;
            CacheDockRowMetrics(panelId);
        }

        UpdateDockerDropPreview(panelId, rootPt);
        e.Handled = true;
    }

    private void FinishDockerDrag()
    {
        if (_dockDragActive && _dockDropColumn >= -2 && _dockDragPanelId != null)
            CommitDockerDrop(_dockDragPanelId, _dockDropColumn, _dockDropRow);
        CancelDockerDrag();
    }

    private void CancelDockerDrag()
    {
        _dockDragPanelId = null;
        _dockDragActive = false;
        _dockDropColumn = -99;
        _dockDropRow = -1;
        _stickyTarget = null;
        _dockRowMetrics.Clear();
        _dockDropOverlay?.Clear();
    }

    private void CommitDockerDrop(string panelId, int columnIndex, int dropCode)
    {
        SaveWorkspaceLayoutFromUi();
        var layout = App.Config.WorkspaceLayout;

        if (dropCode < 0)
        {
            var mergeRow = -dropCode - 1;
            DockLayoutOps.ApplyDrop(layout, panelId, columnIndex, mergeRow, DockDropKind.MergeTab, mergeRow);
        }
        else
            DockLayoutOps.ApplyDrop(layout, panelId, columnIndex, dropCode, DockDropKind.InsertRow);

        foreach (var col in layout.RightColumns)
            DockLayoutOps.CompactTabGroups(col);
        DockLayoutOps.CompactTabGroups(layout.LeftColumn);

        RebuildDockers();
        App.Config.Save();
    }

    private void CacheDockRowMetrics(string movingId)
    {
        _dockRowMetrics.Clear();
        if (_dockDropOverlay == null) return;

        Grid? ColumnGrid(int col) => col < 0
            ? (_leftPanel is Border { Child: Grid g } ? g : null)
            : FindRightColumnGrid(col);

        foreach (var col in new[] { -1 }.Concat(Enumerable.Range(0, App.Config.WorkspaceLayout.RightColumns.Count)))
        {
            var grid = ColumnGrid(col);
            if (grid == null) continue;

            var layout = col < 0
                ? App.Config.WorkspaceLayout.LeftColumn
                : App.Config.WorkspaceLayout.RightColumns[col];

            var resolved = layout.ResolvedRows()
                .Where(r => r.PanelIds.Count > 1 || !r.PanelIds.Contains(movingId))
                .ToList();

            for (var i = 0; i < resolved.Count; i++)
            {
                var row = resolved[i];
                var control = FindRowControl(grid, row);
                if (control == null) continue;

                var bounds = control.Bounds;
                var matrix = control.TransformToVisual(_dockDropOverlay);
                if (matrix == null) continue;

                var topLeft = matrix.Value.Transform(new Point(0, 0));
                var rect = new Rect(topLeft, bounds.Size);
                if (rect.Width <= 0 || rect.Height <= 0) continue;

                var label = row.PanelIds.Count > 1
                    ? string.Join(" · ", row.PanelIds.Select(DockerTitle))
                    : DockerTitle(row.PanelIds[0]);

                _dockRowMetrics.Add(new DockRowMetric(col, i, i, rect, row.PanelIds.Count > 1, label));
            }
        }
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

    private void UpdateDockerDropPreview(string movingId, Point rootPt)
    {
        if (_dockDropOverlay == null)
            return;

        var target = ResolveDropTarget(rootPt);
        if (target == null)
        {
            _dockDropColumn = -99;
            _dockDropRow = -1;
            _stickyTarget = null;
            _dockDropOverlay.Clear();
            return;
        }

        if (_stickyTarget is { } sticky
            && target.ColumnIndex == sticky.ColumnIndex
            && target.RowCode == sticky.RowCode
            && Math.Abs(target.LineY - sticky.LineY) < DropHysteresisPx)
            target = sticky;
        else
            _stickyTarget = target;

        _dockDropColumn = target.ColumnIndex;
        _dockDropRow = target.RowCode;

        if (target.IsTabMerge)
        {
            _dockDropOverlay.ShowTabTarget(
                target.Bounds.X, target.Bounds.Y, target.Bounds.Width, DockDropOverlay.TabStripHeight);
        }
        else
        {
            _dockDropOverlay.ShowInsertLine(target.Bounds.X, target.LineY, target.Bounds.Width);
        }

        var columnName = target.ColumnIndex < 0 ? "Left" : "Right";
        var hint = target.IsTabMerge
            ? $"{columnName} — Add to tab: {target.Label}"
            : $"{columnName} — New row ({target.Label})";
        _dockDropOverlay.ShowHint(hint, rootPt.X, rootPt.Y);
    }

    private sealed record DropTargetState(
        int ColumnIndex,
        int RowCode,
        double LineY,
        Rect Bounds,
        bool IsTabMerge,
        string Label);

    private DropTargetState? ResolveDropTarget(Point rootPt)
    {
        if (_dockRowMetrics.Count == 0)
            return null;

        DockRowMetric? hit = null;
        foreach (var m in _dockRowMetrics)
        {
            if (!m.Bounds.Contains(rootPt)) continue;
            hit = m;
            break;
        }

        if (hit == null)
        {
            var inColumn = _dockRowMetrics
                .GroupBy(m => m.ColumnIndex)
                .OrderBy(g => DistanceToColumn(g.Key, rootPt.X))
                .FirstOrDefault();

            if (inColumn == null) return null;

            hit = rootPt.Y < inColumn.Min(m => m.Bounds.Top)
                ? inColumn.OrderBy(m => m.Bounds.Top).First()
                : inColumn.OrderByDescending(m => m.Bounds.Bottom).First();
        }

        if (hit.IsTabGroup && rootPt.Y < hit.Bounds.Top + DockDropOverlay.TabStripHeight)
            return new DropTargetState(hit.ColumnIndex, -(hit.RowIndex + 1), hit.Bounds.Top,
                hit.Bounds, true, hit.Label);

        var above = rootPt.Y < hit.Bounds.Top + hit.Bounds.Height * 0.5;
        var insertIdx = above ? hit.RowIndex : hit.RowIndex + 1;
        var lineY = above ? hit.Bounds.Top : hit.Bounds.Bottom;
        return new DropTargetState(hit.ColumnIndex, insertIdx, lineY, hit.Bounds, false, hit.Label);
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

    private static Control? FindRowControl(Grid hostGrid, ResolvedRow row)
    {
        foreach (var child in hostGrid.Children)
        {
            if (child is DockTabGroup tg && tg.PanelIds.SequenceEqual(row.PanelIds))
                return tg;

            if (child is Border b && b.Tag is string tag && row.PanelIds.Count == 1 && tag == row.PanelIds[0])
                return b;

            if (child is Grid inner)
            {
                foreach (var ic in inner.Children)
                {
                    if (ic is Border ib && ib.Tag is string it && row.PanelIds.Count == 1 && it == row.PanelIds[0])
                        return ib;
                }
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
