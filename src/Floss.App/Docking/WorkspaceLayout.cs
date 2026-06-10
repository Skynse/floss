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
    /// <summary>8 = repair solo Rows layouts into tab strips. 7 = compact flat panel stacks.</summary>
    public int LayoutVersion { get; set; } = 8;

    /// <summary>Width of the left dock column (brush library, etc.).</summary>
    public double LeftRailWidth { get; set; } = 280;

    public double RightPanelWidth { get; set; } = 320;
    public double RightDockSplit { get; set; } = 0.5;
    public double LeftDockSplit { get; set; } = 0.5;
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
    public List<DockColumnLayout> LeftColumns { get; set; } =
    [
        new()
        {
            Id = "left-0",
            PanelIds = ["tab:left"],
            TabGroups = new Dictionary<string, TabGroupLayout>
            {
                ["tab:left"] = new() { PanelIds = ["tools", "brush", "tool-properties"], ActiveIndex = 1 }
            }
        }
    ];

    /// <summary>Legacy single left column in saved layouts.</summary>
    [JsonPropertyName("LeftColumn")]
    public DockColumnLayout? LeftColumnImport
    {
        set
        {
            if (value == null) return;
            if (LeftColumns.Count == 0)
                LeftColumns.Add(value);
            else
                LeftColumns[0] = value;
        }
    }

    [JsonIgnore]
    public DockColumnLayout LeftColumn
    {
        get => LeftColumns[0];
        set => LeftColumns[0] = value;
    }

    [JsonInclude]
    public List<DockColumnLayout> BottomColumns { get; set; } =
    [
        new() { Id = "bottom-0", PanelIds = ["node-graph"] }
    ];

    [JsonPropertyName("BottomColumn")]
    public DockColumnLayout? BottomColumnImport
    {
        set
        {
            if (value == null) return;
            if (BottomColumns.Count == 0)
                BottomColumns.Add(value);
            else
                BottomColumns[0] = value;
        }
    }

    [JsonIgnore]
    public DockColumnLayout BottomColumn
    {
        get => BottomColumns[0];
        set => BottomColumns[0] = value;
    }

    /// <summary>Relative sizes of dock columns on a side (key = column Id, e.g. left-0).</summary>
    [JsonInclude]
    public Dictionary<string, double> ColumnProportions { get; set; } = new();

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

    public static WorkspaceLayout CreateDefault()
    {
        var layout = BundledWorkspaceLayouts.TryLoad("workspace-default.json") ?? new WorkspaceLayout();
        if (PanelRegistry.AllIds.Count > 0)
            layout.Normalize(PanelRegistry.AllIds);
        return layout;
    }

    public WorkspaceLayout Clone()
        => JsonSerializer.Deserialize<WorkspaceLayout>(
            JsonSerializer.Serialize(this)) ?? CreateDefault();

    /// <summary>
    /// Ensure all known panel ids are present in some column and prune unknown ids.
    /// </summary>
    /// <summary>
    /// Re-sync tab group keys with column rows and compact broken solo-row layouts.
    /// Safe to call before panel registry is populated (e.g. on config load).
    /// </summary>
    public void RepairLayoutIntegrity()
    {
        MigrateColorSlidersPanelId();
        MigrateStrayPanelIdsIntoRows();
        PurgeStaleFlatLayoutWhenRowsAuthoritative();

        foreach (var col in LeftColumns.Concat(RightColumns).Concat(BottomColumns))
            col.RepairTabGroupPanelIds();

        PurgeDetachedTabGroups();
        SanitizeOrphanedTabGroups();
    }

    /// <summary>Remove tab groups whose row key is not listed in <see cref="DockColumnLayout.PanelIds"/>.</summary>
    private void PurgeDetachedTabGroups()
    {
        foreach (var col in LeftColumns.Concat(RightColumns).Concat(BottomColumns))
        {
            if (col.Rows is { Count: > 0 })
                continue;

            foreach (var key in col.TabGroups.Keys.ToList())
            {
                if (!col.PanelIds.Contains(key))
                    col.TabGroups.Remove(key);
            }
        }
    }

    /// <summary>
    /// Drop tab-group members that are not reflected in resolved rows (stale saves).
    /// Does not reshape user row layouts — that only runs in version migrations.
    /// </summary>
    public void SanitizeOrphanedTabGroups()
    {
        PurgeStaleFlatLayoutWhenRowsAuthoritative();

        foreach (var col in LeftColumns.Concat(RightColumns).Concat(BottomColumns))
        {
            if (col.Rows is { Count: > 0 })
                continue;

            var reachable = new HashSet<string>(StringComparer.Ordinal);
            foreach (var row in col.ResolvedRows())
            {
                foreach (var pid in row.PanelIds)
                    reachable.Add(pid);
            }

            foreach (var (key, tab) in col.TabGroups.ToList())
            {
                for (var i = tab.PanelIds.Count - 1; i >= 0; i--)
                {
                    if (!reachable.Contains(tab.PanelIds[i]))
                        tab.PanelIds.RemoveAt(i);
                }

                if (tab.PanelIds.Count == 0)
                {
                    col.TabGroups.Remove(key);
                    col.PanelIds.Remove(key);
                }
            }
        }
    }

    /// <summary>
    /// When <see cref="DockColumnLayout.Rows"/> is authoritative, legacy flat
    /// <see cref="DockColumnLayout.PanelIds"/> / <see cref="DockColumnLayout.TabGroups"/>
    /// entries from older saves must not participate in orphan detection.
    /// </summary>
    private void PurgeStaleFlatLayoutWhenRowsAuthoritative()
    {
        foreach (var col in LeftColumns.Concat(RightColumns).Concat(BottomColumns))
        {
            if (col.Rows is not { Count: > 0 })
                continue;
            col.TabGroups.Clear();
            col.PanelIds.Clear();
        }
    }

    private void MigrateColorSlidersPanelId()
    {
        const string oldId = "color-sliders";
        const string newId = "color-slider";

        if (HiddenPanelIds.Remove(oldId))
            HiddenPanelIds.Add(newId);

        if (FloatingPanels.Remove(oldId, out var floating))
            FloatingPanels.TryAdd(newId, floating);

        if (PanelProportions.Remove(oldId, out var proportion))
            PanelProportions.TryAdd(newId, proportion);

        if (PanelContentState.Remove(oldId, out var content))
            PanelContentState.TryAdd(newId, content);

        foreach (var col in LeftColumns.Concat(RightColumns).Concat(BottomColumns))
            RenamePanelIdInColumn(col, oldId, newId);
    }

    private static void RenamePanelIdInColumn(DockColumnLayout col, string oldId, string newId)
    {
        for (var i = 0; i < col.PanelIds.Count; i++)
        {
            if (col.PanelIds[i] == oldId)
                col.PanelIds[i] = newId;
        }

        foreach (var tab in col.TabGroups.Values)
        {
            for (var i = 0; i < tab.PanelIds.Count; i++)
            {
                if (tab.PanelIds[i] != oldId)
                    continue;
                if (tab.PanelIds.Contains(newId))
                    tab.PanelIds.RemoveAt(i--);
                else
                    tab.PanelIds[i] = newId;
            }
        }

        if (col.Rows == null)
            return;

        foreach (var row in col.Rows)
        {
            for (var i = 0; i < row.PanelIds.Count; i++)
            {
                if (row.PanelIds[i] != oldId)
                    continue;
                if (row.PanelIds.Contains(newId))
                    row.PanelIds.RemoveAt(i--);
                else
                    row.PanelIds[i] = newId;
            }
        }
    }

    /// <summary>True when the panel appears in a resolved dock row (visible to the UI).</summary>
    public bool IsPanelReachable(string id)
        => LeftColumns.Any(c => c.IsPanelReachable(id))
           || RightColumns.Any(c => c.IsPanelReachable(id))
           || BottomColumns.Any(c => c.IsPanelReachable(id));

    /// <summary>Unhide and place a panel into the layout when it is missing from dock rows.</summary>
    public void EnsurePanelPlaced(string id)
    {
        HiddenPanelIds.Remove(id);
        if (!IsPanelReachable(id))
        {
            var zone = PanelRegistry.Get(id)?.DefaultZone ?? "right-0";

            if (zone == "left")
            {
                if (LeftColumns.Count == 0)
                    LeftColumns.Add(new DockColumnLayout { Id = "left-0" });
                DockTabStacks.PlacePanel(LeftColumns[0], id);
            }
            else if (zone == "bottom")
            {
                PlacePanelInBottomColumn(id);
            }
            else if (zone.StartsWith("right-", StringComparison.Ordinal)
                     && int.TryParse(zone.AsSpan(6), out var rightIdx)
                     && rightIdx < RightColumns.Count)
            {
                DockTabStacks.PlacePanel(RightColumns[rightIdx], id);
            }
            else if (RightColumns.Count > 0)
            {
                DockTabStacks.PlacePanel(RightColumns[0], id);
            }
        }

        RepairLayoutIntegrity();
        ActivatePanel(id);
    }

    /// <summary>Select the panel in its tab group or row (after showing a hidden docker).</summary>
    public void ActivatePanel(string id)
    {
        foreach (var col in LeftColumns.Concat(RightColumns).Concat(BottomColumns))
        {
            foreach (var tab in col.TabGroups.Values)
            {
                var idx = tab.PanelIds.IndexOf(id);
                if (idx < 0)
                    continue;
                tab.ActiveIndex = idx;
                return;
            }

            if (col.Rows == null)
                continue;

            foreach (var row in col.Rows)
            {
                var idx = row.PanelIds.IndexOf(id);
                if (idx < 0)
                    continue;
                row.ActiveIndex = idx;
                return;
            }
        }
    }

    public void Normalize(IReadOnlyList<string> knownIds)
    {
        RepairLayoutIntegrity();

        // Migrate: brush is now a first-class docked panel, not a popup-only panel
        HiddenPanelIds.Remove("brush");
        HiddenPanelIds.Remove("tool-properties");
        HiddenPanelIds.Remove("layer-properties");

        HiddenPanelIds.Remove("tools");

        MigrateToWideDockLayoutV2();
        MigrateToTabbedDockV3();
        MigrateToDockableToolsV4();
        MigrateAwayFromForcedHorizontalV5();
        MigrateToBottomColumnsV6();
        MigrateToTabbedPanelStacksV7();
        MigrateFlatRowsToTabStacksV8();
        MigrateStrayPanelIdsIntoRows();

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

        if (BottomColumns.Count == 0)
            BottomColumns = CreateDefault().BottomColumns;

        var known = new HashSet<string>(knownIds);

        // Prune unknown panel IDs (keep tab-group row keys — they are not registry panel ids).
        foreach (var col in RightColumns)
        {
            col.PanelIds = col.PanelIds.Where(id => IsKnownColumnPanelId(id, col, known)).Distinct().ToList();
            col.RepairTabGroupPanelIds();
        }
        foreach (var left in LeftColumns)
        {
            left.PanelIds = left.PanelIds
                .Where(id => IsKnownColumnPanelId(id, left, known))
                .Distinct()
                .ToList();
            left.RepairTabGroupPanelIds();
        }
        if (LeftColumns.Count == 0)
            LeftColumns = CreateDefault().LeftColumns;
        foreach (var bottom in BottomColumns)
        {
            bottom.PanelIds = bottom.PanelIds
                .Where(id => IsKnownColumnPanelId(id, bottom, known))
                .Distinct()
                .ToList();
            bottom.RepairTabGroupPanelIds();
        }

        // Ensure every known panel is placed somewhere
        foreach (var id in known)
        {
            if (IsPanelReachable(id)) continue;
            if (FloatingPanels.ContainsKey(id)) continue;
            if (HiddenPanelIds.Contains(id)) continue;

            var def = PanelRegistry.Get(id);
            var zone = def?.DefaultZone ?? "right-0";

            if (zone == "left")
            {
                if (LeftColumns.Count > 0)
                    DockTabStacks.PlacePanel(LeftColumns[0], id);
                else
                    PlacePanelInLeftColumn(id);
            }
            else if (zone == "bottom")
                PlacePanelInBottomColumn(id);
            else if (zone.StartsWith("right-") && int.TryParse(zone.AsSpan(6), out var rightIdx)
                     && rightIdx < RightColumns.Count)
                DockTabStacks.PlacePanel(RightColumns[rightIdx], id);
            else
                DockTabStacks.PlacePanel(RightColumns[0], id);
        }

        // Deduplicate: remove from non-default columns
        DeduplicateColumns(known);

        RepairLayoutIntegrity();
    }

    private void MigrateFlatRowsToTabStacksV8()
    {
        if (LayoutVersion >= 8)
            return;

        ConsolidateSplitLeftColumns();

        foreach (var col in LeftColumns.Concat(RightColumns).Concat(BottomColumns))
            DockTabStacks.CompactRows(col);

        foreach (var col in LeftColumns.Concat(RightColumns))
        {
            if (col.Rows != null)
                continue;
            if (DockTabStacks.NeedsCompaction(col))
                DockTabStacks.Compact(col);
        }

        LayoutVersion = 8;
    }

    /// <summary>
    /// Merge fragmented left rails that only hold toolkit panels into one <c>tab:left</c> column.
    /// </summary>
    private void ConsolidateSplitLeftColumns()
    {
        if (LeftColumns.Count < 2)
            return;

        if (!AllLeftColumnsToolkitOnly())
            return;

        var toolkitPanels = CollectToolkitPanelsFromLeftColumns();
        if (toolkitPanels.Count == 0)
            return;

        var primary = LeftColumns[0];
        var activePanel = ResolveActiveToolkitPanel(primary, toolkitPanels) ?? toolkitPanels[0];
        var leftStack = DockTabStacks.Known.First(s => s.Key == "tab:left");
        var ordered = leftStack.Members.Where(toolkitPanels.Contains)
            .Concat(toolkitPanels.Where(id => !leftStack.Members.Contains(id)))
            .ToList();

        primary.Rows = null;
        primary.PanelIds = ["tab:left"];
        primary.TabGroups = new Dictionary<string, TabGroupLayout>
        {
            ["tab:left"] = new TabGroupLayout
            {
                PanelIds = ordered,
                ActiveIndex = Math.Clamp(ordered.IndexOf(activePanel), 0, ordered.Count - 1)
            }
        };

        while (LeftColumns.Count > 1)
            LeftColumns.RemoveAt(LeftColumns.Count - 1);
    }

    private static bool IsToolkitPanel(string id)
        => id is "tools" or "brush" or "tool-properties";

    private static IEnumerable<string> EnumerateColumnPanelIds(DockColumnLayout col)
    {
        if (col.Rows is { Count: > 0 })
            return col.Rows.SelectMany(r => r.PanelIds);

        return col.PanelIds.Where(id => !id.StartsWith("tab:", StringComparison.Ordinal))
            .Concat(col.TabGroups.Values.SelectMany(t => t.PanelIds));
    }

    private bool AllLeftColumnsToolkitOnly()
    {
        var hasPanels = false;
        foreach (var col in LeftColumns)
        {
            foreach (var id in EnumerateColumnPanelIds(col))
            {
                hasPanels = true;
                if (!IsToolkitPanel(id))
                    return false;
            }
        }

        return hasPanels;
    }

    private List<string> CollectToolkitPanelsFromLeftColumns()
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        foreach (var col in LeftColumns)
        {
            foreach (var id in EnumerateColumnPanelIds(col).Where(IsToolkitPanel))
            {
                if (seen.Add(id))
                    result.Add(id);
            }
        }

        return result;
    }

    private static string? ResolveActiveToolkitPanel(DockColumnLayout col, IReadOnlyList<string> toolkitPanels)
    {
        if (col.TabGroups.TryGetValue("tab:left", out var tab)
            && tab.ActiveIndex >= 0
            && tab.ActiveIndex < tab.PanelIds.Count)
        {
            var active = tab.PanelIds[tab.ActiveIndex];
            if (toolkitPanels.Contains(active))
                return active;
        }

        foreach (var id in EnumerateColumnPanelIds(col))
        {
            if (toolkitPanels.Contains(id))
                return id;
        }

        return null;
    }

    private void MigrateToTabbedPanelStacksV7()
    {
        if (LayoutVersion >= 7)
            return;

        foreach (var col in LeftColumns)
            DockTabStacks.Compact(col);
        foreach (var col in RightColumns)
            DockTabStacks.Compact(col);

        LayoutVersion = 7;
    }

    private static bool IsKnownColumnPanelId(string id, DockColumnLayout col, HashSet<string> known)
        => known.Contains(id) || col.TabGroups.ContainsKey(id);

    /// <summary>
    /// Panels appended to flat <see cref="DockColumnLayout.PanelIds"/> while <see cref="DockColumnLayout.Rows"/>
    /// is authoritative are invisible — move them into new rows (e.g. newly registered dockers).
    /// </summary>
    private void MigrateStrayPanelIdsIntoRows()
    {
        foreach (var col in LeftColumns.Concat(RightColumns).Concat(BottomColumns))
        {
            if (col.Rows is not { Count: > 0 }) continue;

            foreach (var id in col.PanelIds.Where(id => !col.TabGroups.ContainsKey(id)).ToList())
            {
                if (col.ContainsPanel(id)) continue;
                col.Rows.Add(new DockRowLayout { PanelIds = [id] });
            }

            col.PanelIds = col.PanelIds.Where(id => col.TabGroups.ContainsKey(id)).ToList();
        }
    }

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

    private void MigrateToDockableToolsV4()
    {
        if (LayoutVersion >= 4)
            return;

        if (!HiddenPanelIds.Contains("tools")
            && !LeftColumn.ContainsPanel("tools")
            && !RightColumns.Any(c => c.ContainsPanel("tools"))
            && !BottomColumns.Any(c => c.ContainsPanel("tools"))
            && !FloatingPanels.ContainsKey("tools"))
        {
            if (LeftColumn.TabGroups.TryGetValue("tab:left", out var tab)
                && !tab.PanelIds.Contains("tools"))
            {
                tab.PanelIds.Insert(0, "tools");
            }
            else if (!LeftColumn.PanelIds.Contains("tools"))
            {
                LeftColumn.PanelIds.Insert(0, "tools");
            }
        }

        LayoutVersion = 4;
    }

    /// <summary>Undo layout v5 that forced tools/brush/settings into one horizontal row.</summary>
    private void MigrateAwayFromForcedHorizontalV5()
    {
        static bool IsToolkitSet(IEnumerable<string> ids)
        {
            var set = ids.OrderBy(static x => x, StringComparer.Ordinal).ToArray();
            return set.Length == 3
                   && set[0] == "brush"
                   && set[1] == "tool-properties"
                   && set[2] == "tools";
        }

        static bool TryGetForcedHorizontalToolkitRow(DockColumnLayout col, out List<string> panelIds)
        {
            panelIds = [];
            if (col.Rows is not { Count: 1 })
                return false;
            var row = col.Rows[0];
            if (row.Orientation != DockOrientation.Horizontal || !IsToolkitSet(row.PanelIds))
                return false;
            panelIds = row.PanelIds.ToList();
            return true;
        }

        // Only undo the automatic v5 default/migration — not user-arranged horizontal docks.
        if (LayoutVersion != 5)
            return;

        for (var i = 0; i < LeftColumns.Count; i++)
        {
            if (!TryGetForcedHorizontalToolkitRow(LeftColumns[i], out var toolkit))
                continue;

            LeftColumns[i].Rows = null;
            LeftColumns[i].PanelIds = ["tab:left"];
            LeftColumns[i].TabGroups = new Dictionary<string, TabGroupLayout>
            {
                ["tab:left"] = new()
                {
                    PanelIds = toolkit,
                    ActiveIndex = Math.Clamp(toolkit.IndexOf("brush"), 0, toolkit.Count - 1)
                }
            };
        }

        if (LeftRailWidth > 400)
            LeftRailWidth = 280;

        if (LayoutVersion >= 5)
            LayoutVersion = 4;
    }

    private void MigrateToBottomColumnsV6()
    {
        if (LayoutVersion >= 6)
            return;

        if (BottomColumns.Count == 0)
        {
            BottomColumns =
            [
                new DockColumnLayout
                {
                    Id = "bottom-0",
                    PanelIds = ["node-graph"]
                }
            ];
        }
        else
        {
            for (var i = 0; i < BottomColumns.Count; i++)
                BottomColumns[i].Id = $"bottom-{i}";
        }

        LayoutVersion = 6;
    }

    public IReadOnlyList<DockColumnLayout> ColumnsForZone(DockZone zone) => zone switch
    {
        DockZone.Left => LeftColumns,
        DockZone.Right => RightColumns,
        DockZone.Bottom => BottomColumns,
        _ => RightColumns
    };

    public bool ContainsPanelInZone(string id, DockZone zone)
        => ColumnsForZone(zone).Any(c => c.ContainsPanel(id));

    /// <summary>
    /// Returns the first panel ID that is referenced in a TabGroup but not accessible
    /// through PanelIds or Rows (orphaned). Returns null if layout is valid.
    /// </summary>
    public string? FindOrphanedPanel()
    {
        var allColumns = new List<DockColumnLayout>();
        allColumns.AddRange(LeftColumns);
        allColumns.AddRange(RightColumns);
        allColumns.AddRange(BottomColumns);

        foreach (var col in allColumns)
        {
            // Collect all panel IDs reachable through PanelIds + TabGroups resolution
            var reachable = new HashSet<string>();
            var resolved = col.ResolvedRows();
            foreach (var row in resolved)
                foreach (var pid in row.PanelIds)
                    reachable.Add(pid);

            // Rows model ignores legacy TabGroups (see PurgeStaleFlatLayoutWhenRowsAuthoritative).
            if (col.Rows is { Count: > 0 })
                continue;

            // Check TabGroups for panels not in reachable set
            foreach (var (_, tab) in col.TabGroups)
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

    private void PlacePanelInBottomColumn(string id)
    {
        if (BottomColumns.Count == 0)
            BottomColumns.Add(new DockColumnLayout { Id = "bottom-0" });

        var col = BottomColumns[0];
        if (col.Rows is { Count: > 0 })
        {
            var row = col.Rows[^1];
            if (!row.PanelIds.Contains(id))
                row.PanelIds.Add(id);
            return;
        }

        if (!col.PanelIds.Contains(id))
            col.PanelIds.Add(id);
    }

    private void PlacePanelInLeftColumn(string id)
    {
        if (LeftColumns.Count == 0)
            LeftColumns.Add(new DockColumnLayout { Id = "left-0" });

        var col = LeftColumns[0];
        if (col.Rows is { Count: > 0 })
        {
            var row = col.Rows[^1];
            if (!row.PanelIds.Contains(id))
                row.PanelIds.Add(id);
            return;
        }

        if (!col.PanelIds.Contains(id))
            col.PanelIds.Add(id);
    }

    /// <summary>
    /// When a panel appears in more than one zone, keep a single copy.
    /// Priority: right, then left, then bottom — so cross-side drags to the right rail stick.
    /// </summary>
    private void DeduplicateColumns(HashSet<string> known)
    {
        foreach (var id in known)
        {
            var inRight = RightColumns.Where(c => c.ContainsPanel(id)).ToList();
            var inLeft = LeftColumns.Where(c => c.ContainsPanel(id)).ToList();
            var inBottom = BottomColumns.Where(c => c.ContainsPanel(id)).ToList();
            var count = inRight.Count + inLeft.Count + inBottom.Count;
            if (count <= 1) continue;

            if (inRight.Count > 0)
            {
                var keep = inRight[0];
                foreach (var c in inRight)
                    if (c != keep) c.RemovePanel(id);
                foreach (var c in inLeft) c.RemovePanel(id);
                foreach (var c in inBottom) c.RemovePanel(id);
                continue;
            }

            if (inLeft.Count > 0)
            {
                var keep = inLeft[0];
                foreach (var c in inLeft)
                    if (c != keep) c.RemovePanel(id);
                foreach (var c in inBottom) c.RemovePanel(id);
                continue;
            }

            var keepBottom = inBottom[0];
            foreach (var c in inBottom)
                if (c != keepBottom) c.RemovePanel(id);
        }
    }

    public (int ColumnIndex, int PanelIndex)? FindPanel(string id)
    {
        for (var i = 0; i < LeftColumns.Count; i++)
        {
            var idx = LeftColumns[i].PanelIds.IndexOf(id);
            if (idx >= 0) return (DockColumnIndices.Left(i), idx);
            if (LeftColumns[i].ContainsPanel(id))
            {
                var resolved = LeftColumns[i].ResolvedRows();
                for (var ri = 0; ri < resolved.Count; ri++)
                {
                    var ti = resolved[ri].PanelIds.ToList().IndexOf(id);
                    if (ti >= 0) return (DockColumnIndices.Left(i), ri);
                }
            }
        }

        for (var i = 0; i < BottomColumns.Count; i++)
        {
            var idx = BottomColumns[i].PanelIds.IndexOf(id);
            if (idx >= 0) return (DockColumnIndices.Bottom(i), idx);
            if (!BottomColumns[i].ContainsPanel(id)) continue;
            var resolved = BottomColumns[i].ResolvedRows();
            for (var ri = 0; ri < resolved.Count; ri++)
            {
                var ti = resolved[ri].PanelIds.ToList().IndexOf(id);
                if (ti >= 0) return (DockColumnIndices.Bottom(i), ri);
            }
        }

        for (var i = 0; i < RightColumns.Count; i++)
        {
            var idx = RightColumns[i].PanelIds.IndexOf(id);
            if (idx >= 0) return (i, idx);
            if (!RightColumns[i].ContainsPanel(id)) continue;
            var resolved = RightColumns[i].ResolvedRows();
            for (var ri = 0; ri < resolved.Count; ri++)
            {
                var ti = resolved[ri].PanelIds.ToList().IndexOf(id);
                if (ti >= 0) return (i, ri);
            }
        }

        return null;
    }

    public void RemovePanel(string id)
    {
        foreach (var col in LeftColumns)
            col.RemovePanel(id);
        foreach (var col in BottomColumns)
            col.RemovePanel(id);
        foreach (var col in RightColumns)
            col.RemovePanel(id);
    }

    public bool IsFloating(string id)
        => FloatingPanels.TryGetValue(id, out var s) && s.IsFloating;

    public bool IsVisible(string id)
    {
        if (HiddenPanelIds.Contains(id))
            return false;
        if (IsFloating(id))
            return true;
        return IsPanelReachable(id);
    }
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

    /// <summary>Vertical rows are always shown as a tab strip (even with one panel).</summary>
    [JsonIgnore]
    public bool IsTabGroup => Orientation == DockOrientation.Vertical && PanelIds.Count >= 1;

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

    /// <summary>Vertical rows render as a tab strip (even with one panel).</summary>
    public bool IsTabStrip => Orientation != DockOrientation.Horizontal && PanelIds.Count >= 1;
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
        if (Rows != null)
            return Rows.Any(r => r.PanelIds.Contains(id));
        return PanelIds.Contains(id) || TabGroups.Values.Any(t => t.PanelIds.Contains(id));
    }

    /// <summary>True when the panel is in a row the dock UI actually builds.</summary>
    public bool IsPanelReachable(string id)
        => ResolvedRows().Any(row => row.PanelIds.Contains(id));

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
            if (!key.StartsWith("tab:", StringComparison.Ordinal)
                && !key.StartsWith("row:", StringComparison.Ordinal))
                continue;
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
            foreach (var (key, tab) in TabGroups.ToList())
            {
                tab.PanelIds.Remove(id);
                if (tab.PanelIds.Count == 0)
                {
                    TabGroups.Remove(key);
                    PanelIds.Remove(key);
                }
            }

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
        }
    }

    /// <summary>
    /// Returns the effective row layout. Uses Rows if populated, otherwise
    /// resolves from flat PanelIds/TabGroups.
    /// </summary>
    public IReadOnlyList<ResolvedRow> ResolvedRows()
    {
        var result = new List<ResolvedRow>();

        if (Rows != null)
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
