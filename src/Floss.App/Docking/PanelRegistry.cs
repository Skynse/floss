using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;

namespace Floss.App.Docking;

/// <summary>
/// Registry of all available dockable panels.
/// Replaces the hardcoded AllDockerIds array and GetDockerInfo switch.
/// </summary>
public static class PanelRegistry
{
    private static readonly Dictionary<string, IDockPanel> _panels = new();

    public static IReadOnlyList<IDockPanel> All => _panels.Values.ToList().AsReadOnly();
    public static IReadOnlyList<string> AllIds => _panels.Keys.ToList().AsReadOnly();

    public static void Register(IDockPanel panel)
    {
        _panels[panel.Id] = panel;
    }

    public static IDockPanel? Get(string id)
        => _panels.TryGetValue(id, out var panel) ? panel : null;

    public static bool Exists(string id)
        => _panels.ContainsKey(id);

    /// <summary>Clear all registered panels (for test isolation).</summary>
    public static void Clear() => _panels.Clear();

    /// <summary>
    /// Register the built-in default panels. Called once at startup.
    /// </summary>
    public static void RegisterDefaults(Func<string, Func<Control>> buildContent)
    {
        Register(new DockPanelDef("tools", "Tools",
            buildContent("tools"),
            Proportion: 0.22, MinHeight: 80, DefaultZone: "left"));

        Register(new DockPanelDef("brush", "Brushes",
            buildContent("brush"),
            Proportion: 0.28, MinHeight: 160, DefaultZone: "left"));

        Register(new DockPanelDef("tool-properties", "Brush",
            buildContent("tool-properties"),
            Proportion: 0.15, MinHeight: 64, DefaultZone: "left"));

        Register(new DockPanelDef("layer-properties", "Layer Color",
            buildContent("layer-properties"),
            Proportion: 0.10, MinHeight: 64, DefaultZone: "right-0"));

        Register(new DockPanelDef("layers", "Layers",
            buildContent("layers"),
            Proportion: 0.40, MinHeight: 140, DefaultZone: "right-0"));

        Register(new DockPanelDef("color", "Color",
            buildContent("color"),
            Proportion: 0.18, MinHeight: 180, Sizing: DockPanelSizing.Auto, DefaultZone: "right-0"));

        Register(new DockPanelDef("color-slider", "Color Sliders",
            buildContent("color-slider"),
            Proportion: 0.12, MinHeight: 48, Sizing: DockPanelSizing.Auto, DefaultZone: "right-0"));

        Register(new DockPanelDef("node-graph", "Node Graph",
            buildContent("node-graph"),
            AllowFloat: true, AllowHide: true,
            Proportion: 0.35, MinHeight: 160, DefaultZone: "bottom"));
    }
}
