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
}

/// <summary>Layout mutations for docked panels (tabs, rows, columns).</summary>
public static class DockLayoutOps
{
    public static DockPlacement? FindPlacement(WorkspaceLayout layout, string panelId)
    {
        var left = SearchColumn(layout.LeftColumn, -1, panelId);
        if (left != null) return left;

        for (var i = 0; i < layout.RightColumns.Count; i++)
        {
            var r = SearchColumn(layout.RightColumns[i], i, panelId);
            if (r != null) return r;
        }

        return SearchColumn(layout.BottomColumn, -2, panelId);
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
        if (columnIndex <= -2) return layout.BottomColumn;
        if (columnIndex < 0) return layout.LeftColumn;
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

    public static void ApplyDrop(
        WorkspaceLayout layout,
        string panelId,
        int columnIndex,
        int insertRowIndex,
        DockDropKind kind,
        int mergeTabRowIndex = -1)
    {
        RemoveFromAllColumns(layout, panelId);
        layout.HiddenPanelIds.Remove(panelId);
        if (layout.FloatingPanels.TryGetValue(panelId, out var f))
            f.IsFloating = false;

        var col = GetColumn(layout, columnIndex);

        if (kind == DockDropKind.MergeTab && mergeTabRowIndex >= 0
            && (uint)mergeTabRowIndex < (uint)col.PanelIds.Count)
        {
            var rowKey = col.PanelIds[mergeTabRowIndex];
            if (col.TabGroups.TryGetValue(rowKey, out var existing))
            {
                if (!existing.PanelIds.Contains(panelId))
                    existing.PanelIds.Add(panelId);
                existing.ActiveIndex = existing.PanelIds.IndexOf(panelId);
                return;
            }

            var tabKey = "tab:" + Guid.NewGuid().ToString("N")[..8];
            col.TabGroups[tabKey] = new TabGroupLayout
            {
                PanelIds = [rowKey, panelId],
                ActiveIndex = 1
            };
            col.PanelIds[mergeTabRowIndex] = tabKey;
            return;
        }

        insertRowIndex = Math.Clamp(insertRowIndex, 0, col.PanelIds.Count);
        col.PanelIds.Insert(insertRowIndex, panelId);
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
