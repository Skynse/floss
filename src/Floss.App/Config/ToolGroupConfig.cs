using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Input;
using Floss.App.Brushes;
using Floss.App.Input;
using Floss.App.Tools;
using KeyBinding = Floss.App.Input.KeyBinding;

namespace Floss.App;

// Engine lives on the preset, not the group — a group can hold mixed presets.
public enum ToolPresetEngine
{
    Brush, Eraser, Smudge,
    Select, MagicWand,
    Fill, LassoFill,
    Eyedropper, Move,
    Gradient, Shape, Polyline
}

// Output process determines what the tool's input produces (CSP-style).
public enum ToolOutputProcess
{
    DirectDraw,
    Zoom,
    Pan,
    Rotate,
    Eyedropper,
    EraseIfTransparent
}

public sealed class ToolPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public ToolPresetEngine Engine { get; set; }
    public ToolOutputProcess OutputProcess { get; set; } = ToolOutputProcess.DirectDraw;
    public KeyBinding AlternateInvocation { get; set; } = KeyBinding.Empty;
    public double? BrushSize { get; set; }
    public double? BrushOpacity { get; set; }
    public double? BrushFlow { get; set; }
    public double? BrushHardness { get; set; }
    public double? BrushSpacing { get; set; }
    public double? BrushSmoothing { get; set; }
    public double? BrushGrain { get; set; }
    // Apply overrides to a BrushPreset, returning a modified copy
    public BrushPreset ApplyToBrushPreset(BrushPreset preset)
    {
        var needsUpdate = BrushSize.HasValue || BrushOpacity.HasValue || BrushHardness.HasValue ||
            BrushSpacing.HasValue || BrushFlow.HasValue || BrushSmoothing.HasValue ||
            BrushGrain.HasValue;

        return needsUpdate ? preset with
        {
            Size = BrushSize ?? preset.Size,
            Opacity = BrushOpacity ?? preset.Opacity,
            Hardness = BrushHardness ?? preset.Hardness,
            Spacing = BrushSpacing ?? preset.Spacing,
            Flow = BrushFlow ?? preset.Flow,
            Smoothing = BrushSmoothing ?? preset.Smoothing,
            Grain = BrushGrain ?? preset.Grain
        } : preset;
    }

    // Capture current brush preset state into override properties
    public void CaptureFromBrushPreset(BrushPreset preset)
    {
        BrushSize = preset.Size;
        BrushOpacity = preset.Opacity;
        BrushHardness = preset.Hardness;
        BrushSpacing = preset.Spacing;
        BrushFlow = preset.Flow;
        BrushSmoothing = preset.Smoothing;
        BrushGrain = preset.Grain;
    }

    // Brush/Eraser/Smudge — references a BrushAsset on disk; null = use current brush
    public string? BrushId { get; set; }

    // Select
    public SelectMode SelectMode { get; set; } = SelectMode.Rect;

    // MagicWand / Fill
    public double Tolerance { get; set; } = 0.1;

    // Gradient
    public GradientType GradientType { get; set; } = GradientType.Linear;

    // Shape
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public ShapeDrawMode ShapeDrawMode { get; set; } = ShapeDrawMode.Fill;
    public float ShapeStrokeWidth { get; set; } = 4;

    // Polyline
    public bool PolylineClosePath { get; set; }
    public float PolylineStrokeWidth { get; set; } = 4;
}

public sealed class ToolCategory
{
    public string Name { get; set; } = "";
    public List<string> PresetIds { get; set; } = [];
}

public sealed class ToolGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public KeyBinding Shortcut { get; set; } = KeyBinding.Empty;
    public ToolPresetEngine DefaultEngine { get; set; } = ToolPresetEngine.Brush;
    public string? CustomIcon { get; set; }
    public List<ToolPreset> Presets { get; set; } = [];
    public List<ToolCategory> Categories { get; set; } = [];
    public string? LastActivePresetId { get; set; }

    [JsonIgnore]
    public string ActiveIcon =>
        CustomIcon ?? Icons.DefaultIcon(ActivePreset?.Engine ?? DefaultEngine);

    [JsonIgnore]
    public ToolPreset? ActivePreset =>
        Presets.FirstOrDefault(p => p.Id == LastActivePresetId) ?? Presets.FirstOrDefault();
}

public sealed class ToolGroupConfig
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public List<ToolGroup> Groups { get; set; } = Defaults();

    // ── Persistence ───────────────────────────────────────────────────────────

    public static ToolGroupConfig Load()
    {
        try
        {
            var path = AppPaths.ToolGroupConfigPath;
            if (File.Exists(path))
            {
                var cfg = JsonSerializer.Deserialize<ToolGroupConfig>(File.ReadAllText(path), JsonOpts) ?? new();
                cfg.EnsureDefaultPresets();
                return cfg;
            }
        }
        catch { }
        return new();
    }

    private void EnsureDefaultPresets()
    {
        foreach (var group in Groups)
        {
            if (group.Presets.Count == 0)
            {
                group.Presets.Add(new ToolPreset { Name = group.Name, Engine = group.DefaultEngine });
            }
            else if (group.DefaultEngine is not (ToolPresetEngine.Brush or ToolPresetEngine.Eraser or ToolPresetEngine.Smudge))
            {
                // Old configs serialised without an Engine field — it defaults to Brush (0).
                // Correct any preset in a non-paint group that still carries the Brush default.
                foreach (var p in group.Presets.Where(p => p.Engine == ToolPresetEngine.Brush))
                    p.Engine = group.DefaultEngine;
            }
        }
    }

    public void Save()
    {
        try { File.WriteAllText(AppPaths.ToolGroupConfigPath, JsonSerializer.Serialize(this, JsonOpts)); }
        catch { }
    }

    // ── Sync with brush assets ────────────────────────────────────────────────
    // Ensures every BrushAsset on disk is represented as a ToolPreset in some group.

    public void SyncWithAssets(IReadOnlyList<BrushAsset> assets, ToolGroup? preferredGroup = null)
    {
        var allBrushIds = Groups
            .SelectMany(g => g.Presets)
            .Where(p => p.BrushId != null)
            .Select(p => p.BrushId!)
            .ToHashSet();

        var brushGroup = (preferredGroup?.DefaultEngine == ToolPresetEngine.Brush ? preferredGroup : null)
                       ?? Groups.FirstOrDefault(g => g.DefaultEngine == ToolPresetEngine.Brush)
                       ?? Groups.First();
        var eraserGroup = (preferredGroup?.DefaultEngine == ToolPresetEngine.Eraser ? preferredGroup : null)
                       ?? Groups.FirstOrDefault(g => g.DefaultEngine == ToolPresetEngine.Eraser)
                       ?? brushGroup;

        foreach (var asset in assets)
        {
            if (allBrushIds.Contains(asset.Id)) continue;

            var isEraser = asset.Preset.Kind == BrushKind.Eraser;
            var target = isEraser ? eraserGroup : brushGroup;
            var engine = isEraser ? ToolPresetEngine.Eraser : ToolPresetEngine.Brush;

            var preset = new ToolPreset { Name = asset.Preset.Name, Engine = engine, BrushId = asset.Id };
            target.Presets.Add(preset);

            var catName = asset.Preset.Kind switch
            {
                BrushKind.Pencil => "Pencils",
                BrushKind.Ink => "Pens",
                BrushKind.Marker => "Markers",
                BrushKind.Airbrush => "Airbrush",
                BrushKind.Eraser => "Erasers",
                _ => "General"
            };
            EnsureInCategory(target, preset.Id, catName);
        }
    }



    private static void EnsureInCategory(ToolGroup group, string presetId, string categoryName)
    {
        foreach (var c in group.Categories) c.PresetIds.Remove(presetId);
        var cat = group.Categories.FirstOrDefault(c => c.Name == categoryName);
        if (cat == null) { cat = new ToolCategory { Name = categoryName }; group.Categories.Add(cat); }
        if (!cat.PresetIds.Contains(presetId)) cat.PresetIds.Add(presetId);
    }

    // ── Default icon for a group ──────────────────────────────────────────────

    public static string DefaultIcon(ToolPresetEngine engine) => Icons.DefaultIcon(engine);

    // ── Factory defaults ──────────────────────────────────────────────────────

    private static List<ToolGroup> Defaults() =>
    [
        new() { Name = "Brush",  DefaultEngine = ToolPresetEngine.Brush,  Shortcut = new(Key.B),
            Presets = [new() { Name = "Brush",  Engine = ToolPresetEngine.Brush }] },
        new() { Name = "Eraser", DefaultEngine = ToolPresetEngine.Eraser, Shortcut = new(Key.E),
            Presets = [new() { Name = "Eraser", Engine = ToolPresetEngine.Eraser }] },
        new()
        {
            Name = "Smudge", DefaultEngine = ToolPresetEngine.Smudge, Shortcut = new(Key.U),
            Presets = [new() { Name = "Smudge", Engine = ToolPresetEngine.Smudge }]
        },
        new()
        {
            Name = "Select", DefaultEngine = ToolPresetEngine.Select, Shortcut = new(Key.S),
            Presets =
            [
                new() { Name = "Rectangle", Engine = ToolPresetEngine.Select, SelectMode = SelectMode.Rect },
                new() { Name = "Lasso",     Engine = ToolPresetEngine.Select, SelectMode = SelectMode.Lasso },
                new() { Name = "Polygon",   Engine = ToolPresetEngine.Select, SelectMode = SelectMode.PolylineLasso },
            ]
        },
        new()
        {
            Name = "Magic Wand", DefaultEngine = ToolPresetEngine.MagicWand, Shortcut = new(Key.W),
            Presets = [new() { Name = "Magic Wand", Engine = ToolPresetEngine.MagicWand }]
        },
        new()
        {
            Name = "Fill", DefaultEngine = ToolPresetEngine.Fill, Shortcut = new(Key.G),
            Presets = [new() { Name = "Fill", Engine = ToolPresetEngine.Fill }]
        },
        new()
        {
            Name = "Lasso Fill", DefaultEngine = ToolPresetEngine.LassoFill, Shortcut = new(Key.L),
            Presets = [new() { Name = "Lasso Fill", Engine = ToolPresetEngine.LassoFill }]
        },
        new()
        {
            Name = "Move", DefaultEngine = ToolPresetEngine.Move, Shortcut = new(Key.V),
            Presets = [new() { Name = "Move", Engine = ToolPresetEngine.Move }]
        },
        new()
        {
            Name = "Eyedropper", DefaultEngine = ToolPresetEngine.Eyedropper, Shortcut = new(Key.I),
            Presets = [new() { Name = "Eyedropper", Engine = ToolPresetEngine.Eyedropper }]
        },
        new()
        {
            Name = "Gradient", DefaultEngine = ToolPresetEngine.Gradient,
            Presets =
            [
                new() { Name = "Linear", Engine = ToolPresetEngine.Gradient, GradientType = GradientType.Linear },
                new() { Name = "Radial", Engine = ToolPresetEngine.Gradient, GradientType = GradientType.Radial },
            ]
        },
        new()
        {
            Name = "Shape", DefaultEngine = ToolPresetEngine.Shape,
            Presets =
            [
                new() { Name = "Rectangle", Engine = ToolPresetEngine.Shape, ShapeKind = ShapeKind.Rectangle },
                new() { Name = "Ellipse",   Engine = ToolPresetEngine.Shape, ShapeKind = ShapeKind.Ellipse },
                new() { Name = "Line",      Engine = ToolPresetEngine.Shape, ShapeKind = ShapeKind.Line },
            ]
        },
        new()
        {
            Name = "Polyline", DefaultEngine = ToolPresetEngine.Polyline,
            Presets =
            [
                new() { Name = "Open",   Engine = ToolPresetEngine.Polyline, PolylineClosePath = false },
                new() { Name = "Closed", Engine = ToolPresetEngine.Polyline, PolylineClosePath = true },
            ]
        },
    ];
}
