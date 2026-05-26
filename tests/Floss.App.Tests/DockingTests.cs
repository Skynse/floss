using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;

namespace Floss.App.Tests;

public class DockingTests
{
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
    public void Column_RemovePanel_DissolvesTabGroup_WhenOneRemains()
    {
        var col = new DockColumnLayout
        {
            PanelIds = ["tab:0"],
            TabGroups = { ["tab:0"] = new TabGroupLayout { PanelIds = ["brush", "layers"] } }
        };
        col.RemovePanel("brush");

        // Tab group dissolved — "layers" is now a solo panel
        Assert.False(col.TabGroups.ContainsKey("tab:0"));
        Assert.Contains("layers", col.PanelIds);
        Assert.DoesNotContain("tab:0", col.PanelIds);
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

    // ═══════════════════════════════════════════════════════════════════════════
    // WorkspaceLayout
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Layout_Default_HasThreeColumns()
    {
        var layout = WorkspaceLayout.CreateDefault();
        Assert.Equal(2, layout.RightColumns.Count);
        Assert.Equal("left", layout.LeftColumn.Id);
        Assert.Equal("bottom", layout.BottomColumn.Id);
    }

    [Fact]
    public void Layout_Default_AllKnownPanelsPlaced()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "Tools", () => new TextBlock(), DefaultZone: "left"));
        PanelRegistry.Register(new DockPanelDef("brush", "Brush", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("node-graph", "Node Graph", () => new TextBlock(), DefaultZone: "bottom"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.LeftColumn.ContainsPanel("tools"));
        Assert.True(layout.RightColumns.Any(c => c.ContainsPanel("brush")));
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
        Assert.Equal(-2, result.Value.ColumnIndex);
    }

    [Fact]
    public void Layout_FindPanel_FindsInRightColumn()
    {
        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["brush"];
        layout.RightColumns[1].PanelIds = ["color"];

        var br = layout.FindPanel("brush");
        Assert.NotNull(br);
        Assert.Equal(0, br.Value.ColumnIndex);

        var co = layout.FindPanel("color");
        Assert.NotNull(co);
        Assert.Equal(1, co.Value.ColumnIndex);
    }

    [Fact]
    public void Layout_RemovePanel_RemovesFromAll()
    {
        var layout = new WorkspaceLayout
        {
            LeftColumn = new DockColumnLayout { PanelIds = ["tools"] },
            RightColumns = [new() { PanelIds = ["brush", "layers"] }]
        };
        layout.RemovePanel("brush");

        Assert.False(layout.LeftColumn.ContainsPanel("brush"));
        Assert.False(layout.RightColumns.Any(c => c.ContainsPanel("brush")));
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
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "right-0"));

        var layout = new WorkspaceLayout
        {
            LeftColumn = new DockColumnLayout { PanelIds = ["ghost", "brush"] }
        };
        layout.Normalize(PanelRegistry.AllIds);

        // "ghost" is unknown — pruned
        Assert.DoesNotContain("ghost", layout.LeftColumn.PanelIds);
        // "brush" belongs in right-0, moved there by dedup
        Assert.False(layout.LeftColumn.ContainsPanel("brush"));
        Assert.True(layout.RightColumns[0].ContainsPanel("brush"));
    }

    [Fact]
    public void Normalize_Deduplicates_KeepsInDefaultZone()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("node-graph", "", () => new TextBlock(), DefaultZone: "bottom"));

        // Simulate old config where node-graph is in both bottom and right
        var layout = new WorkspaceLayout
        {
            BottomColumn = new DockColumnLayout { PanelIds = ["node-graph"] },
            RightColumns = [new() { PanelIds = ["node-graph"] }, new() { PanelIds = [] }]
        };
        layout.Normalize(PanelRegistry.AllIds);

        // Should be removed from right column (kept in bottom)
        Assert.True(layout.BottomColumn.ContainsPanel("node-graph"));
        Assert.False(layout.RightColumns[0].ContainsPanel("node-graph"));
    }

    [Fact]
    public void Normalize_Deduplicates_KeepsInLeft()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("tools", "", () => new TextBlock(), DefaultZone: "left"));

        var layout = new WorkspaceLayout
        {
            LeftColumn = new DockColumnLayout { PanelIds = ["tools"] },
            RightColumns = [new() { PanelIds = ["tools"] }, new() { PanelIds = [] }]
        };
        layout.Normalize(PanelRegistry.AllIds);

        Assert.True(layout.LeftColumn.ContainsPanel("tools"));
        Assert.False(layout.RightColumns[0].ContainsPanel("tools"));
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
        PanelRegistry.Register(new DockPanelDef("color", "Color", () => new TextBlock(), DefaultZone: "right-1", Proportion: 0.35));

        // Simulate "Reset Layout": clear then normalize fresh
        var layout = new WorkspaceLayout
        {
            // Empty: no panels placed
            LeftColumn = new DockColumnLayout { PanelIds = [] },
            RightColumns = [new() { PanelIds = [] }, new() { PanelIds = [] }]
        };
        layout.Normalize(PanelRegistry.AllIds);

        // All panels should be placed in their default zones
        Assert.True(layout.LeftColumn.ContainsPanel("tools"));
        Assert.True(layout.RightColumns[0].ContainsPanel("brush"));
        Assert.True(layout.RightColumns[1].ContainsPanel("color"));
        Assert.Null(layout.FloatingPanels.GetValueOrDefault("tools"));
    }

    [Fact]
    public void Layout_MovePanelBetweenColumns_UpdateIsConsistent()
    {
        PanelRegistry.Clear();
        PanelRegistry.Register(new DockPanelDef("brush", "", () => new TextBlock(), DefaultZone: "right-0"));
        PanelRegistry.Register(new DockPanelDef("color", "", () => new TextBlock(), DefaultZone: "right-1"));

        var layout = WorkspaceLayout.CreateDefault();
        layout.RightColumns[0].PanelIds = ["brush"];
        layout.RightColumns[1].PanelIds = ["color"];

        // Move "brush" from right-0 to left column
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
        PanelRegistry.Register(new DockPanelDef("color", "", () => new TextBlock(), DefaultZone: "right-1", Proportion: 0.35));

        var defaults = WorkspaceLayout.CreateDefault();
        defaults.Normalize(PanelRegistry.AllIds);

        // Verify all panels are placed exactly once
        var allPlacements = new HashSet<string>();
        foreach (var id in PanelRegistry.AllIds)
        {
            var p = defaults.FindPanel(id);
            Assert.NotNull(p);
            allPlacements.Add(id);

            // Verify not in multiple columns
            var count = (defaults.LeftColumn.ContainsPanel(id) ? 1 : 0)
                + defaults.RightColumns.Count(c => c.ContainsPanel(id))
                + (defaults.BottomColumn.ContainsPanel(id) ? 1 : 0)
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
        layout.RightColumns[1].PanelIds.Clear();

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

        // Remove second → 1 left, tab group dissolves, solo remains
        col.RemovePanel("b");
        Assert.False(col.TabGroups.ContainsKey("tab:0"));
        Assert.Contains("c", col.PanelIds);
        Assert.DoesNotContain("tab:0", col.PanelIds);
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
}
