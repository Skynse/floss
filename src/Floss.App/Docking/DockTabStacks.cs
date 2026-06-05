using System;
using System.Collections.Generic;
using System.Linq;

namespace Floss.App.Docking;

/// <summary>
/// Photoshop-style tab groups: merge related flat panel rows into one tabbed strip.
/// </summary>
internal static class DockTabStacks
{
    internal readonly record struct TabStackDefinition(string Key, string[] Members);

    internal static readonly TabStackDefinition[] Known =
    [
        new("tab:color", ["color", "color-slider", "layer-properties"]),
        new("tab:layers", ["layers"]),
        new("tab:left", ["tools", "brush", "tool-properties"]),
    ];

    /// <summary>
    /// True when two or more members of a known stack appear as separate column entries (not grouped).
    /// </summary>
    public static bool NeedsCompaction(DockColumnLayout col)
    {
        var flat = FlatPanelIds(col);
        foreach (var stack in Known)
        {
            var present = stack.Members.Where(flat.Contains).ToList();
            if (present.Count < 2) continue;

            if (col.TabGroups.Values.Any(t =>
                    t.PanelIds.Count >= 2 &&
                    present.All(p => t.PanelIds.Contains(p))))
                continue;

            return true;
        }

        return false;
    }

    public static void Compact(DockColumnLayout col)
    {
        if (col.Rows is { Count: > 0 })
        {
            col.PanelIds = col.Rows.SelectMany(r => r.PanelIds).Distinct().ToList();
            col.Rows = null;
        }

        var flat = col.PanelIds.ToList();
        var tabGroups = new Dictionary<string, TabGroupLayout>(col.TabGroups, StringComparer.Ordinal);
        var newOrder = new List<string>();
        var consumed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var stack in Known)
        {
            var present = stack.Members.Where(flat.Contains).ToList();
            if (present.Count < 2) continue;

            var existingKey = col.PanelIds.FirstOrDefault(p =>
                tabGroups.TryGetValue(p, out var t) &&
                present.All(m => t.PanelIds.Contains(m)) &&
                t.PanelIds.Count == present.Count);

            if (existingKey != null)
            {
                if (!newOrder.Contains(existingKey))
                    newOrder.Add(existingKey);
                foreach (var p in present) consumed.Add(p);
                continue;
            }

            var active = 0;
            if (tabGroups.TryGetValue(stack.Key, out var prev))
            {
                var idx = prev.PanelIds.IndexOf(
                    prev.ActiveIndex >= 0 && prev.ActiveIndex < prev.PanelIds.Count
                        ? prev.PanelIds[prev.ActiveIndex]
                        : present[0]);
                active = idx >= 0 ? idx : 0;
            }

            tabGroups[stack.Key] = new TabGroupLayout
            {
                PanelIds = present,
                ActiveIndex = Math.Clamp(active, 0, present.Count - 1)
            };
            newOrder.Add(stack.Key);
            foreach (var p in present) consumed.Add(p);
        }

        foreach (var id in flat)
        {
            if (consumed.Contains(id)) continue;

            if (tabGroups.ContainsKey(id))
            {
                if (!newOrder.Contains(id))
                    newOrder.Add(id);
                consumed.Add(id);
                continue;
            }

            newOrder.Add(id);
        }

        col.PanelIds = newOrder;
        col.TabGroups = tabGroups;
        col.RepairTabGroupPanelIds();
    }

    /// <summary>Place a new panel into its default tab stack when possible.</summary>
    public static void PlacePanel(DockColumnLayout col, string panelId)
    {
        if (col.ContainsPanel(panelId)) return;

        foreach (var stack in Known)
        {
            if (!stack.Members.Contains(panelId)) continue;

            var existingKey = col.PanelIds.FirstOrDefault(p =>
                col.TabGroups.TryGetValue(p, out var t) &&
                t.PanelIds.Any(m => stack.Members.Contains(m)));

            if (existingKey != null && col.TabGroups.TryGetValue(existingKey, out var tab))
            {
                if (!tab.PanelIds.Contains(panelId))
                    tab.PanelIds.Add(panelId);
                return;
            }

            var present = stack.Members.Where(m => col.ContainsPanel(m) || m == panelId).Distinct().ToList();
            if (present.Count >= 2)
            {
                foreach (var m in present)
                    col.RemovePanel(m);
                col.TabGroups[stack.Key] = new TabGroupLayout
                {
                    PanelIds = present,
                    ActiveIndex = Math.Max(0, present.IndexOf(panelId))
                };
                if (!col.PanelIds.Contains(stack.Key))
                    col.PanelIds.Add(stack.Key);
                col.RepairTabGroupPanelIds();
                return;
            }

            break;
        }

        col.PanelIds.Add(panelId);
    }

    private static List<string> FlatPanelIds(DockColumnLayout col)
    {
        if (col.Rows is { Count: > 0 })
            return col.Rows.SelectMany(r => r.PanelIds).Distinct().ToList();
        return col.PanelIds.Where(id => !col.TabGroups.ContainsKey(id)).ToList();
    }
}
