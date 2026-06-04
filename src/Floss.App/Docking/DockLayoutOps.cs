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
}

public enum DockDropKind
{
    InsertRow,
    MergeTab,
    /// <summary>New dock column beside the canvas (left or right stack).</summary>
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
            var r = SearchColumn(layout.RightColumns[i], i, panelId);
            if (r != null) return r;
        }

        return SearchColumn(layout.BottomColumn, DockColumnIndices.Bottom, panelId);
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
        if (columnIndex == DockColumnIndices.Bottom)
            return layout.BottomColumn;

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

        return layout.RightColumns[Math.Clamp(columnIndex, 0, layout.RightColumns.Count - 1)];
    }

    public static void RemoveFromAllColumns(WorkspaceLayout layout, string panelId)
        => layout.RemovePanel(panelId);

    public static void DockToColumn(WorkspaceLayout layout, string panelId, int columnIndex)
    {
        RemoveFromAllColumns(layout, panelId);
        layout.HiddenPanelIds.Remove(panelId);
        GetColumn(layout, columnIndex).PanelIds.Add(panelId);
        if (layout.FloatingPanels.TryGetValue(panelId, out var f))
            f.IsFloating = false;
    }

    public static bool MovePanel(WorkspaceLayout layout, string panelId, int delta)
    {
        var p = FindPlacement(layout, panelId);
        if (p == null) return false;

        if (p.IsTabMember)
        {
            var tab = p.Column.TabGroups[p.TabGroupKey!];
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
        var rowTarget = Math.Clamp(p.RowIndex + delta, 0, col.PanelIds.Count - 1);
        if (rowTarget == p.RowIndex) return false;
        col.PanelIds.RemoveAt(p.RowIndex);
        col.PanelIds.Insert(rowTarget, p.RowKey);
        return true;
    }

    public static void ExtractToRow(WorkspaceLayout layout, string panelId, int columnIndex, int insertRowIndex)
    {
        RemoveFromAllColumns(layout, panelId);
        var col = GetColumn(layout, columnIndex);
        insertRowIndex = Math.Clamp(insertRowIndex, 0, col.PanelIds.Count);
        col.PanelIds.Insert(insertRowIndex, panelId);
    }

    public static void EnsureRowsModel(DockColumnLayout col)
    {
        if (col.Rows is { Count: > 0 })
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
        string? anchorPanelId = null,
        bool insertBeforeAnchor = true)
    {
        switch (kind)
        {
            case DockDropKind.InsertRow:
                ApplyInsertRow(layout, panelId, columnIndex, insertRowIndex);
                break;
            case DockDropKind.MergeTab:
                ApplyMergeTab(layout, panelId, columnIndex, mergeTabRowIndex);
                break;
            case DockDropKind.InsertDockColumn:
                ApplyInsertDockColumn(layout, panelId, columnIndex < 0, insertRowIndex);
                break;
        }
    }

    /// <param name="insertColumnIndex">Index in <see cref="WorkspaceLayout.LeftColumns"/> or <see cref="WorkspaceLayout.RightColumns"/>.</param>
    public static void ApplyInsertDockColumn(
        WorkspaceLayout layout,
        string panelId,
        bool onLeftSide,
        int insertColumnIndex)
    {
        PreparePanelForDock(layout, panelId);
        insertColumnIndex = Math.Max(0, insertColumnIndex);

        var newCol = new DockColumnLayout
        {
            Rows = [new DockRowLayout { PanelIds = [panelId] }]
        };

        if (onLeftSide)
        {
            insertColumnIndex = Math.Clamp(insertColumnIndex, 0, layout.LeftColumns.Count);
            layout.LeftColumns.Insert(insertColumnIndex, newCol);
            for (var i = 0; i < layout.LeftColumns.Count; i++)
                layout.LeftColumns[i].Id = $"left-{i}";
            return;
        }

        insertColumnIndex = Math.Clamp(insertColumnIndex, 0, layout.RightColumns.Count);
        layout.RightColumns.Insert(insertColumnIndex, newCol);
        for (var i = 0; i < layout.RightColumns.Count; i++)
            layout.RightColumns[i].Id = $"right-{i}";
    }

    public static void ApplyInsertRow(WorkspaceLayout layout, string panelId, int columnIndex, int insertRowIndex)
    {
        PreparePanelForDock(layout, panelId);
        var col = GetColumn(layout, columnIndex);
        EnsureRowsModel(col);
        insertRowIndex = Math.Clamp(insertRowIndex, 0, col.Rows!.Count);
        col.Rows.Insert(insertRowIndex, new DockRowLayout { PanelIds = [panelId] });
    }

    public static void ApplyMergeTab(WorkspaceLayout layout, string panelId, int columnIndex, int mergeTabRowIndex)
    {
        PreparePanelForDock(layout, panelId);
        var col = GetColumn(layout, columnIndex);
        EnsureRowsModel(col);

        if (mergeTabRowIndex < 0 || mergeTabRowIndex >= col.Rows!.Count)
            return;

        var row = col.Rows[mergeTabRowIndex];
        if (row.Orientation == DockOrientation.Horizontal)
            row.Orientation = DockOrientation.Vertical;

        if (row.PanelIds.Count == 1 && string.Equals(row.PanelIds[0], panelId, StringComparison.Ordinal))
            return;

        if (!row.PanelIds.Contains(panelId))
            row.PanelIds.Add(panelId);
        row.ActiveIndex = row.PanelIds.IndexOf(panelId);
    }

    private static void PreparePanelForDock(WorkspaceLayout layout, string panelId)
    {
        RemoveFromAllColumns(layout, panelId);
        layout.HiddenPanelIds.Remove(panelId);
        if (layout.FloatingPanels.TryGetValue(panelId, out var f))
            f.IsFloating = false;
    }

    /// <summary>Collapse single-panel tabs back to solo row keys.</summary>
    public static void CompactTabGroups(DockColumnLayout col)
    {
        foreach (var (key, tab) in col.TabGroups.ToList())
        {
            if (tab.PanelIds.Count != 1) continue;
            var solo = tab.PanelIds[0];
            var idx = col.PanelIds.IndexOf(key);
            if (idx >= 0)
                col.PanelIds[idx] = solo;
            col.TabGroups.Remove(key);
        }
    }
}
