using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace Floss.App.Tests;

public class DockingTests
{
    private static void SetColumnPanels(DockColumnLayout col, params string[] panelIds)
    {
        col.PanelIds = panelIds.ToList();
        col.TabGroups.Clear();
        col.Rows = null;
    }

    private static readonly object Gate = new();
    private static bool _initialized;

    private static void EnsureAvalonia()
    {
        lock (Gate)
        {
            if (_initialized || Application.Current != null)
            {
                _initialized = true;
                return;
            }
            try
            {
                AppBuilder.Configure<App>()
                    .UseSkia()
                    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                    .SetupWithoutStarting();
            }
            catch (InvalidOperationException) { }
            _initialized = true;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // PanelRegistry
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Registry_RegisterAndGet()
    {
        PanelRegistry.Clear(); // Reset for test
        PanelRegistry.Register(new DockPanelDef(
            "test-panel", "Test", () => new TextBlock { Text = "hello" },
            DefaultZone: "right-0"));

        var p = PanelRegistry.Get("test-panel");
        Assert.NotNull(p);
        Assert.Equal("test-panel", p.Id);
        Assert.Equal("Test", p.Title);
        Assert.Equal("right-0", p.DefaultZone);
        Assert.Equal(0.25, p.Proportion);
        Assert.Equal(64, p.MinHeight);
        Assert.True(p.AllowFloat);
        Assert.True(p.AllowHide);

        var content = p.BuildContent();
        Assert.IsType<TextBlock>(content);
    }

    [Fact]
    public void Registry_UnknownReturnsNull()
    {
        Assert.Null(PanelRegistry.Get("nonexistent"));
    }

    [Fact]
    public void Registry_AllIds_ReflectsRegistration()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "A", () => new TextBlock()));
        PanelRegistry.Register(new DockPanelDef("b", "B", () => new TextBlock()));

        Assert.Contains("a", PanelRegistry.AllIds);
        Assert.Contains("b", PanelRegistry.AllIds);
        Assert.Equal(2, PanelRegistry.AllIds.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DockColumnLayout
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Column_ContainsPanel_FindsInFlatList()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["brush", "layers", "color"]
        };
        Assert.True(col.ContainsPanel("brush"));
        Assert.True(col.ContainsPanel("color"));
        Assert.False(col.ContainsPanel("tools"));
    }

    [Fact]
    public void Column_ContainsPanel_FindsInTabGroup()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0", "color"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["brush", "tool-properties"] } }
        };
        Assert.True(col.ContainsPanel("brush"));
        Assert.True(col.ContainsPanel("tool-properties"));
        Assert.True(col.ContainsPanel("color"));
        Assert.False(col.ContainsPanel("layers"));
    }

    [Fact]
    public void Column_RemovePanel_RemovesFromFlatList()
    {
        var col = new DockColumnLayout { PanelIds = ["brush", "layers"] };
        col.RemovePanel("brush");
        Assert.False(col.ContainsPanel("brush"));
        Assert.True(col.ContainsPanel("layers"));
    }

    [Fact]
    public void Column_RemovePanel_KeepsTabGroup_WhenOneRemains()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["brush", "layers"] } }
        };
        col.RemovePanel("brush");

        Assert.True(col.TabGroups.ContainsKey("tab:0"));
        Assert.Equal(["layers"], col.TabGroups["tab:0"].PanelIds);
        Assert.Contains("tab:0", col.PanelIds);
    }

    [Fact]
    public void Column_RemovePanel_RemovesEmptyTabGroup()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["brush", "layers"] } }
        };
        col.RemovePanel("brush");
        col.RemovePanel("layers");

        // Both removed, tab group should be gone
        Assert.False(col.TabGroups.ContainsKey("tab:0"));
        Assert.Empty(col.PanelIds);
    }

    [Fact]
    public void Column_ResolvedRows_FlatPanels()
    {
        var col = new DockColumnLayout { PanelIds = ["brush", "layers"] };
        var rows = col.ResolvedRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal(["brush"], rows[0].PanelIds);
        Assert.Equal(["layers"], rows[1].PanelIds);
    }

    [Fact]
    public void Column_ResolvedRows_WithTabGroups()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0", "color"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["brush", "tool-properties"] } }
        };
        var rows = col.ResolvedRows();

        Assert.Equal(2, rows.Count);
        Assert.Equal(["brush", "tool-properties"], rows[0].PanelIds);
        Assert.Equal(["color"], rows[1].PanelIds);
    }

    [Fact]
    public void DockTabStacks_Compact_MergesFlatColorStack()
    {
        var col = new DockColumnLayout { PanelIds = ["color", "color-slider", "layers"] };
        Assert.True(DockTabStacks.NeedsCompaction(col));

        DockTabStacks.Compact(col);

        Assert.Equal(["tab:color", "layers"], col.PanelIds);
        Assert.Equal(["color", "color-slider"], col.TabGroups["tab:color"].PanelIds);
        var rows = col.ResolvedRows();
        Assert.Equal(2, rows.Count);
        Assert.Equal(2, rows[0].PanelIds.Count);
        Assert.Equal(["layers"], rows[1].PanelIds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // WorkspaceLayout
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Layout_Default_HasOneRightColumn()
    {
        var layout = WorkspaceLayout.CreateDefault();
        Assert.Single(layout.RightColumns);
        Assert.Equal("left-0", layout.LeftColumns[0].Id);
        Assert.Equal("bottom-0", layout.BottomColumn.Id);
    }

    [Fact]
    public void Layout_Default_AllKnownPanelsPlaced()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "Tools", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("brush", "Brush", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("node-graph", "Node Graph", () => new TextBlock(), DefaultZone: "bottom"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.HiddenPanelIds.Clear();
        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.LeftColumns.Any(c => c.ContainsPanel("tools")));
        Assert.True(layout.LeftColumns.Any(c => c.ContainsPanel("brush")));
        Assert.True(layout.BottomColumn.ContainsPanel("node-graph"));
    }

    [Fact]
    public void Layout_FindPanel_FindsInLeft()
    {
        var layout = new WorkspaceLayout
        {
            LeftColumn = new DockColumnLayout { Id = "left", PanelIds = ["tools"] }
        };
        var result = layout.FindPanel("tools");
        Assert.NotNull(result);
        Assert.Equal(-1, result.Value.ColumnIndex);
    }

    [Fact]
    public void Layout_FindPanel_FindsInBottom()
    {
        var layout = new WorkspaceLayout
        {
            BottomColumn = new DockColumnLayout { Id = "bottom", PanelIds = ["node-graph"] }
        };
        var result = layout.FindPanel("node-graph");
        Assert.NotNull(result);
        Assert.Equal(DockColumnIndices.Bottom(0), result.Value.ColumnIndex);
    }

    [Fact]
    public void Layout_FindPanel_FindsInRightColumn()
    {
        var layout = WorkspaceLayout.CreateDefault();
        foreach (var c in layout.LeftColumns)
            SetColumnPanels(c);
        SetColumnPanels(layout.RightColumns[0], "brush", "color");
        layout.RightColumns.Add(new DockColumnLayout { Id = "right-1", PanelIds = ["layers"] });

        var br = layout.FindPanel("brush");
        Assert.NotNull(br);
        Assert.Equal(0, br.Value.ColumnIndex);

        var ly = layout.FindPanel("layers");
        Assert.NotNull(ly);
        Assert.Equal(1, ly.Value.ColumnIndex);
    }

    [Fact]
    public void Layout_RemovePanel_RemovesFromAll()
    {
        var layout = new WorkspaceLayout
        {
            LeftColumn = new DockColumnLayout { PanelIds = ["tools"] },
            RightColumns = [new() { PanelIds = ["brush", "layers"] }, new() { PanelIds = ["color"] }],
            BottomColumn = new DockColumnLayout { PanelIds = ["node-graph"] }
        };
        layout.RemovePanel("brush");

        Assert.False(layout.LeftColumn.ContainsPanel("brush"));
        Assert.False(layout.RightColumns.Any(c => c.ContainsPanel("brush")));
        Assert.False(layout.BottomColumn.ContainsPanel("brush"));
        Assert.True(layout.RightColumns[0].ContainsPanel("layers"));
    }

    [Fact]
    public void Layout_Clone_IsDeepCopy()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.LeftColumn.PanelIds.Add("extra");

        var clone = layout.Clone();
        clone.LeftColumn.PanelIds.Remove("extra");

        Assert.Contains("extra", layout.LeftColumn.PanelIds); // Original unaffected
        Assert.DoesNotContain("extra", clone.LeftColumn.PanelIds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // TabGroupLayout
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TabGroup_DefaultActiveIndex()
    {
        var tab = new TabGroupLayout { PanelIds = ["a", "b", "c"] };
        Assert.Equal(0, tab.ActiveIndex);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DockTabGroup control (headless)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DockTabGroup_CreatesTabs()
    {
        EnsureAvalonia();

        var content = new Dictionary<string, Control>
        {
            ["a"] = new TextBlock { Text = "Panel A" },
            ["b"] = new TextBlock { Text = "Panel B" }
        };
        var titles = new Dictionary<string, string> { ["a"] = "Alpha", ["b"] = "Beta" };

        var group = new DockTabGroup(["a", "b"], content, titles);

        Assert.Equal(2, group.PanelIds.Count);
        Assert.Equal("a", group.ActivePanelId);
        Assert.Contains("a", group.PanelIds);
        Assert.Contains("b", group.PanelIds);
    }

    [Fact]
    public void DockTabGroup_SwitchesActivePanel()
    {
        EnsureAvalonia();

        var content = new Dictionary<string, Control>
        {
            ["a"] = new TextBlock { Text = "Panel A" },
            ["b"] = new TextBlock { Text = "Panel B" }
        };
        var titles = new Dictionary<string, string> { ["a"] = "Alpha", ["b"] = "Beta" };

        var group = new DockTabGroup(["a", "b"], content, titles);
        Assert.Equal("a", group.ActivePanelId);

        group.SetActivePanel("b");
        Assert.Equal("b", group.ActivePanelId);
    }

    [Fact]
    public void DockTabGroup_FiresTabChangedEvent()
    {
        EnsureAvalonia();

        var content = new Dictionary<string, Control>
        {
            ["a"] = new TextBlock { Text = "A" },
            ["b"] = new TextBlock { Text = "B" }
        };
        var titles = new Dictionary<string, string> { ["a"] = "A", ["b"] = "B" };
        var group = new DockTabGroup(["a", "b"], content, titles);

        string? changedTo = null;
        group.TabChanged += id => changedTo = id;

        group.SetActivePanel("b");
        Assert.Equal("b", changedTo);
    }

    [Fact]
    public void DockTabGroup_SetActive_NoOp_WhenAlreadyActive()
    {
        EnsureAvalonia();

        var content = new Dictionary<string, Control> { ["a"] = new TextBlock() };
        var titles = new Dictionary<string, string> { ["a"] = "A" };
        var group = new DockTabGroup(["a"], content, titles);

        int fired = 0;
        group.TabChanged += _ => fired++;

        group.SetActivePanel("a"); // Already active, should not fire
        Assert.Equal(0, fired);
    }

    [Fact]
    public void DockTabGroup_IgnoresUnknownPanelId()
    {
        EnsureAvalonia();

        var content = new Dictionary<string, Control> { ["a"] = new TextBlock() };
        var titles = new Dictionary<string, string> { ["a"] = "A" };
        var group = new DockTabGroup(["a"], content, titles);

        group.SetActivePanel("bogus");
        Assert.Equal("a", group.ActivePanelId); // Unchanged
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DockColumnLayout -> PanelId setter
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PanelIdsProperty_GetSet_RoundTrips()
    {
        var col = new DockColumnLayout();
        col.PanelIds = ["a", "b", "c"];

        Assert.Equal(3, col.PanelIds.Count);
        Assert.Equal("a", col.PanelIds[0]);
        Assert.Equal("c", col.PanelIds[2]);
    }

    [Fact]
    public void PanelIdsProperty_Set_DoesNotAffectTabGroups()
    {
        var col = new DockColumnLayout
        {
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["a", "b"] } }
        };
        col.PanelIds = ["x", "y"];

        Assert.Equal(2, col.PanelIds.Count);
        // TabGroups persist independently
        Assert.True(col.TabGroups.ContainsKey("tab:0"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Horizontal rows (side-by-side docking)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Column_Rows_HorizontalLayout()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["brush", "color"], Orientation = DockOrientation.Horizontal },
                new() { PanelIds = ["layers"], Orientation = DockOrientation.Vertical }
            }
        };

        var resolved = col.ResolvedRows();
        Assert.Equal(2, resolved.Count);

        // First row: horizontal, two panels side-by-side
        Assert.Equal(DockOrientation.Horizontal, resolved[0].Orientation);
        Assert.Equal(["brush", "color"], resolved[0].PanelIds);

        // Second row: vertical, single panel
        Assert.Equal(DockOrientation.Vertical, resolved[1].Orientation);
        Assert.Equal(["layers"], resolved[1].PanelIds);
    }

    [Fact]
    public void Column_Rows_RemovePanel_RemovesFromHorizontalRow()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["brush", "color"], Orientation = DockOrientation.Horizontal }
            }
        };
        col.RemovePanel("color");

        // Row should now have only "brush"
        Assert.Single(col.Rows);
        Assert.Single(col.Rows[0].PanelIds);
        Assert.Equal("brush", col.Rows[0].PanelIds[0]);
    }

    [Fact]
    public void Column_Rows_RemovePanel_RemovesEmptyHorizontalRow()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["brush"], Orientation = DockOrientation.Horizontal }
            }
        };
        col.RemovePanel("brush");
        Assert.Empty(col.Rows);
    }

    [Fact]
    public void Column_ContainsPanel_WorksWithRows()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["brush", "color"], Orientation = DockOrientation.Horizontal },
                new() { PanelIds = ["layers"] }
            }
        };
        Assert.True(col.ContainsPanel("brush"));
        Assert.True(col.ContainsPanel("color"));
        Assert.True(col.ContainsPanel("layers"));
        Assert.False(col.ContainsPanel("tools"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Layout Normalize edge cases
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Normalize_PrunesUnknownPanelIds_AndMovesToDefaultZone()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "left"));

        var layout = new WorkspaceLayout
        {
            LeftColumn = new DockColumnLayout { PanelIds = ["ghost", "brush"] },
            RightColumns = [] // clear default right columns so brush isn't duplicated there
        };
        layout.Normalize(PanelRegistry.AllIds);

        // "ghost" is unknown — pruned
        Assert.DoesNotContain("ghost", layout.LeftColumn.PanelIds);
        // "brush" stays where user placed it; Normalize only deduplicates, not re-zones
        Assert.True(layout.LeftColumn.ContainsPanel("brush"));
    }

    [Fact]
    public void Normalize_Deduplicates_PrefersRightOverBottom()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("node-graph", "", () => new TextBlock(), DefaultZone: "bottom"));

        var layout = new WorkspaceLayout
        {
            BottomColumn = new DockColumnLayout { PanelIds = ["node-graph"] },
            RightColumns = [new() { PanelIds = ["node-graph"] }, new() { PanelIds = [] }]
        };
        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.RightColumns[0].ContainsPanel("node-graph"));
        Assert.False(layout.BottomColumn.ContainsPanel("node-graph"));
    }

    [Fact]
    public void Normalize_Deduplicates_PrefersRightOverLeft()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left"));

        var layout = new WorkspaceLayout
        {
            LeftColumn = new DockColumnLayout { PanelIds = ["tools"] },
            RightColumns = [new() { PanelIds = ["tools"] }, new() { PanelIds = [] }]
        };
        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.RightColumns[0].ContainsPanel("tools"));
        Assert.False(layout.LeftColumn.ContainsPanel("tools"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Layout reset / rebuild scenarios (covers crash at "Reset Layout")
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Layout_ResetLayout_RemovesAllPanelsThenReaddsDefault()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "Tools", () => new TextBlock(), DefaultZone: "left", Proportion: 0.5));
        PanelRegistry.Register(new DockPanelDef("brush", "Brush", () => new TextBlock(), DefaultZone: "right-0", Proportion: 0.3));
        PanelRegistry.Register(new DockPanelDef("layers", "Layers", () => new TextBlock(), DefaultZone: "right-0", Proportion: 0.35));

        var layout = new WorkspaceLayout
        {
            HiddenPanelIds = [],
            LeftColumn = new DockColumnLayout { PanelIds = [] },
            RightColumns = [new() { PanelIds = [] }, new() { PanelIds = [] }]
        };
        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.LeftColumn.ContainsPanel("tools"));
        Assert.True(layout.RightColumns[0].ContainsPanel("brush"));
        Assert.True(layout.RightColumns[0].ContainsPanel("layers"));
        Assert.Null(layout.FloatingPanels.GetValueOrDefault("tools"));
    }

    [Fact]
    public void Layout_MovePanelBetweenColumns_UpdateIsConsistent()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("color", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        foreach (var c in layout.LeftColumns)
            SetColumnPanels(c);
        SetColumnPanels(layout.RightColumns[0], "brush");
        layout.RightColumns.Add(new DockColumnLayout { Id = "right-1", PanelIds = ["color"] });

        var placement = layout.FindPanel("brush");
        Assert.NotNull(placement);
        Assert.Equal(0, placement!.Value.ColumnIndex);

        layout.RemovePanel("brush");
        layout.LeftColumn.PanelIds.Add("brush");

        // Verify old column no longer has it, new column does
        Assert.False(layout.RightColumns[0].ContainsPanel("brush"));
        Assert.True(layout.LeftColumn.ContainsPanel("brush"));
        Assert.True(layout.RightColumns[1].ContainsPanel("color"));
    }

    [Fact]
    public void Layout_ResetThenMovePanels_PreservesAllPanels()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left", Proportion: 0.5));
        PanelRegistry.Register(new DockPanelDef("layers", "", () => new TextBlock(), DefaultZone: "right-0", Proportion: 0.25));
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "right-0", Proportion: 0.3));
        PanelRegistry.Register(new DockPanelDef("color", "", () => new TextBlock(), DefaultZone: "right-0", Proportion: 0.35));

        var defaults = WorkspaceLayout.CreateDefault();
        defaults.HiddenPanelIds.Clear();
        defaults.Normalize(PanelRegistry.AllIds);

        // Verify all panels are placed exactly once
        var allPlacements = new HashSet<string>();
        foreach (var id in PanelRegistry.AllIds)
        {
            var p = defaults.FindPanel(id);
            Assert.NotNull(p);
            allPlacements.Add(id);

            // Verify not in multiple columns
            var count = (defaults.LeftColumns.Any(c => c.ContainsPanel(id)) ? 1 : 0)
                + defaults.RightColumns.Count(c => c.ContainsPanel(id))
                + (defaults.BottomColumns.Any(c => c.ContainsPanel(id)) ? 1 : 0)
                + (defaults.FloatingPanels.ContainsKey(id) ? 1 : 0);
            Assert.InRange(count, 1, 1);
        }
        Assert.Equal(PanelRegistry.AllIds.Count, allPlacements.Count);
    }

    [Fact]
    public void Layout_RemovePanel_AllOperationsConsistent()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "", () => new TextBlock()));
        PanelRegistry.Register(new DockPanelDef("b", "", () => new TextBlock()));
        PanelRegistry.Register(new DockPanelDef("c", "", () => new TextBlock()));

        var layout = new WorkspaceLayout
        {
            LeftColumn = new DockColumnLayout { PanelIds = ["a"] },
            RightColumns = [new() { PanelIds = ["b", "c"] }, new() { PanelIds = [] }]
        };

        // Remove via layout.RemovePanel (cross-column)
        layout.RemovePanel("b");

        // Verify: contains all columns agree
        Assert.False(layout.LeftColumn.ContainsPanel("b"));
        foreach (var col in layout.RightColumns)
            Assert.False(col.ContainsPanel("b"));
        Assert.Null(layout.FindPanel("b"));
        Assert.NotNull(layout.FindPanel("a"));
        Assert.NotNull(layout.FindPanel("c"));
    }

    [Fact]
    public void Layout_ShortRemoveReaddCycle_NoDuplicates()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("x", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();

        // Initial: no x placed
        layout.LeftColumn.PanelIds.Clear();
        layout.RightColumns[0].PanelIds.Clear();

        // Add to right-0
        layout.RightColumns[0].PanelIds.Add("x");
        Assert.True(layout.RightColumns[0].ContainsPanel("x"));

        // Remove
        layout.RemovePanel("x");
        Assert.False(layout.RightColumns[0].ContainsPanel("x"));

        // Re-add to left
        layout.LeftColumn.PanelIds.Add("x");
        Assert.True(layout.LeftColumn.ContainsPanel("x"));
        Assert.False(layout.RightColumns[0].ContainsPanel("x"));
    }

    [Fact]
    public void Layout_TabGroup_RemoveSoloDissolves_RemoveAllClears()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["a", "b", "c"] } }
        };

        // Remove one → still has 2, tab group survives
        col.RemovePanel("a");
        Assert.True(col.TabGroups.ContainsKey("tab:0"));
        Assert.Equal(["b", "c"], col.TabGroups["tab:0"].PanelIds);

        // Remove second → 1 left, tab group stays (always-tabbed)
        col.RemovePanel("b");
        Assert.True(col.TabGroups.ContainsKey("tab:0"));
        Assert.Equal(["c"], col.TabGroups["tab:0"].PanelIds);
        Assert.Contains("tab:0", col.PanelIds);
    }

    [Fact]
    public void Layout_HorizontalRow_RemoveFromRow_ShrinksRow()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["a", "b"], Orientation = DockOrientation.Horizontal },
                new() { PanelIds = ["c"] }
            }
        };

        Assert.True(col.ContainsPanel("a"));
        Assert.True(col.ContainsPanel("b"));

        col.RemovePanel("a");

        Assert.False(col.ContainsPanel("a"));
        Assert.True(col.ContainsPanel("b"));
        Assert.True(col.ContainsPanel("c"));
        Assert.Single(col.Rows![0].PanelIds); // "b" only
        Assert.Equal("b", col.Rows[0].PanelIds[0]);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // DetachFromVisualParent integration (headless Border/Panel re-parenting)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Detach_ChildFromPanel_ChildHasNoParentAfterDetach()
    {
        EnsureAvalonia();

        var panel = new Grid();
        var child = new Border();
        panel.Children.Add(child);
        Assert.NotNull(child.Parent);
        Assert.True(ReferenceEquals(panel, child.Parent));

        // Simulate GetOrCreatePanelSection's detach: remove from Panel children
        panel.Children.Remove(child);
        Assert.Null(child.Parent); // Avalonia clears Parent on removal from logical tree
    }

    [Fact]
    public void Detach_ChildFromBorder_SetChildNullThenReparent()
    {
        EnsureAvalonia();

        var border = new Border();
        var child = new Border();
        border.Child = child;
        Assert.NotNull(child.Parent);
        Assert.True(ReferenceEquals(border, child.Parent));

        // Detach: set border.Child to null
        border.Child = null;
        Assert.Null(child.Parent);

        // Should be able to reparent to a Grid now
        var grid = new Grid();
        grid.Children.Add(child);
        Assert.NotNull(child.Parent);
        Assert.True(ReferenceEquals(grid, child.Parent));
    }

    [Fact]
    public void Detach_ChildFromContentControl_ReparentToPanel()
    {
        EnsureAvalonia();

        var cc = new ContentControl();
        var child = new Border();
        cc.Content = child;
        Assert.NotNull(child.Parent);

        // Detach
        cc.Content = null;
        Assert.Null(child.Parent);

        // Reparent
        var grid = new Grid();
        grid.Children.Add(child);
        Assert.True(ReferenceEquals(grid, child.Parent));
    }

    [Fact]
    public void Detach_MultipleChildren_NoParentAfterSequentialRemove()
    {
        EnsureAvalonia();

        var grid = new Grid();
        var a = new Border();
        var b = new Border();
        var c = new Border();

        grid.Children.Add(a);
        grid.Children.Add(b);
        grid.Children.Add(c);

        Assert.True(ReferenceEquals(grid, a.Parent));
        Assert.True(ReferenceEquals(grid, b.Parent));
        Assert.True(ReferenceEquals(grid, c.Parent));

        grid.Children.Remove(a);
        grid.Children.Remove(b);
        grid.Children.Remove(c);

        Assert.Null(a.Parent);
        Assert.Null(b.Parent);
        Assert.Null(c.Parent);
    }

    [Fact]
    public void Detach_AddToNewParent_DoesNotCrash()
    {
        EnsureAvalonia();

        var oldGrid = new Grid();
        var child = new Border();
        oldGrid.Children.Add(child);

        // Detach from old
        oldGrid.Children.Remove(child);

        // Reparent to new — this is exactly what RebuildDockers does
        var newGrid = new Grid();
        newGrid.Children.Add(child);

        Assert.True(ReferenceEquals(newGrid, child.Parent));
        Assert.Single(newGrid.Children);
    }

    [Fact]
    public void Detach_AddToParentWithoutDetach_ShouldThrow()
    {
        EnsureAvalonia();

        var oldGrid = new Grid();
        var newGrid = new Grid();
        var child = new Border();

        oldGrid.Children.Add(child);

        // Without detach, adding to new parent should throw InvalidOperationException
        Assert.Throws<InvalidOperationException>(() => newGrid.Children.Add(child));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Cross-column move: left ↔ right, bottom ↔ right
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void MovePanel_LeftToRight0()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.LeftColumn, "tools", "brush");
        SetColumnPanels(layout.RightColumns[0]);

        layout.LeftColumn.PanelIds.Remove("brush");
        layout.RightColumns[0].PanelIds.Add("brush");

        Assert.False(layout.LeftColumn.ContainsPanel("brush"));
        Assert.True(layout.RightColumns[0].ContainsPanel("brush"));
        Assert.True(layout.LeftColumn.ContainsPanel("tools"));
    }

    [Fact]
    public void MovePanel_Right0ToLeft()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("layers", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.RightColumns[0], "layers");
        SetColumnPanels(layout.LeftColumn);

        layout.RightColumns[0].PanelIds.Remove("layers");
        layout.LeftColumn.PanelIds.Add("layers");

        Assert.False(layout.RightColumns[0].ContainsPanel("layers"));
        Assert.True(layout.LeftColumn.ContainsPanel("layers"));
    }

    [Fact]
    public void MovePanel_Right0ToRight1()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("color", "", () => new TextBlock(), DefaultZone: "right-1"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns.Add(new DockColumnLayout { Id = "right-1", PanelIds = [] });
        SetColumnPanels(layout.RightColumns[0], "brush", "color");
        SetColumnPanels(layout.RightColumns[1]);

        layout.RightColumns[0].PanelIds.Remove("color");
        layout.RightColumns[1].PanelIds.Add("color");

        Assert.False(layout.RightColumns[0].ContainsPanel("color"));
        Assert.True(layout.RightColumns[1].ContainsPanel("color"));
    }

    [Fact]
    public void MovePanel_BottomToRight0()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("node-graph", "", () => new TextBlock(), DefaultZone: "bottom"));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.BottomColumn, "node-graph");
        SetColumnPanels(layout.RightColumns[0]);

        layout.BottomColumn.PanelIds.Remove("node-graph");
        layout.RightColumns[0].PanelIds.Add("node-graph");

        Assert.False(layout.BottomColumn.ContainsPanel("node-graph"));
        Assert.True(layout.RightColumns[0].ContainsPanel("node-graph"));
    }

    [Fact]
    public void MovePanel_Right0ToBottom()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("node-graph", "", () => new TextBlock(), DefaultZone: "bottom"));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.RightColumns[0], "node-graph");
        SetColumnPanels(layout.BottomColumn);

        layout.RightColumns[0].PanelIds.Remove("node-graph");
        layout.BottomColumn.PanelIds.Add("node-graph");

        Assert.False(layout.RightColumns[0].ContainsPanel("node-graph"));
        Assert.True(layout.BottomColumn.ContainsPanel("node-graph"));
    }

    [Fact]
    public void MovePanel_BottomToLeft()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("node-graph", "", () => new TextBlock(), DefaultZone: "bottom"));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.BottomColumn, "node-graph");
        SetColumnPanels(layout.LeftColumn);

        layout.BottomColumn.PanelIds.Remove("node-graph");
        layout.LeftColumn.PanelIds.Add("node-graph");

        Assert.False(layout.BottomColumn.ContainsPanel("node-graph"));
        Assert.True(layout.LeftColumn.ContainsPanel("node-graph"));
    }

    [Fact]
    public void MovePanel_LeftToBottom()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left"));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.LeftColumn, "tools");
        SetColumnPanels(layout.BottomColumn);

        layout.LeftColumn.PanelIds.Remove("tools");
        layout.BottomColumn.PanelIds.Add("tools");

        Assert.False(layout.LeftColumn.ContainsPanel("tools"));
        Assert.True(layout.BottomColumn.ContainsPanel("tools"));
    }

    [Fact]
    public void RemovePanel_RemovesFromAllColumns()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        layout.LeftColumn.PanelIds = ["a"];
        layout.RightColumns[0].PanelIds = ["a"];
        layout.BottomColumn.PanelIds = ["a"];

        // Layout.RemovePanel should remove from ALL
        layout.RemovePanel("a");

        Assert.False(layout.LeftColumn.ContainsPanel("a"));
        Assert.False(layout.RightColumns[0].ContainsPanel("a"));
        Assert.False(layout.BottomColumn.ContainsPanel("a"));
    }

    [Fact]
    public void FindPanel_FindsInBottomColumn()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.BottomColumn.PanelIds = ["node-graph"];

        var result = layout.FindPanel("node-graph");
        Assert.NotNull(result);
        Assert.Equal(DockColumnIndices.Bottom(0), result.Value.ColumnIndex);
        Assert.Equal(0, result.Value.PanelIndex);
    }

    [Fact]
    public void FindPanel_BottomAtPosition()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.BottomColumn.PanelIds = ["a", "node-graph", "b"];

        var result = layout.FindPanel("node-graph");
        Assert.NotNull(result);
        Assert.Equal(DockColumnIndices.Bottom(0), result.Value.ColumnIndex);
        Assert.Equal(1, result.Value.PanelIndex);
    }

    [Fact]
    public void FindPanel_ReturnsNullWhenNotPlaced()
    {
        var layout = WorkspaceLayout.CreateDefault();
        Assert.Null(layout.FindPanel("nonexistent"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Cross-column: all 10 possible moves (4 columns = 4×3)
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    // Left → Right-0, Left → Right-1, Left → Bottom
    [InlineData("left", "right-0")]
    [InlineData("left", "right-1")]
    [InlineData("left", "bottom")]
    // Right-0 → Left, Right-0 → Right-1, Right-0 → Bottom
    [InlineData("right-0", "left")]
    [InlineData("right-0", "right-1")]
    [InlineData("right-0", "bottom")]
    // Right-1 → Left, Right-1 → Right-0, Right-1 → Bottom
    [InlineData("right-1", "left")]
    [InlineData("right-1", "right-0")]
    [InlineData("right-1", "bottom")]
    // Bottom → Left, Bottom → Right-0, Bottom → Right-1
    [InlineData("bottom", "left")]
    [InlineData("bottom", "right-0")]
    [InlineData("bottom", "right-1")]
    public void MovePanel_AllCrossColumnMoves(string from, string to)
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("x", "X", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        // Ensure 2 right columns for right-1 tests
        if (layout.RightColumns.Count < 2)
            layout.RightColumns.Add(new DockColumnLayout { Id = "right-1", PanelIds = [] });
        SetColumnPanels(layout.LeftColumn);
        SetColumnPanels(layout.RightColumns[0]);
        SetColumnPanels(layout.RightColumns[1]);
        SetColumnPanels(layout.BottomColumn);

        DockColumnLayout srcCol = from switch
        {
            "left" => layout.LeftColumn,
            "right-0" => layout.RightColumns[0],
            "right-1" => layout.RightColumns[1],
            "bottom" => layout.BottomColumn,
            _ => throw new ArgumentException()
        };
        srcCol.PanelIds = ["x"];

        // Remove from source
        srcCol.PanelIds.Remove("x");

        // Place in target
        DockColumnLayout dstCol = to switch
        {
            "left" => layout.LeftColumn,
            "right-0" => layout.RightColumns[0],
            "right-1" => layout.RightColumns[1],
            "bottom" => layout.BottomColumn,
            _ => throw new ArgumentException()
        };
        dstCol.PanelIds.Add("x");

        // Verify: target has it, source doesn't
        Assert.True(dstCol.ContainsPanel("x"));
        Assert.False(srcCol.ContainsPanel("x"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Tab group lifecycle: create, add, remove, dissolve
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void TabGroup_AddPanelToExistingTabGroup()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["a", "b"] } }
        };

        col.TabGroups["tab:0"].PanelIds.Add("c");
        Assert.Equal(["a", "b", "c"], col.TabGroups["tab:0"].PanelIds);
        Assert.True(col.ContainsPanel("c"));
    }

    [Fact]
    public void TabGroup_RemoveLeavesSingle_KeepsTabGroup()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["a", "b"] } }
        };

        col.RemovePanel("a");

        Assert.True(col.TabGroups.ContainsKey("tab:0"));
        Assert.Contains("tab:0", col.PanelIds);
        Assert.Equal(["b"], col.TabGroups["tab:0"].PanelIds);
        Assert.Single(col.PanelIds);
    }

    [Fact]
    public void TabGroup_AddThirdAndRemoveFirst_KeepsTabGroup()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["a", "b", "c"] } }
        };

        col.RemovePanel("a");
        Assert.True(col.TabGroups.ContainsKey("tab:0"));
        Assert.Equal(["b", "c"], col.TabGroups["tab:0"].PanelIds);
    }

    [Fact]
    public void TabGroup_CreateFromTwoSoloPanels()
    {
        var layout = WorkspaceLayout.CreateDefault();
        var col = layout.RightColumns[0];
        col.Rows = null;
        col.PanelIds = ["brush", "layers"];

        // Simulate tab-with drop: replace "layers" with tab group
        var tabKey = "tab:new";
        col.TabGroups[tabKey] = new TabGroupLayout
        {
            PanelIds = ["brush", "layers"],
            ActiveIndex = 1
        };
        col.PanelIds[0] = tabKey;  // Replace brush slot with tab key
        // Actually need to remove both panels and replace with tab key
        // Simpler: just verify resolved rows work
        col.PanelIds = [tabKey];
        col.TabGroups[tabKey] = new TabGroupLayout
        {
            PanelIds = ["brush", "layers"],
            ActiveIndex = 1
        };

        var rows = col.ResolvedRows();
        Assert.Single(rows);
        Assert.Equal(["brush", "layers"], rows[0].PanelIds);
        Assert.Equal(1, rows[0].ActiveTabIndex);
    }

    [Fact]
    public void TabGroup_ActiveIndexRespected()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:abc"],
            TabGroups = { ["tab:abc"] = new TabGroupLayout { PanelIds = ["x", "y", "z"], ActiveIndex = 2 } }
        };

        var rows = col.ResolvedRows();
        Assert.Single(rows);
        Assert.Equal(2, rows[0].ActiveTabIndex);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Horizontal split lifecycle
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HorizontalSplit_CreatesSideBySide()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["a", "b"], Orientation = DockOrientation.Horizontal }
            }
        };

        var rows = col.ResolvedRows();
        Assert.Single(rows);
        Assert.Equal(DockOrientation.Horizontal, rows[0].Orientation);
        Assert.Equal(["a", "b"], rows[0].PanelIds);
    }

    [Fact]
    public void HorizontalSplit_RemoveOne_ShrinksToSolo()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["a", "b"], Orientation = DockOrientation.Horizontal }
            }
        };

        col.RemovePanel("b");
        Assert.Single(col.Rows!);
        Assert.Single(col.Rows[0].PanelIds);
        Assert.Equal("a", col.Rows[0].PanelIds[0]);
    }

    [Fact]
    public void HorizontalSplit_RemoveAll_RemovesRow()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["a", "b"], Orientation = DockOrientation.Horizontal },
                new() { PanelIds = ["c"] }
            }
        };

        col.RemovePanel("a");
        col.RemovePanel("b");

        Assert.Single(col.Rows!);
        Assert.Equal("c", col.Rows[0].PanelIds[0]);
    }

    [Fact]
    public void HorizontalSplit_ConvertFromFlat()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["a", "b", "c"]
        };

        // Convert to rows with a horizontal split on "a"
        var resolved = col.ResolvedRows();
        col.Rows = resolved.Select(r => new DockRowLayout
        {
            PanelIds = r.PanelIds.ToList(),
            Orientation = r.Orientation,
            ActiveIndex = r.ActiveTabIndex
        }).ToList();

        // Make the first row horizontal with an extra panel
        col.Rows[0].PanelIds.Add("d");
        col.Rows[0].Orientation = DockOrientation.Horizontal;

        Assert.Equal(3, col.Rows.Count);
        Assert.Equal(DockOrientation.Horizontal, col.Rows[0].Orientation);
        Assert.Equal(["a", "d"], col.Rows[0].PanelIds);
        Assert.True(col.ContainsPanel("d"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Layout: floating ↔ docked transitions
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Floating_Detach_SetsIsFloating()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("layers", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["layers"];

        // Detach: mark floating
        layout.FloatingPanels["layers"] = new FloatingPanelState { IsFloating = true };

        Assert.True(layout.IsFloating("layers"));
    }

    [Fact]
    public void Floating_Dock_ClearsIsFloating()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("layers", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.FloatingPanels["layers"] = new FloatingPanelState { IsFloating = true };

        layout.RightColumns[0].PanelIds.Add("layers");
        layout.FloatingPanels["layers"].IsFloating = false;

        Assert.False(layout.IsFloating("layers"));
        Assert.True(layout.RightColumns[0].ContainsPanel("layers"));
    }

    [Fact]
    public void Floating_IsVisibleWhenFloating()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["a"];
        layout.FloatingPanels["a"] = new FloatingPanelState { IsFloating = true };

        Assert.True(layout.IsFloating("a"));
        Assert.True(layout.IsVisible("a"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Layout: visibility and hidden panels
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void HiddenPanel_IsNotVisible()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["a"];
        layout.HiddenPanelIds.Add("a");

        Assert.False(layout.IsVisible("a"));
    }

    [Fact]
    public void HiddenPanel_StillInLayoutData()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.RightColumns[0], "a");
        layout.HiddenPanelIds.Add("a");

        Assert.True(layout.RightColumns[0].ContainsPanel("a"));
        Assert.False(layout.IsVisible("a"));
    }

    [Fact]
    public void UnhiddenPanel_IsVisibleAgain()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["a"];
        layout.HiddenPanelIds.Add("a");
        layout.HiddenPanelIds.Remove("a");

        Assert.True(layout.IsVisible("a"));
    }

    [Fact]
    public void IsVisible_UnreachablePanel_ReturnsFalse()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("history", "Undo History", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.RemovePanel("history");
        layout.HiddenPanelIds.Remove("history");

        Assert.False(layout.IsPanelReachable("history"));
        Assert.False(layout.IsVisible("history"));
    }

    [Fact]
    public void RepairLayoutIntegrity_MigratesStrayPanelIdsIntoRows()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("histogram", "Histogram", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = new WorkspaceLayout
        {
            RightColumns =
            [
                new DockColumnLayout
                {
                    Id = "right-0",
                    Rows = [new DockRowLayout { PanelIds = ["layers"] }],
                    PanelIds = ["histogram"]
                }
            ]
        };

        Assert.False(layout.IsPanelReachable("histogram"));

        layout.RepairLayoutIntegrity();

        Assert.True(layout.IsPanelReachable("histogram"));
        Assert.DoesNotContain("histogram", layout.RightColumns[0].PanelIds);
    }

    [Fact]
    public void EnsurePanelPlaced_ActivatesTabInGroup()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("history", "Undo History", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.HiddenPanelIds.Add("history");
        layout.RightColumns[0].TabGroups["tab:navigator"].ActiveIndex = 0;

        layout.EnsurePanelPlaced("history");

        Assert.Equal(1, layout.RightColumns[0].TabGroups["tab:navigator"].ActiveIndex);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Layout: rapid panel moves, insert-at-position, multi-column consistency
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void PanelInsert_AtSpecificPosition()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["a", "b"];

        layout.RightColumns[0].PanelIds.Insert(0, "c");

        Assert.Equal(["c", "a", "b"], layout.RightColumns[0].PanelIds);
    }

    [Fact]
    public void PanelInsert_AtEnd()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["a", "b"];

        layout.RightColumns[0].PanelIds.Insert(2, "c");

        Assert.Equal(["a", "b", "c"], layout.RightColumns[0].PanelIds);
    }

    [Fact]
    public void RapidMove_BackAndForth_NoDuplicates()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("x", "", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.LeftColumn);
        SetColumnPanels(layout.RightColumns[0]);
        SetColumnPanels(layout.BottomColumn);

        layout.LeftColumn.PanelIds = ["x"];
        layout.RemovePanel("x");
        layout.RightColumns[0].PanelIds.Add("x");
        Assert.False(layout.LeftColumn.ContainsPanel("x"));
        Assert.True(layout.RightColumns[0].ContainsPanel("x"));

        // Right-0 → Left
        layout.RemovePanel("x");
        layout.LeftColumn.PanelIds.Add("x");
        Assert.True(layout.LeftColumn.ContainsPanel("x"));
        Assert.False(layout.RightColumns[0].ContainsPanel("x"));

        // Left → Bottom
        layout.RemovePanel("x");
        layout.BottomColumn.PanelIds.Add("x");
        Assert.True(layout.BottomColumn.ContainsPanel("x"));
        Assert.False(layout.LeftColumn.ContainsPanel("x"));

        // Bottom → Right-0 (back)
        layout.RemovePanel("x");
        layout.RightColumns[0].PanelIds.Add("x");
        Assert.True(layout.RightColumns[0].ContainsPanel("x"));
        Assert.False(layout.BottomColumn.ContainsPanel("x"));
    }

    [Fact]
    public void RapidMove_BackAndForth_NoOrphans()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("x", "", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        for (var i = 0; i < 20; i++)
        {
            SetColumnPanels(layout.LeftColumn);
            SetColumnPanels(layout.RightColumns[0]);
            SetColumnPanels(layout.BottomColumn);

            var cols = new[] { layout.LeftColumn, layout.RightColumns[0], layout.BottomColumn };
            var from = cols[i % 3];
            var to = cols[(i + 1) % 3];

            from.PanelIds = ["x"];
            Assert.True(from.ContainsPanel("x"));

            // Remove from source, add to target
            from.PanelIds.Remove("x");
            to.PanelIds.Add("x");

            Assert.False(from.ContainsPanel("x"));
            Assert.True(to.ContainsPanel("x"));
            Assert.Null(layout.FindOrphanedPanel());
        }
    }

    [Fact]
    public void Layout_HasNoOrphansByDefault()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("node-graph", "", () => new TextBlock(), DefaultZone: "bottom"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.Normalize(PanelRegistry.AllIds);

        Assert.Null(layout.FindOrphanedPanel());
    }

    [Fact]
    public void TabGroup_PanelInTabButNotInPanelIds_IsOrphanUntilSanitized()
    {
        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.RightColumns[0]);
        layout.RightColumns[0].TabGroups["ghost"] = new TabGroupLayout
        {
            PanelIds = ["orphan"]
        };

        Assert.Equal("orphan", layout.FindOrphanedPanel());

        layout.RepairLayoutIntegrity();

        Assert.Null(layout.FindOrphanedPanel());
        Assert.False(layout.RightColumns[0].TabGroups.ContainsKey("ghost"));
    }

    [Fact]
    public void RepairLayoutIntegrity_RestoresMissingTabKeys()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds.Remove("tab:color");

        layout.RepairLayoutIntegrity();

        Assert.Contains("tab:color", layout.RightColumns[0].PanelIds);
        Assert.Null(layout.FindOrphanedPanel());
    }

    [Fact]
    public void RepairLayoutIntegrity_RenamesColorSlidersPanelId()
    {
        var layout = WorkspaceLayout.CreateDefault();
        var colorTab = layout.RightColumns[0].TabGroups["tab:color"];
        colorTab.PanelIds = colorTab.PanelIds
            .Select(id => id == "color-slider" ? "color-sliders" : id)
            .ToList();
        layout.HiddenPanelIds.Remove("color-slider");
        layout.HiddenPanelIds.Add("color-sliders");

        layout.RepairLayoutIntegrity();

        Assert.Contains("color-slider", colorTab.PanelIds);
        Assert.DoesNotContain("color-sliders", colorTab.PanelIds);
        Assert.Contains("color-slider", layout.HiddenPanelIds);
        Assert.DoesNotContain("color-sliders", layout.HiddenPanelIds);
    }

    [Fact]
    public void RepairLayoutIntegrity_PreservesFragmentedToolkitLeftColumns()
    {
        var layout = new WorkspaceLayout
        {
            LayoutVersion = 8,
            LeftColumns =
            [
                new DockColumnLayout
                {
                    Id = "left-0",
                    Rows = [new DockRowLayout { PanelIds = ["tools"] }]
                },
                new DockColumnLayout
                {
                    Id = "left-1",
                    Rows = [new DockRowLayout { PanelIds = ["tool-properties"] }]
                },
                new DockColumnLayout
                {
                    Id = "left-2",
                    Rows = [new DockRowLayout { PanelIds = ["brush"] }]
                },
            ]
        };

        layout.RepairLayoutIntegrity();

        Assert.Equal(3, layout.LeftColumns.Count);
    }

    [Fact]
    public void MigrateV8_ConsolidatesFragmentedToolkitLeftColumns()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("tool-properties", "", () => new TextBlock(), DefaultZone: "left"));

        var layout = new WorkspaceLayout
        {
            LayoutVersion = 7,
            LeftColumns =
            [
                new DockColumnLayout
                {
                    Id = "left-0",
                    Rows = [new DockRowLayout { PanelIds = ["tools"] }]
                },
                new DockColumnLayout
                {
                    Id = "left-1",
                    Rows = [new DockRowLayout { PanelIds = ["tool-properties"] }]
                },
                new DockColumnLayout
                {
                    Id = "left-2",
                    Rows = [new DockRowLayout { PanelIds = ["brush"] }]
                },
            ]
        };

        layout.Normalize(PanelRegistry.AllIds);

        Assert.Single(layout.LeftColumns);
        Assert.Contains("tab:left", layout.LeftColumns[0].PanelIds);
        var tab = layout.LeftColumns[0].TabGroups["tab:left"];
        Assert.Equal(["tools", "brush", "tool-properties"], tab.PanelIds);
    }

    [Fact]
    public void RepairLayoutIntegrity_DoesNotConsolidateMixedLeftColumns()
    {
        var layout = new WorkspaceLayout
        {
            LeftColumns =
            [
                new DockColumnLayout
                {
                    Id = "left-0",
                    Rows = [new DockRowLayout { PanelIds = ["tools"] }]
                },
                new DockColumnLayout
                {
                    Id = "left-1",
                    Rows = [new DockRowLayout { PanelIds = ["history"] }]
                },
            ]
        };

        layout.RepairLayoutIntegrity();

        Assert.Equal(2, layout.LeftColumns.Count);
    }

    [Fact]
    public void EnsurePanelPlaced_MakesHiddenPanelReachable()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("history", "Undo History", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("layers", "Layers", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.RemovePanel("history");
        layout.HiddenPanelIds.Add("history");

        Assert.False(layout.IsPanelReachable("history"));

        layout.EnsurePanelPlaced("history");

        Assert.False(layout.HiddenPanelIds.Contains("history"));
        Assert.True(layout.IsPanelReachable("history"));
    }

    [Fact]
    public void TabGroup_PanelInRowsButNotInTab_PanelIsNotOrphan()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["a", "b"], Orientation = DockOrientation.Horizontal }
            },
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["c"] } }
        };

        // Rows model is authoritative — stale TabGroups do not trigger orphan detection.
        var layout = new WorkspaceLayout { RightColumns = [col] };
        Assert.Null(layout.FindOrphanedPanel());

        layout.RepairLayoutIntegrity();

        Assert.Null(layout.FindOrphanedPanel());
        Assert.Empty(layout.RightColumns[0].TabGroups);
    }

    [Fact]
    public void TabGroup_OrphanedInRowsMode_StaleTabGroupsIgnoredUntilRepair()
    {
        var col = new DockColumnLayout
        {
            Rows = new List<DockRowLayout>
            {
                new() { PanelIds = ["a"] }
            },
            TabGroups = { ["tab:orphan"] = new TabGroupLayout { PanelIds = ["orphan"] } }
        };

        var layout = new WorkspaceLayout
        {
            RightColumns = [col, new() { PanelIds = [] }]
        };

        Assert.Null(layout.FindOrphanedPanel());

        layout.RepairLayoutIntegrity();

        Assert.Null(layout.FindOrphanedPanel());
        Assert.Equal(["a"], layout.RightColumns[0].Rows![0].PanelIds);
    }

    [Fact]
    public void Normalize_PreservesCustomRowsLayoutAcrossRuns()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("tool-properties", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("color", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("layers", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = new WorkspaceLayout
        {
            LayoutVersion = 8,
            LeftColumns =
            [
                new DockColumnLayout
                {
                    Id = "left-0",
                    Rows =
                    [
                        new DockRowLayout { PanelIds = ["tools"] },
                        new DockRowLayout { PanelIds = ["brush"] },
                        new DockRowLayout { PanelIds = ["tool-properties"] },
                    ]
                }
            ],
            RightColumns =
            [
                new DockColumnLayout
                {
                    Id = "right-0",
                    Rows =
                    [
                        new DockRowLayout { PanelIds = ["color"] },
                        new DockRowLayout { PanelIds = ["layers"] },
                    ]
                }
            ]
        };

        layout.Normalize(PanelRegistry.AllIds);
        layout.Normalize(PanelRegistry.AllIds);

        Assert.Single(layout.LeftColumns);
        Assert.Equal(3, layout.LeftColumns[0].Rows!.Count);
        Assert.Equal(["tools"], layout.LeftColumns[0].Rows[0].PanelIds);
        Assert.Equal(["brush"], layout.LeftColumns[0].Rows[1].PanelIds);
        Assert.Equal(["tool-properties"], layout.LeftColumns[0].Rows[2].PanelIds);
        Assert.Equal(2, layout.RightColumns[0].Rows!.Count);
    }

    [Fact]
    public void RepairLayoutIntegrity_PreservesRowsLayoutWithStaleTabGroups()
    {
        var layout = new WorkspaceLayout
        {
            RightColumns =
            [
                new DockColumnLayout
                {
                    Id = "right-0",
                    Rows =
                    [
                        new DockRowLayout { PanelIds = ["color", "layers"], ActiveIndex = 1 },
                        new DockRowLayout { PanelIds = ["overview"] }
                    ],
                    PanelIds = ["tab:color", "tab:layers"],
                    TabGroups = new Dictionary<string, TabGroupLayout>
                    {
                        ["tab:color"] = new() { PanelIds = ["color", "color-slider", "layer-properties"], ActiveIndex = 0 },
                        ["tab:layers"] = new() { PanelIds = ["layers"], ActiveIndex = 0 }
                    }
                }
            ]
        };

        layout.RepairLayoutIntegrity();

        Assert.Null(layout.FindOrphanedPanel());
        Assert.Equal(2, layout.RightColumns[0].Rows!.Count);
        Assert.Equal(["color", "layers"], layout.RightColumns[0].Rows[0].PanelIds);
        Assert.Empty(layout.RightColumns[0].TabGroups);
        Assert.Empty(layout.RightColumns[0].PanelIds);
    }

    [Fact]
    public void DockLayoutOps_ResolveTabInsertIndex_UsesTabMidpoints()
    {
        var tabs = new List<(double Left, double Right)>
        {
            (0, 40),
            (40, 100),
            (100, 160)
        };

        Assert.Equal(0, DockLayoutOps.ResolveTabInsertIndex(tabs, 10));
        Assert.Equal(1, DockLayoutOps.ResolveTabInsertIndex(tabs, 50));
        Assert.Equal(2, DockLayoutOps.ResolveTabInsertIndex(tabs, 110));
        Assert.Equal(3, DockLayoutOps.ResolveTabInsertIndex(tabs, 200));
    }

    [Fact]
    public void DockLayoutOps_ApplyMergeTab_InsertsAtIndex()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].Rows =
        [
            new DockRowLayout { PanelIds = ["layers", "color"], Orientation = DockOrientation.Vertical }
        ];
        layout.RightColumns[0].PanelIds = [];
        layout.RightColumns[0].TabGroups.Clear();

        DockLayoutOps.ApplyMergeTab(
            layout,
            "brush",
            DockColumnIndices.Right(0),
            ["layers", "color"],
            insertIndex: 1);

        Assert.Equal(["layers", "brush", "color"], layout.RightColumns[0].Rows![0].PanelIds);
    }

    [Fact]
    public void DockLayoutOps_ApplyMergeTab_ReordersWithinSameRow()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].Rows =
        [
            new DockRowLayout { PanelIds = ["layers", "color", "brush"], Orientation = DockOrientation.Vertical }
        ];
        layout.RightColumns[0].PanelIds = [];
        layout.RightColumns[0].TabGroups.Clear();

        DockLayoutOps.ApplyMergeTab(
            layout,
            "brush",
            DockColumnIndices.Right(0),
            ["layers", "color", "brush"],
            insertIndex: 0);

        Assert.Equal(["brush", "layers", "color"], layout.RightColumns[0].Rows![0].PanelIds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Layout: edge cases with empty columns
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EmptyColumn_ContainsPanel_ReturnsFalse()
    {
        var col = new DockColumnLayout { PanelIds = [] };
        Assert.False(col.ContainsPanel("anything"));
    }

    [Fact]
    public void EmptyColumn_ResolvedRows_ReturnsEmpty()
    {
        var col = new DockColumnLayout { PanelIds = [] };
        Assert.Empty(col.ResolvedRows());
    }

    [Fact]
    public void EmptyColumn_RemovePanel_NoOp()
    {
        var col = new DockColumnLayout { PanelIds = [] };
        col.RemovePanel("nonexistent");
        Assert.Empty(col.PanelIds);
    }

    [Fact]
    public void Layout_FindPanelInEmptyColumn_ReturnsNull()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds.Clear();
        Assert.Null(layout.FindPanel("anything"));
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Clone deep-copy coverage
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Clone_PreservesBottomColumn()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.BottomColumn.PanelIds = ["node-graph", "extra"];

        var clone = layout.Clone();
        Assert.Equal(["node-graph", "extra"], clone.BottomColumn.PanelIds);
    }

    [Fact]
    public void Clone_PreservesTabGroups()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["tab:x"];
        layout.RightColumns[0].TabGroups["tab:x"] = new TabGroupLayout
        {
            PanelIds = ["a", "b"],
            ActiveIndex = 1
        };

        var clone = layout.Clone();
        Assert.Equal(1, clone.RightColumns[0].TabGroups["tab:x"].ActiveIndex);
        Assert.Equal(["a", "b"], clone.RightColumns[0].TabGroups["tab:x"].PanelIds);
    }

    [Fact]
    public void Clone_PreservesRows()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].Rows = new List<DockRowLayout>
        {
            new() { PanelIds = ["a", "b"], Orientation = DockOrientation.Horizontal, ActiveIndex = 0 }
        };

        var clone = layout.Clone();
        Assert.Equal(DockOrientation.Horizontal, clone.RightColumns[0].Rows![0].Orientation);
        Assert.Equal(["a", "b"], clone.RightColumns[0].Rows[0].PanelIds);
    }

    [Fact]
    public void Clone_PreservesFloatingPanels()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.FloatingPanels["x"] = new FloatingPanelState
        {
            IsFloating = true, X = 42, Y = 99, Width = 400, Height = 300
        };

        var clone = layout.Clone();
        var fp = clone.FloatingPanels["x"];
        Assert.True(fp.IsFloating);
        Assert.Equal(42, fp.X);
        Assert.Equal(99, fp.Y);
        Assert.Equal(400, fp.Width);
        Assert.Equal(300, fp.Height);
    }

    [Fact]
    public void Clone_PreservesHiddenPanels()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.HiddenPanelIds.Add("brush");
        layout.HiddenPanelIds.Add("layers");

        var clone = layout.Clone();
        Assert.Contains("brush", clone.HiddenPanelIds);
        Assert.Contains("layers", clone.HiddenPanelIds);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Normalize: pan placement from scratch (no config derangement)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Normalize_EmptyLayout_PlacesAllPanels()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("layers", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("color", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("node-graph", "", () => new TextBlock(), DefaultZone: "bottom"));

        var layout = new WorkspaceLayout
        {
            HiddenPanelIds = [],
            LeftColumn = new DockColumnLayout { PanelIds = [] },
            RightColumns = [new() { PanelIds = [] }],
            BottomColumn = new DockColumnLayout { PanelIds = [] }
        };
        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.LeftColumn.ContainsPanel("tools"));
        Assert.True(layout.RightColumns[0].ContainsPanel("layers") || layout.RightColumns[0].ContainsPanel("brush"));
        Assert.True(layout.RightColumns[0].ContainsPanel("color"));
        Assert.True(layout.BottomColumn.ContainsPanel("node-graph"));
    }

    [Fact]
    public void Normalize_PreservesExistingTabGroups()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("b", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = WorkspaceLayout.CreateDefault();
        SetColumnPanels(layout.RightColumns[0], "tab:1");
        layout.RightColumns[0].TabGroups["tab:1"] = new TabGroupLayout { PanelIds = ["a", "b"] };

        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.RightColumns[0].ContainsPanel("a"));
        Assert.True(layout.RightColumns[0].ContainsPanel("b"));
    }

    [Fact]
    public void ApplyInsertRow_AdjustsIndexWhenMovingWithinSameColumn()
    {
        var col = new DockColumnLayout
        {
            Id = "left-1",
            Rows =
            [
                new DockRowLayout { PanelIds = ["brush"] },
                new DockRowLayout { PanelIds = ["tool-properties"] },
                new DockRowLayout { PanelIds = ["history"] },
            ]
        };
        var layout = new WorkspaceLayout { LeftColumns = [col] };

        DockLayoutOps.ApplyInsertRow(layout, "history", DockColumnIndices.Left(0), 1);

        Assert.Equal(["brush", "history", "tool-properties"], col.Rows!.Select(r => r.PanelIds[0]));
    }

    [Fact]
    public void DockLayoutOps_FindPlacement_TabMember()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.LayoutVersion = 5;
        layout.LeftColumn.Rows = null;
        layout.LeftColumn.PanelIds = ["tab:left"];
        layout.LeftColumn.TabGroups = new Dictionary<string, TabGroupLayout>
        {
            ["tab:left"] = new() { PanelIds = ["brush", "tool-properties"], ActiveIndex = 0 }
        };
        layout.Normalize(PanelRegistry.AllIds);

        var brush = DockLayoutOps.FindPlacement(layout, "brush");
        Assert.NotNull(brush);
        Assert.True(brush!.IsTabMember);
        Assert.Equal("tab:left", brush.TabGroupKey);

        Assert.True(DockLayoutOps.MovePanel(layout, "brush", 1));
        var after = DockLayoutOps.FindPlacement(layout, "brush");
        Assert.NotNull(after);
        Assert.Equal(1, after!.IndexInTab);
    }

    [Fact]
    public void Normalize_RevertsForcedHorizontalV5ToTabLeft()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.LayoutVersion = 5;
        layout.LeftColumn.PanelIds = [];
        layout.LeftColumn.TabGroups = new Dictionary<string, TabGroupLayout>();
        layout.LeftColumn.Rows =
        [
            new DockRowLayout
            {
                Orientation = DockOrientation.Horizontal,
                PanelIds = ["tools", "brush", "tool-properties"]
            }
        ];

        layout.Normalize(PanelRegistry.AllIds);

        Assert.Equal(8, layout.LayoutVersion);
        Assert.Contains("tab:left", layout.LeftColumn.PanelIds);
        Assert.True(layout.LeftColumn.TabGroups["tab:left"].PanelIds.Contains("brush"));
    }

    [Fact]
    public void DockLayoutOps_ApplyInsertDockColumn_AddsBottomStack()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.BottomColumns =
        [
            new DockColumnLayout { Id = "bottom-0", Rows = [new DockRowLayout { PanelIds = ["node-graph"] }] }
        ];

        DockLayoutOps.ApplyInsertDockColumn(layout, "layers", DockZone.Bottom, insertColumnIndex: 1);

        Assert.Equal(2, layout.BottomColumns.Count);
        Assert.Contains("layers", layout.BottomColumns[1].Rows![0].PanelIds);
    }

    [Fact]
    public void DockLayoutOps_ApplyInsertDockColumn_AddsLeftStackBesideCanvas()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.LeftColumns =
        [
            new DockColumnLayout
            {
                Id = "left-0",
                Rows = [new DockRowLayout { PanelIds = ["tools"] }]
            }
        ];

        DockLayoutOps.ApplyInsertDockColumn(layout, "brush", DockZone.Left, insertColumnIndex: 1);

        Assert.Equal(2, layout.LeftColumns.Count);
        Assert.Contains("brush", layout.LeftColumns[1].Rows![0].PanelIds);
    }

    [Fact]
    public void Normalize_RepairsTabKeysStrippedFromPanelIds()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("tool-properties", "", () => new TextBlock(), DefaultZone: "left"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.LeftColumn.PanelIds = [];
        layout.LeftColumn.TabGroups = new Dictionary<string, TabGroupLayout>
        {
            ["tab:left"] = new() { PanelIds = ["brush", "tool-properties"], ActiveIndex = 0 }
        };

        layout.Normalize(PanelRegistry.AllIds);

        Assert.Contains("tab:left", layout.LeftColumn.PanelIds);
        Assert.NotEmpty(layout.LeftColumn.ResolvedRows());
        Assert.Null(layout.FindOrphanedPanel());
    }

    [Fact]
    public void Normalize_DontDuplicatePanels_PlacedOnce()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("a", "", () => new TextBlock()));
        PanelRegistry.Register(new DockPanelDef("b", "", () => new TextBlock()));
        PanelRegistry.Register(new DockPanelDef("c", "", () => new TextBlock()));
        PanelRegistry.Register(new DockPanelDef("d", "", () => new TextBlock()));

        var layout = WorkspaceLayout.CreateDefault();
        foreach (var c in layout.LeftColumns)
            SetColumnPanels(c);
        foreach (var c in layout.RightColumns)
            SetColumnPanels(c);
        foreach (var c in layout.BottomColumns)
            SetColumnPanels(c);

        layout.Normalize(PanelRegistry.AllIds);

        var total = layout.LeftColumns.Sum(CountPlacedInColumn)
            + layout.RightColumns.Sum(CountPlacedInColumn)
            + layout.BottomColumns.Sum(CountPlacedInColumn);
        Assert.Equal(PanelRegistry.AllIds.Count, total);
    }

    [Fact]
    public void CreateDefault_LoadsBundledLayout()
    {
        var layout = WorkspaceLayout.CreateDefault();
        Assert.Equal(8, layout.LayoutVersion);
        Assert.Single(layout.LeftColumns);
        Assert.Contains("tab:left", layout.LeftColumns[0].PanelIds);
        Assert.Contains("brush", layout.LeftColumns[0].TabGroups["tab:left"].PanelIds);
        Assert.Contains("tab:color", layout.RightColumns[0].PanelIds);
        Assert.Contains("color", layout.RightColumns[0].TabGroups["tab:color"].PanelIds);
        Assert.Contains("node-graph", layout.HiddenPanelIds);
        Assert.DoesNotContain("overview", layout.HiddenPanelIds);
        Assert.True(layout.RightColumns[0].ContainsPanel("overview"));
    }

    [Fact]
    public void Normalize_MigratesSoloRowsIntoTabStacks()
    {
        var layout = new WorkspaceLayout
        {
            LayoutVersion = 7,
            RightColumns =
            [
                new DockColumnLayout
                {
                    Id = "right-0",
                    Rows =
                    [
                        new DockRowLayout { PanelIds = ["color"] },
                        new DockRowLayout { PanelIds = ["color-slider"] },
                        new DockRowLayout { PanelIds = ["layer-properties"] },
                        new DockRowLayout { PanelIds = ["layers"] },
                    ]
                }
            ]
        };

        layout.Normalize(PanelRegistry.AllIds);

        Assert.Equal(8, layout.LayoutVersion);
        Assert.Null(layout.RightColumns[0].Rows);
        Assert.Contains("tab:color", layout.RightColumns[0].PanelIds);
        Assert.Equal(3, layout.RightColumns[0].TabGroups["tab:color"].PanelIds.Count);
    }

    [Fact]
    public void Normalize_PlacesNewPanelWhenColumnUsesRows()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("layers", "Layers", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("overview", "Navigator", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = new WorkspaceLayout
        {
            LayoutVersion = 7,
            RightColumns =
            [
                new DockColumnLayout
                {
                    Id = "right-0",
                    Rows = [new DockRowLayout { PanelIds = ["layers"] }]
                }
            ]
        };

        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.RightColumns[0].ContainsPanel("overview"));
        Assert.Equal(2, layout.RightColumns[0].Rows!.Count);
        Assert.Equal(["overview"], layout.RightColumns[0].Rows[^1].PanelIds);
    }

    [Fact]
    public void DockRowLayout_SingleVerticalPanel_IsTabGroup()
    {
        var row = new DockRowLayout { PanelIds = ["layers"], Orientation = DockOrientation.Vertical };
        Assert.True(row.IsTabGroup);
    }

    [Fact]
    public void DockLayoutOps_ApplyMergeTab_CrossZone_LeftToRight()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.LeftColumns[0].Rows =
        [
            new DockRowLayout { PanelIds = ["brush"], Orientation = DockOrientation.Vertical }
        ];
        layout.LeftColumns[0].PanelIds = [];
        layout.LeftColumns[0].TabGroups.Clear();
        layout.RightColumns[0].Rows =
        [
            new DockRowLayout { PanelIds = ["layers"], Orientation = DockOrientation.Vertical }
        ];
        layout.RightColumns[0].PanelIds = [];
        layout.RightColumns[0].TabGroups.Clear();

        DockLayoutOps.ApplyMergeTab(layout, "brush", DockColumnIndices.Right(0), mergeTabRowIndex: 0);

        Assert.DoesNotContain("brush", layout.LeftColumns[0].ResolvedRows().SelectMany(r => r.PanelIds));
        Assert.Contains("brush", layout.RightColumns[0].Rows![0].PanelIds);
    }

    [Fact]
    public void DockLayoutOps_ApplyMergeTab_AddsToSoloVerticalRow()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].Rows =
        [
            new DockRowLayout { PanelIds = ["layers"], Orientation = DockOrientation.Vertical },
            new DockRowLayout { PanelIds = ["color"], Orientation = DockOrientation.Vertical }
        ];

        DockLayoutOps.ApplyMergeTab(layout, "brush", DockColumnIndices.Right(0), mergeTabRowIndex: 0);

        Assert.Equal(["layers", "brush"], layout.RightColumns[0].Rows![0].PanelIds);
    }

    [Fact]
    public void DockDropBands_NarrowBody_DoesNotExceedMax()
    {
        // Regression: body width 62 → max edge 27.9; old code used Math.Clamp(..., 40, 27.9) and crashed.
        var edgeW = DockDropBands.BandSize(62, 0.28, 40, 0.45);
        Assert.InRange(edgeW, 0, 62 * 0.45);

        var edgeH = DockDropBands.BandSize(40, 0.14, 20, 0.35);
        Assert.InRange(edgeH, 0, 40 * 0.35);
    }

    private static int CountPlacedInColumn(DockColumnLayout col)
    {
        return col.ResolvedRows().SelectMany(r => r.PanelIds).Count();
    }
}
