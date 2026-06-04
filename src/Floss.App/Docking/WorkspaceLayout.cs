using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Floss.App.Docking;

/// <summary>
/// Persistable workspace layout configuration.
/// </summary>
public sealed class WorkspaceLayout
{
    /// <summary>3 = tabbed dock columns. See notes/ui-visual-refresh.md.</summary>
    public int LayoutVersion { get; set; } = 3;

    /// <summary>Width of the left dock column (brush library, etc.), not the 48px tool rail.</summary>
    public double LeftRailWidth { get; set; } = 280;

    public double RightPanelWidth { get; set; } = 320;
    public double RightDockSplit { get; set; } = 0.5;
    public double BottomDockHeight { get; set; } = 320;

    [JsonInclude]
    public List<DockColumnLayout> RightColumns { get; set; } =
    [
        new()
        {
            Id = "right-0",
            PanelIds = ["tab:color", "tab:layers"],
            TabGroups = new Dictionary<string, TabGroupLayout>
            {
                ["tab:color"] = new() { PanelIds = ["color", "color-slider", "layer-properties"], ActiveIndex = 0 },
                ["tab:layers"] = new() { PanelIds = ["layers"], ActiveIndex = 0 }
            }
        }
    ];

    [JsonInclude]
    public DockColumnLayout LeftColumn { get; set; } = new()
    {
        Id = "left",
        PanelIds = ["tab:left"],
        TabGroups = new Dictionary<string, TabGroupLayout>
        {
            ["tab:left"] = new() { PanelIds = ["brush", "tool-properties"], ActiveIndex = 0 }
        }
    };

    [JsonInclude]
    public DockColumnLayout BottomColumn { get; set; } = new() { Id = "bottom", PanelIds = ["node-graph"] };

    [JsonInclude]
    [JsonPropertyName("PanelHeights")] // Backward compat name; values now represent proportions
    public Dictionary<string, double> PanelProportions { get; set; } = new();

    [JsonInclude]
    public Dictionary<string, FloatingPanelState> FloatingPanels { get; set; } = new();

    [JsonInclude]
    [JsonPropertyName("HiddenDockers")]
    public HashSet<string> HiddenPanelIds { get; set; } = ["color-slider"];

    /// <summary>Per-panel content state (scroll position, selected category, etc.).</summary>
    [JsonInclude]
    public Dictionary<string, object?> PanelContentState { get; set; } = new();

    public static WorkspaceLayout CreateDefault() => new();

    public WorkspaceLayout Clone()
        => JsonSerializer.Deserialize<WorkspaceLayout>(
            JsonSerializer.Serialize(this)) ?? CreateDefault();

    /// <summary>
    /// Ensure all known panel ids are present in some column and prune unknown ids.
    /// </summary>
    public void Normalize(IReadOnlyList<string> knownIds)
    {
        // Migrate: brush is now a first-class docked panel, not a popup-only panel
        HiddenPanelIds.Remove("brush");
        HiddenPanelIds.Remove("tool-properties");
        HiddenPanelIds.Remove("layer-properties");

        // Tools live on the fixed left rail (v2), not as a docked panel
        HiddenPanelIds.Remove("tools");
        LeftColumn.PanelIds.Remove("tools");

        MigrateToWideDockLayoutV2();
        MigrateToTabbedDockV3();

        // Migrate old default or missing panels to the v1 right-stack layout.
        if (LayoutVersion < 2 &&
            RightColumns.Count == 1 &&
            (RightColumns[0].PanelIds.SequenceEqual(["brush", "color", "color-slider", "layers"]) ||
             RightColumns[0].PanelIds.SequenceEqual(["color", "brush", "layers"])))
        {
            RightColumns[0].PanelIds = ["color", "color-slider", "layer-properties", "brush", "tool-properties", "layers"];
        }

        if (RightColumns.Count < 1)
            RightColumns = CreateDefault().RightColumns;

        BottomColumn ??= CreateDefault().BottomColumn;

        var known = new HashSet<string>(knownIds);

        // Prune unknown panel IDs (keep tab-group row keys — they are not registry panel ids).
        foreach (var col in RightColumns)
        {
            col.PanelIds = col.PanelIds.Where(id => IsKnownColumnPanelId(id, col, known)).Distinct().ToList();
            col.RepairTabGroupPanelIds();
        }
        LeftColumn.PanelIds = LeftColumn.PanelIds
            .Where(id => IsKnownColumnPanelId(id, LeftColumn, known))
            .Distinct()
            .ToList();
        LeftColumn.RepairTabGroupPanelIds();
        BottomColumn.PanelIds = BottomColumn.PanelIds
            .Where(id => IsKnownColumnPanelId(id, BottomColumn, known))
            .Distinct()
            .ToList();
        BottomColumn.RepairTabGroupPanelIds();

        // Ensure every known panel is placed somewhere
        foreach (var id in known)
        {
            if (id == "tools")
                continue; // fixed tool rail — not a dock column panel
            if (RightColumns.Any(c => c.ContainsPanel(id))) continue;
            if (LeftColumn.ContainsPanel(id)) continue;
            if (BottomColumn.ContainsPanel(id)) continue;
            if (FloatingPanels.ContainsKey(id)) continue;
            if (HiddenPanelIds.Contains(id)) continue;

            var def = PanelRegistry.Get(id);
            var zone = def?.DefaultZone ?? "right-0";

            if (zone == "left")
                LeftColumn.PanelIds.Add(id);
            else if (zone == "bottom")
                BottomColumn.PanelIds.Add(id);
            else if (zone.StartsWith("right-") && int.TryParse(zone.AsSpan(6), out var rightIdx)
                     && rightIdx < RightColumns.Count)
                RightColumns[rightIdx].PanelIds.Add(id);
            else
                RightColumns[0].PanelIds.Add(id);
        }

        // Deduplicate: remove from non-default columns
        DeduplicateColumns(known);
    }

    private static bool IsKnownColumnPanelId(string id, DockColumnLayout col, HashSet<string> known)
        => known.Contains(id) || col.TabGroups.ContainsKey(id);

    private void MigrateToWideDockLayoutV2()
    {
        if (LayoutVersion >= 2)
            return;

        if (RightPanelWidth < 280)
            RightPanelWidth = 320;

        if (LeftColumn.PanelIds.Count == 0 && RightColumns.Count > 0)
        {
            var right = RightColumns[0].PanelIds;
            if (right.Remove("brush"))
                LeftColumn.PanelIds.Add("brush");
            if (right.Remove("tool-properties"))
                LeftColumn.PanelIds.Add("tool-properties");
        }

        if (LeftColumn.PanelIds.Count > 0 && LeftRailWidth < 120)
            LeftRailWidth = 280;

        LayoutVersion = 2;
    }

    private void MigrateToTabbedDockV3()
    {
        if (LayoutVersion >= 3)
            return;

        static bool HasTabGroups(DockColumnLayout col) => col.TabGroups.Count > 0;

        if (!HasTabGroups(LeftColumn))
        {
            var leftPanels = LeftColumn.PanelIds
                .Where(id => id is "brush" or "tool-properties")
                .Distinct()
                .ToList();
            if (leftPanels.Count == 0)
                leftPanels = ["brush", "tool-properties"];

            LeftColumn.PanelIds = ["tab:left"];
            LeftColumn.TabGroups = new Dictionary<string, TabGroupLayout>
            {
                ["tab:left"] = new() { PanelIds = leftPanels, ActiveIndex = 0 }
            };
        }

        if (RightColumns.Count > 0 && !HasTabGroups(RightColumns[0]))
        {
            var right = RightColumns[0];
            var colorStack = right.PanelIds
                .Where(id => id is "color" or "color-slider" or "layer-properties")
                .Distinct()
                .ToList();
            if (colorStack.Count == 0)
                colorStack = ["color", "color-slider", "layer-properties"];

            var layerPanels = right.PanelIds.Contains("layers")
                ? new List<string> { "layers" }
                : new List<string>();

            right.PanelIds = layerPanels.Count > 0
                ? ["tab:color", "tab:layers"]
                : ["tab:color"];
            right.TabGroups = new Dictionary<string, TabGroupLayout>
            {
                ["tab:color"] = new() { PanelIds = colorStack, ActiveIndex = 0 }
            };
            if (layerPanels.Count > 0)
                right.TabGroups["tab:layers"] = new() { PanelIds = layerPanels, ActiveIndex = 0 };
        }

        LayoutVersion = 3;
    }

    /// <summary>
    /// Returns the first panel ID that is referenced in a TabGroup but not accessible
    /// through PanelIds or Rows (orphaned). Returns null if layout is valid.
    /// </summary>
    public string? FindOrphanedPanel()
    {
        var allColumns = new List<DockColumnLayout> { LeftColumn };
        allColumns.AddRange(RightColumns);
        allColumns.Add(BottomColumn);

        foreach (var col in allColumns)
        {
            // Collect all panel IDs reachable through PanelIds + TabGroups resolution
            var reachable = new HashSet<string>();
            var resolved = col.ResolvedRows();
            foreach (var row in resolved)
                foreach (var pid in row.PanelIds)
                    reachable.Add(pid);

            // Check TabGroups for panels not in reachable set
            foreach (var (key, tab) in col.TabGroups)
            {
                foreach (var pid in tab.PanelIds)
                {
                    if (!reachable.Contains(pid))
                        return pid; // orphaned!
                }
            }
        }

        return null; // Valid
    }

    private void DeduplicateColumns(HashSet<string> known)
    {
        foreach (var id in known)
        {
            var inRight = RightColumns.Where(c => c.ContainsPanel(id)).ToList();
            var inLeft = LeftColumn.ContainsPanel(id);
            var inBottom = BottomColumn.ContainsPanel(id);
            if (inRight.Count + (inLeft ? 1 : 0) + (inBottom ? 1 : 0) <= 1) continue;

            var def = PanelRegistry.Get(id);
            var zone = def?.DefaultZone ?? "right-0";

            if (zone == "bottom" && inBottom) { foreach (var c in inRight) c.PanelIds.Remove(id); if (inLeft) LeftColumn.PanelIds.Remove(id); }
            else if (zone == "left" && inLeft) { foreach (var c in inRight) c.PanelIds.Remove(id); if (inBottom) BottomColumn.PanelIds.Remove(id); }
            else
            {
                // Keep in first matching RightColumn; remove from all others
                var keepCol = inRight.FirstOrDefault();
                if (keepCol == null)
                {
                    // Panel is in both left and bottom — keep in left, remove from bottom
                    if (inLeft && inBottom) BottomColumn.PanelIds.Remove(id);
                    // Panel is only in left but doesn't belong there — move to right
                    else if (inLeft && !inRight.Any() && !inBottom)
                    {
                        LeftColumn.PanelIds.Remove(id);
                        RightColumns[0].PanelIds.Add(id);
                    }
                    // Panel is only in bottom but doesn't belong there — move to right
                    else if (inBottom && !inRight.Any() && !inLeft)
                    {
                        BottomColumn.PanelIds.Remove(id);
                        RightColumns[0].PanelIds.Add(id);
                    }
                }
                else
                {
                    // Remove from non-RightColumn locations
                    if (inLeft) LeftColumn.PanelIds.Remove(id);
                    if (inBottom) BottomColumn.PanelIds.Remove(id);
                    foreach (var c in inRight)
                        if (c != keepCol) c.PanelIds.Remove(id);
                }
            }
        }
    }

    public (int ColumnIndex, int PanelIndex)? FindPanel(string id)
    {
        var idx = LeftColumn.PanelIds.IndexOf(id);
        if (idx >= 0) return (-1, idx);

        idx = BottomColumn.PanelIds.IndexOf(id);
        if (idx >= 0) return (-2, idx);

        for (var i = 0; i < RightColumns.Count; i++)
        {
            idx = RightColumns[i].PanelIds.IndexOf(id);
            if (idx >= 0) return (i, idx);
        }

        return null;
    }

    public void RemovePanel(string id)
    {
        LeftColumn.RemovePanel(id);
        BottomColumn.RemovePanel(id);
        foreach (var col in RightColumns)
            col.RemovePanel(id);
    }

    public bool IsFloating(string id)
        => FloatingPanels.TryGetValue(id, out var s) && s.IsFloating;

    public bool IsVisible(string id)
        => !HiddenPanelIds.Contains(id) && !IsFloating(id);
}

/// <summary>
/// A single row in a dock column. One PanelId = solo panel, multiple = tab group.
/// When Orientation is Horizontal, children are placed side-by-side within the row.
/// Modeled after Dock library's recursive IDock containers.
/// </summary>
public sealed class DockRowLayout
{
    public List<string> PanelIds { get; set; } = [];

    public DockOrientation Orientation { get; set; } = DockOrientation.Vertical;

    public bool IsTabGroup => Orientation == DockOrientation.Vertical && PanelIds.Count > 1;

    /// <summary>Active panel index in tab groups (0 by default).</summary>
    public int ActiveIndex { get; set; }
}

public enum DockOrientation
{
    Vertical,
    Horizontal
}

/// <summary>
/// Result of resolving column rows: panel IDs + layout orientation.
/// </summary>
public sealed class ResolvedRow
{
    public IReadOnlyList<string> PanelIds { get; init; } = [];
    public DockOrientation Orientation { get; init; } = DockOrientation.Vertical;
    public int ActiveTabIndex { get; init; }
}

/// <summary>
/// Metadata for a tab group. Multiple panels share one row with tab strip.
/// </summary>
public sealed class TabGroupLayout
{
    public List<string> PanelIds { get; set; } = [];
    public int ActiveIndex { get; set; }
}

public sealed class DockColumnLayout
{
    public string Id { get; set; } = "";

    [JsonPropertyName("Panels")]
    public List<string> PanelIds { get; set; } = [];

    /// <summary>
    /// Optional tab groups. Key is any unique string (e.g. "tab:0").
    /// The key appears in PanelIds to mark the row; the grouped panels are in the value list.
    /// </summary>
    public Dictionary<string, TabGroupLayout> TabGroups { get; set; } = new();

    /// <summary>
    /// When non-empty, replaces the flat PanelIds/TabGroups layout with explicit row
    /// definitions that support horizontal orientation for side-by-side docking.
    /// </summary>
    public List<DockRowLayout>? Rows { get; set; }

    public bool ContainsPanel(string id)
    {
        if (Rows != null) return Rows.Any(r => r.PanelIds.Contains(id));
        return PanelIds.Contains(id) || TabGroups.Values.Any(t => t.PanelIds.Contains(id));
    }

    /// <summary>
    /// Tab groups are referenced by keys in <see cref="PanelIds"/> (e.g. "tab:left").
    /// Re-insert any keys present in <see cref="TabGroups"/> but missing from the column list.
    /// </summary>
    public void RepairTabGroupPanelIds()
    {
        if (Rows is { Count: > 0 })
            return;

        foreach (var key in TabGroups.Keys)
        {
            if (!PanelIds.Contains(key))
                PanelIds.Add(key);
        }
    }

    public void RemovePanel(string id)
    {
        if (Rows != null)
        {
            foreach (var row in Rows) row.PanelIds.Remove(id);
            Rows.RemoveAll(r => r.PanelIds.Count == 0);
            return;
        }

        PanelIds.Remove(id);
        foreach (var (key, tab) in TabGroups.ToList())
        {
            tab.PanelIds.Remove(id);
            if (tab.PanelIds.Count == 0)
            {
                PanelIds.Remove(key);
                TabGroups.Remove(key);
            }
            else if (tab.PanelIds.Count == 1)
            {
                var soloId = tab.PanelIds[0];
                var idx = PanelIds.IndexOf(key);
                if (idx >= 0) PanelIds[idx] = soloId;
                else PanelIds.Add(soloId); // Key not in PanelIds — add solo directly
                TabGroups.Remove(key);
            }
        }
    }

    /// <summary>
    /// Returns the effective row layout. Uses Rows if populated, otherwise
    /// resolves from flat PanelIds/TabGroups.
    /// </summary>
    public IReadOnlyList<ResolvedRow> ResolvedRows()
    {
        var result = new List<ResolvedRow>();

        if (Rows is { Count: > 0 })
        {
            foreach (var row in Rows)
            {
                result.Add(new ResolvedRow
                {
                    PanelIds = row.PanelIds,
                    Orientation = row.Orientation,
                    ActiveTabIndex = row.ActiveIndex
                });
            }
            return result;
        }

        // Flat PanelIds + TabGroups fallback — all rows are vertical
        foreach (var id in PanelIds)
        {
            if (TabGroups.TryGetValue(id, out var tab))
            {
                result.Add(new ResolvedRow
                {
                    PanelIds = tab.PanelIds,
                    Orientation = DockOrientation.Vertical,
                    ActiveTabIndex = tab.ActiveIndex
                });
            }
            else
            {
                result.Add(new ResolvedRow
                {
                    PanelIds = [id],
                    Orientation = DockOrientation.Vertical,
                    ActiveTabIndex = 0
                });
            }
        }
        return result;
    }
}

public sealed class FloatingPanelState
{
    public bool IsFloating { get; set; }
    public double X { get; set; } = 120;
    public double Y { get; set; } = 120;
    public double Width { get; set; } = 320;
    public double Height { get; set; } = 480;
}

public sealed class WorkspacePreset
{
    public string Name { get; set; } = "";
    public WorkspaceLayout Layout { get; set; } = WorkspaceLayout.CreateDefault();
}
