using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Docking;

/// <summary>Where a panel lives in the workspace layout.</summary>
public sealed record DockPlacement(
    DockColumnLayout Column,
    int ColumnIndex,
    int RowIndex,
    string RowKey,
    string? TabGroupKey,
    int IndexInTab)
{
    public bool IsTabMember => TabGroupKey != null && IndexInTab >= 0;
    public bool IsSoloRow => !IsTabMember && RowKey == TabGroupKey;
    public DockZone Zone => DockColumnIndices.ZoneOf(ColumnIndex);
}

public enum DockDropKind
{
    InsertRow,
    MergeTab,
    /// <summary>New dock column on left, right, or bottom edge.</summary>
    InsertDockColumn,
}

/// <summary>Layout mutations for docked panels (tabs, rows, columns).</summary>
public static class DockLayoutOps
{
    public static DockPlacement? FindPlacement(WorkspaceLayout layout, string panelId)
    {
        for (var i = 0; i < layout.LeftColumns.Count; i++)
        {
            var left = SearchColumn(layout.LeftColumns[i], DockColumnIndices.Left(i), panelId);
            if (left != null) return left;
        }

        for (var i = 0; i < layout.RightColumns.Count; i++)
        {
            var r = SearchColumn(layout.RightColumns[i], DockColumnIndices.Right(i), panelId);
            if (r != null) return r;
        }

        for (var i = 0; i < layout.BottomColumns.Count; i++)
        {
            var b = SearchColumn(layout.BottomColumns[i], DockColumnIndices.Bottom(i), panelId);
            if (b != null) return b;
        }

        return null;
    }

    private static DockPlacement? SearchColumn(DockColumnLayout col, int columnIndex, string panelId)
    {
        if (col.Rows is { Count: > 0 })
        {
            for (var ri = 0; ri < col.Rows.Count; ri++)
            {
                var row = col.Rows[ri];
                var ti = row.PanelIds.IndexOf(panelId);
                if (ti < 0) continue;
                return new DockPlacement(col, columnIndex, ri, row.PanelIds[0], null,
                    row.IsTabGroup ? ti : -1);
            }
            return null;
        }

        for (var i = 0; i < col.PanelIds.Count; i++)
        {
            var key = col.PanelIds[i];
            if (string.Equals(key, panelId, StringComparison.Ordinal))
                return new DockPlacement(col, columnIndex, i, key, null, -1);

            if (!col.TabGroups.TryGetValue(key, out var tab))
                continue;

            var idx = tab.PanelIds.IndexOf(panelId);
            if (idx >= 0)
                return new DockPlacement(col, columnIndex, i, key, key, idx);
        }

        return null;
    }

    public static DockColumnLayout GetColumn(WorkspaceLayout layout, int columnIndex)
    {
        var leftIdx = DockColumnIndices.TryParseLeft(columnIndex);
        if (leftIdx != null)
        {
            while (layout.LeftColumns.Count <= leftIdx.Value)
            {
                layout.LeftColumns.Add(new DockColumnLayout
                {
                    Id = $"left-{layout.LeftColumns.Count}"
                });
            }

            return layout.LeftColumns[leftIdx.Value];
        }

        var bottomIdx = DockColumnIndices.TryParseBottom(columnIndex);
        if (bottomIdx != null)
        {
            while (layout.BottomColumns.Count <= bottomIdx.Value)
            {
                layout.BottomColumns.Add(new DockColumnLayout
                {
                    Id = $"bottom-{layout.BottomColumns.Count}"
                });
            }

            return layout.BottomColumns[bottomIdx.Value];
        }

        var rightIdx = Math.Clamp(columnIndex, 0, Math.Max(0, layout.RightColumns.Count - 1));
        while (layout.RightColumns.Count <= rightIdx)
        {
            layout.RightColumns.Add(new DockColumnLayout
            {
                Id = $"right-{layout.RightColumns.Count}"
            });
        }

        return layout.RightColumns[rightIdx];
    }

    public static List<DockColumnLayout> ColumnsForZone(WorkspaceLayout layout, DockZone zone)
        => zone switch
        {
            DockZone.Left => layout.LeftColumns,
            DockZone.Right => layout.RightColumns,
            DockZone.Bottom => layout.BottomColumns,
            _ => layout.RightColumns
        };

    public static void RemoveFromAllColumns(WorkspaceLayout layout, string panelId)
        => layout.RemovePanel(panelId);

    public static void DockToColumn(WorkspaceLayout layout, string panelId, int columnIndex)
    {
        RemoveFromAllColumns(layout, panelId);
        layout.HiddenPanelIds.Remove(panelId);
        var col = GetColumn(layout, columnIndex);
        EnsureRowsModel(col);
        if (col.Rows!.Count == 0)
            col.Rows.Add(new DockRowLayout { PanelIds = [panelId] });
        else
            col.Rows[0].PanelIds.Add(panelId);

        if (layout.FloatingPanels.TryGetValue(panelId, out var f))
            f.IsFloating = false;
    }

    public static bool MovePanel(WorkspaceLayout layout, string panelId, int delta)
    {
        var p = FindPlacement(layout, panelId);
        if (p == null) return false;

        if (p.IsTabMember && p.Column.TabGroups.TryGetValue(p.TabGroupKey!, out var tab))
        {
            var target = p.IndexInTab + delta;
            if (target >= 0 && target < tab.PanelIds.Count)
            {
                tab.PanelIds.RemoveAt(p.IndexInTab);
                tab.PanelIds.Insert(target, panelId);
                return true;
            }

            var insertRow = p.RowIndex + (delta < 0 ? p.RowIndex : p.RowIndex + 1);
            ExtractToRow(layout, panelId, p.ColumnIndex, insertRow);
            return true;
        }

        var col = p.Column;
        EnsureRowsModel(col);
        var rowTarget = Math.Clamp(p.RowIndex + delta, 0, col.Rows!.Count - 1);
        if (rowTarget == p.RowIndex) return false;
        var row = col.Rows[rowTarget];
        col.Rows.RemoveAt(p.RowIndex);
        col.Rows.Insert(rowTarget, row);
        return true;
    }

    public static void ExtractToRow(WorkspaceLayout layout, string panelId, int columnIndex, int insertRowIndex)
    {
        RemoveFromAllColumns(layout, panelId);
        ApplyInsertRow(layout, panelId, columnIndex, insertRowIndex);
    }

    public static void EnsureRowsModel(DockColumnLayout col)
    {
        if (col.Rows != null)
            return;

        col.Rows = col.ResolvedRows().Select(r => new DockRowLayout
        {
            PanelIds = r.PanelIds.ToList(),
            Orientation = r.Orientation,
            ActiveIndex = r.ActiveTabIndex
        }).ToList();
        col.PanelIds = [];
        col.TabGroups.Clear();
    }

    public static void ApplyDrop(
        WorkspaceLayout layout,
        string panelId,
        int columnIndex,
        int insertRowIndex,
        DockDropKind kind,
        int mergeTabRowIndex = -1,
        IReadOnlyList<string>? mergeTargetRowPanelIds = null,
        int mergeTabInsertIndex = -1)
    {
        switch (kind)
        {
            case DockDropKind.InsertRow:
                ApplyInsertRow(layout, panelId, columnIndex, insertRowIndex);
                break;
            case DockDropKind.MergeTab:
                if (mergeTargetRowPanelIds is { Count: > 0 })
                    ApplyMergeTab(layout, panelId, columnIndex, mergeTargetRowPanelIds, mergeTabInsertIndex);
                else
                    ApplyMergeTab(layout, panelId, columnIndex, mergeTabRowIndex, mergeTabInsertIndex);
                break;
            case DockDropKind.InsertDockColumn:
                ApplyInsertDockColumn(layout, panelId, DockColumnIndices.ZoneOf(columnIndex), insertRowIndex);
                break;
        }
    }

    public static void ApplyInsertDockColumn(
        WorkspaceLayout layout,
        string panelId,
        DockZone zone,
        int insertColumnIndex)
    {
        PreparePanelForDock(layout, panelId);
        insertColumnIndex = Math.Max(0, insertColumnIndex);

        var newCol = new DockColumnLayout
        {
            Rows = [new DockRowLayout { PanelIds = [panelId] }]
        };

        var columns = ColumnsForZone(layout, zone);
        insertColumnIndex = Math.Clamp(insertColumnIndex, 0, columns.Count);
        columns.Insert(insertColumnIndex, newCol);

        var prefix = zone switch
        {
            DockZone.Left => "left",
            DockZone.Bottom => "bottom",
            _ => "right"
        };
        for (var i = 0; i < columns.Count; i++)
            columns[i].Id = $"{prefix}-{i}";
    }

    public static void ApplyInsertRow(WorkspaceLayout layout, string panelId, int columnIndex, int insertRowIndex)
    {
        var source = FindPlacement(layout, panelId);
        PreparePanelForDock(layout, panelId);
        if (source is { ColumnIndex: var srcCol, RowIndex: var srcRow }
            && srcCol == columnIndex
            && srcRow < insertRowIndex)
        {
            insertRowIndex--;
        }

        var col = GetColumn(layout, columnIndex);
        EnsureRowsModel(col);
        insertRowIndex = Math.Clamp(insertRowIndex, 0, col.Rows!.Count);
        col.Rows.Insert(insertRowIndex, new DockRowLayout { PanelIds = [panelId] });
    }

    public static void ApplyMergeTab(
        WorkspaceLayout layout,
        string panelId,
        int columnIndex,
        int mergeTabRowIndex,
        int insertIndex = -1)
    {
        var source = FindPlacement(layout, panelId);
        if (source is { ColumnIndex: var sc, RowIndex: var sr, IndexInTab: >= 0 }
            && sc == columnIndex
            && sr == mergeTabRowIndex
            && insertIndex > source.IndexInTab)
        {
            insertIndex--;
        }

        PreparePanelForDock(layout, panelId);
        var col = GetColumn(layout, columnIndex);
        EnsureRowsModel(col);

        if (mergeTabRowIndex < 0 || mergeTabRowIndex >= col.Rows!.Count)
            return;

        MergePanelIntoRow(col.Rows[mergeTabRowIndex], panelId, insertIndex);
    }

    /// <summary>Merge by row identity — survives row-index shifts after removing the dragged panel.</summary>
    public static void ApplyMergeTab(
        WorkspaceLayout layout,
        string panelId,
        int columnIndex,
        IReadOnlyList<string> targetRowPanelIds,
        int insertIndex = -1)
    {
        var source = FindPlacement(layout, panelId);
        if (source is { IndexInTab: >= 0 } && targetRowPanelIds.Contains(panelId) && insertIndex > source.IndexInTab)
            insertIndex--;

        PreparePanelForDock(layout, panelId);
        var col = GetColumn(layout, columnIndex);
        EnsureRowsModel(col);

        var row = FindRowForMerge(col, targetRowPanelIds, panelId);
        if (row == null)
            return;

        MergePanelIntoRow(row, panelId, insertIndex);
    }

    /// <summary>Insert index from pointer X among tab bounds (left half → before tab).</summary>
    public static int ResolveTabInsertIndex(IReadOnlyList<(double Left, double Right)> tabs, double pointerX)
    {
        for (var i = 0; i < tabs.Count; i++)
        {
            if (pointerX < (tabs[i].Left + tabs[i].Right) * 0.5)
                return i;
        }

        return tabs.Count;
    }

    private static DockRowLayout? FindRowForMerge(
        DockColumnLayout col,
        IReadOnlyList<string> targetRowPanelIds,
        string movingPanelId)
    {
        var exact = col.Rows!.FirstOrDefault(r => RowPanelIdsMatch(r.PanelIds, targetRowPanelIds));
        if (exact != null)
            return exact;

        return col.Rows!.FirstOrDefault(r =>
            targetRowPanelIds.All(id => id == movingPanelId || r.PanelIds.Contains(id))
            && r.PanelIds.All(id => targetRowPanelIds.Contains(id)));
    }

    private static void MergePanelIntoRow(DockRowLayout row, string panelId, int insertIndex)
    {
        if (row.Orientation == DockOrientation.Horizontal)
            row.Orientation = DockOrientation.Vertical;

        if (row.PanelIds.Count == 1 && string.Equals(row.PanelIds[0], panelId, StringComparison.Ordinal))
            return;

        if (!row.PanelIds.Contains(panelId))
        {
            if (insertIndex >= 0 && insertIndex <= row.PanelIds.Count)
                row.PanelIds.Insert(insertIndex, panelId);
            else
                row.PanelIds.Add(panelId);
        }

        row.ActiveIndex = row.PanelIds.IndexOf(panelId);
    }

    private static bool RowPanelIdsMatch(IReadOnlyList<string> a, IReadOnlyList<string> b)
        => a.SequenceEqual(b);

    private static void PreparePanelForDock(WorkspaceLayout layout, string panelId)
    {
        RemoveFromAllColumns(layout, panelId);
        layout.HiddenPanelIds.Remove(panelId);
        if (layout.FloatingPanels.TryGetValue(panelId, out var f))
            f.IsFloating = false;
    }

    /// <summary>No-op: panels stay in tab groups even when alone ().</summary>
    public static void CompactTabGroups(DockColumnLayout col) { }
}
