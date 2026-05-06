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

// Input process determines how user input is captured (CSP-style).
public enum InputProcessType
{
    None = 0,
    BrushStroke,    // Freehand stroke with stabilization
    Lasso,          // Freehand polygon with stabilization
    Polyline,       // Click-to-add vertices
    Rect,           // Drag rectangle
    Click,          // Single click
    Drag,           // Drag start-to-end
    Liquify         // Direct raw positions, no stabilization
}

// Output process determines what the tool's input produces (CSP-style).
public enum OutputProcessType
{
    None = 0,
    DirectDraw,      // Paint with brush engine
    ClosedAreaFill,  // Fill enclosed region
    SelectionArea,   // Create selection
    FloodFill,       // Flood fill by color
    Gradient,        // Apply gradient
    Eyedropper,      // Sample color
    MoveLayer,       // Move layer offset
    Stroke,          // Stroke a path
    Zoom,            // Zoom canvas
    Pan,             // Pan canvas
    MagicWand,       // Magic wand selection
    Liquify,         // Warp/distort pixels
}

public enum FillReferenceMode
{
    CurrentLayer,    // sample from active layer only
    ReferenceLayers, // sample from layers marked IsReference
    AllLayers,       // sample composite of all visible layers
}

public enum LiquifyMode
{
    Push,
    Expand,
    Pinch,
    PushLeft,
    PushRight,
    TwirlCW,
    TwirlCCW,
}

public enum EyedropperSampleMode
{
    Image,
    CurrentLayer
}

// Legacy enum for backward compat during migration.
public enum ToolPresetEngine
{
    Brush, Eraser, Smudge,
    Select, MagicWand,
    Fill, LassoFill,
    Eyedropper, Move,
    Gradient, Shape, Polyline,
    Liquify
}

public sealed class ToolPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";

    // New process-based architecture (CSP-style)
    public InputProcessType InputProcess { get; set; }
    public OutputProcessType OutputProcess { get; set; }

    // Legacy engine field for migration
    public ToolPresetEngine Engine { get; set; }

    public KeyBinding AlternateInvocation { get; set; } = KeyBinding.Empty;

    // Shared input process properties
    public double Stabilization { get; set; } = 0.0;  // 0 = none, 1 = full

    // Shared output process properties
    public bool Antialiasing { get; set; } = true;

    // Brush properties (for DirectDraw output)
    public double? BrushSize { get; set; }
    public double? BrushOpacity { get; set; }
    public double? BrushFlow { get; set; }
    public double? BrushHardness { get; set; }
    public double? BrushSpacing { get; set; }
    public double? BrushSmoothing { get; set; }
    public double? BrushGrain { get; set; }
    public double? BrushColorMix { get; set; }
    public double? BrushColorLoad { get; set; }
    public double? BrushColorStretch { get; set; }
    public double? BrushBlurAmount { get; set; }
    public MixingMode? BrushMixingMode { get; set; }
    public double? BrushAmountOfPaint { get; set; }
    public double? BrushDensityOfPaint { get; set; }
    public double? BrushTipDensity { get; set; }
    public SkiaSharp.SKBlendMode? BrushBlendMode { get; set; }
    public string? BrushDynamicsJson { get; set; }

    // Apply overrides to a BrushPreset, returning a modified copy
    public BrushPreset ApplyToBrushPreset(BrushPreset preset)
    {
        var needsUpdate = BrushSize.HasValue || BrushOpacity.HasValue || BrushHardness.HasValue ||
            BrushSpacing.HasValue || BrushFlow.HasValue || BrushSmoothing.HasValue ||
            BrushGrain.HasValue || BrushColorMix.HasValue || BrushColorLoad.HasValue ||
            BrushColorStretch.HasValue || BrushBlurAmount.HasValue || BrushMixingMode.HasValue ||
            BrushAmountOfPaint.HasValue || BrushDensityOfPaint.HasValue || BrushTipDensity.HasValue ||
            BrushBlendMode.HasValue || !string.IsNullOrEmpty(BrushDynamicsJson);

        if (!needsUpdate) return preset;

        var result = preset with
        {
            Size = BrushSize ?? preset.Size,
            Opacity = BrushOpacity ?? preset.Opacity,
            Hardness = BrushHardness ?? preset.Hardness,
            Spacing = BrushSpacing ?? preset.Spacing,
            Flow = BrushFlow ?? preset.Flow,
            Smoothing = BrushSmoothing ?? preset.Smoothing,
            Grain = BrushGrain ?? preset.Grain,
            ColorMix = BrushColorMix ?? preset.ColorMix,
            ColorLoad = BrushColorLoad ?? preset.ColorLoad,
            ColorStretch = BrushColorStretch ?? preset.ColorStretch,
            BlurAmount = BrushBlurAmount ?? preset.BlurAmount,
            MixingMode = BrushMixingMode ?? preset.MixingMode,
            AmountOfPaint = BrushAmountOfPaint ?? preset.AmountOfPaint,
            DensityOfPaint = BrushDensityOfPaint ?? preset.DensityOfPaint,
            TipDensity = BrushTipDensity ?? preset.TipDensity,
            BlendMode = BrushBlendMode ?? preset.BlendMode,
        };

        if (!string.IsNullOrEmpty(BrushDynamicsJson))
        {
            try
            {
                var dynamics = BrushDynamics.Deserialize(BrushDynamicsJson);
                result = result with { Dynamics = dynamics };
            }
            catch { }
        }

        return result;
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
        BrushColorMix = preset.ColorMix;
        BrushColorLoad = preset.ColorLoad;
        BrushColorStretch = preset.ColorStretch;
        BrushBlurAmount = preset.BlurAmount;
        BrushMixingMode = preset.MixingMode;
        BrushAmountOfPaint = preset.AmountOfPaint;
        BrushDensityOfPaint = preset.DensityOfPaint;
        BrushTipDensity = preset.TipDensity;
        BrushBlendMode = preset.BlendMode;
        BrushDynamicsJson = preset.Dynamics.Serialize();
    }

    // Brush/Eraser/Smudge — references a BrushAsset on disk; null = use current brush
    public string? BrushId { get; set; }

    // Icon path (MDI SVG path string). When set, drives the toolbar group icon.
    public string? PresetIcon { get; set; }

    // Select
    public SelectMode SelectMode { get; set; } = SelectMode.Rect;
    public SelectOp SelectOp { get; set; } = SelectOp.Replace;

    // MagicWand / Fill
    public double Tolerance { get; set; } = 0.1;
    public double AreaScaling { get; set; }
    public bool ContiguousFill { get; set; } = true;
    public FillReferenceMode FillReference { get; set; } = FillReferenceMode.CurrentLayer;

    // Gradient
    public GradientType GradientType { get; set; } = GradientType.Linear;

    // Shape
    public ShapeKind ShapeKind { get; set; } = ShapeKind.Rectangle;
    public ShapeDrawMode ShapeDrawMode { get; set; } = ShapeDrawMode.Fill;
    public float ShapeStrokeWidth { get; set; } = 4;

    // Polyline
    public bool PolylineClosePath { get; set; }
    public float PolylineStrokeWidth { get; set; } = 4;

    // Liquify
    public LiquifyMode LiquifyMode { get; set; } = LiquifyMode.Push;
    public double LiquifySize { get; set; } = 80;
    public double LiquifyStrength { get; set; } = 0.3;

    // Eyedropper
    public EyedropperSampleMode EyedropperSampleMode { get; set; } = EyedropperSampleMode.Image;
    public bool EyedropperExcludeLockedLayers { get; set; }
    public bool EyedropperExcludeReferenceLayers { get; set; }

    // ── Legacy migration ──────────────────────────────────────────────────────
    // Converts old Engine field to new InputProcess+OutputProcess.
    // Should be called after deserialization.
    public void MigrateFromLegacy()
    {
        if (InputProcess != default && OutputProcess != default) return; // Already migrated

        (InputProcess, OutputProcess) = Engine switch
        {
            ToolPresetEngine.Brush      => (InputProcessType.BrushStroke, OutputProcessType.DirectDraw),
            ToolPresetEngine.Eraser     => (InputProcessType.BrushStroke, OutputProcessType.DirectDraw),
            ToolPresetEngine.Smudge     => (InputProcessType.BrushStroke, OutputProcessType.DirectDraw),
            ToolPresetEngine.Select     => SelectMode switch
            {
                SelectMode.Rect          => (InputProcessType.Rect, OutputProcessType.SelectionArea),
                SelectMode.Lasso         => (InputProcessType.Lasso, OutputProcessType.SelectionArea),
                SelectMode.PolylineLasso => (InputProcessType.Polyline, OutputProcessType.SelectionArea),
                _                        => (InputProcessType.Rect, OutputProcessType.SelectionArea)
            },
            // MAGIC WAND MIGRATION: Map to MagicWand output (not SelectionArea).
            // SelectionArea only handles PolygonInput/RectInput; ClickInput does nothing.
            ToolPresetEngine.MagicWand  => (InputProcessType.Click, OutputProcessType.MagicWand),
            ToolPresetEngine.Fill       => (InputProcessType.Click, OutputProcessType.FloodFill),
            ToolPresetEngine.LassoFill  => (InputProcessType.Lasso, OutputProcessType.ClosedAreaFill),
            ToolPresetEngine.Move       => (InputProcessType.Drag, OutputProcessType.MoveLayer),
            ToolPresetEngine.Eyedropper => (InputProcessType.Click, OutputProcessType.Eyedropper),
            ToolPresetEngine.Gradient   => (InputProcessType.Drag, OutputProcessType.Gradient),
            ToolPresetEngine.Shape      => (InputProcessType.Rect, OutputProcessType.Stroke),
            ToolPresetEngine.Polyline   => (InputProcessType.Polyline, OutputProcessType.Stroke),
            ToolPresetEngine.Liquify    => (InputProcessType.Liquify, OutputProcessType.Liquify),
            _                           => (InputProcessType.BrushStroke, OutputProcessType.DirectDraw)
        };
    }
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
        CustomIcon ?? ActivePreset?.PresetIcon ?? Icons.DefaultIcon(ActivePreset?.Engine ?? DefaultEngine);

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
            foreach (var preset in group.Presets)
                preset.MigrateFromLegacy();

            if (group.Presets.Count == 0)
            {
                var defaultPreset = new ToolPreset { Name = group.Name, Engine = group.DefaultEngine };
                defaultPreset.MigrateFromLegacy();
                group.Presets.Add(defaultPreset);
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

        var eraserGroup = Groups.FirstOrDefault(g => g.DefaultEngine == ToolPresetEngine.Eraser);

        foreach (var asset in assets)
        {
            if (allBrushIds.Contains(asset.Id)) continue;

            if (asset.Preset.Kind == Brushes.BrushKind.Eraser && eraserGroup != null)
            {
                var preset = new ToolPreset
                {
                    Name = asset.Preset.Name,
                    InputProcess = InputProcessType.BrushStroke,
                    OutputProcess = OutputProcessType.DirectDraw,
                    BrushId = asset.Id,
                    BrushBlendMode = SkiaSharp.SKBlendMode.DstOut
                };
                eraserGroup.Presets.Add(preset);
            }
            else
            {
                var preset = new ToolPreset
                {
                    Name = asset.Preset.Name,
                    InputProcess = InputProcessType.BrushStroke,
                    OutputProcess = OutputProcessType.DirectDraw,
                    BrushId = asset.Id
                };
                brushGroup.Presets.Add(preset);
            }
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
            Presets = [new() { Name = "Brush",  Engine = ToolPresetEngine.Brush, InputProcess = InputProcessType.BrushStroke, OutputProcess = OutputProcessType.DirectDraw }] },
        new() { Name = "Eraser", DefaultEngine = ToolPresetEngine.Eraser, Shortcut = new(Key.E),
            Presets = [new() { Name = "Eraser", InputProcess = InputProcessType.BrushStroke, OutputProcess = OutputProcessType.DirectDraw, BrushBlendMode = SkiaSharp.SKBlendMode.DstOut }] },
        new()
        {
            Name = "Smudge", DefaultEngine = ToolPresetEngine.Smudge, Shortcut = new(Key.U),
            Presets = [new() { Name = "Smudge", Engine = ToolPresetEngine.Smudge, InputProcess = InputProcessType.BrushStroke, OutputProcess = OutputProcessType.DirectDraw, BrushColorMix = 0.8, BrushColorLoad = 0.0, BrushDensityOfPaint = 0.0 }]
        },
        new()
        {
            Name = "Select", DefaultEngine = ToolPresetEngine.Select, Shortcut = new(Key.S),
            Presets =
            [
                new() { Name = "Rectangle", Engine = ToolPresetEngine.Select, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.SelectionArea, SelectMode = SelectMode.Rect },
                new() { Name = "Lasso",     Engine = ToolPresetEngine.Select, InputProcess = InputProcessType.Lasso, OutputProcess = OutputProcessType.SelectionArea, SelectMode = SelectMode.Lasso, Stabilization = 0.3 },
                new() { Name = "Polygon",   Engine = ToolPresetEngine.Select, InputProcess = InputProcessType.Polyline, OutputProcess = OutputProcessType.SelectionArea, SelectMode = SelectMode.PolylineLasso },
            ]
        },
        new()
        {
            Name = "Magic Wand", DefaultEngine = ToolPresetEngine.MagicWand, Shortcut = new(Key.W),
            Presets = [new() { Name = "Magic Wand", Engine = ToolPresetEngine.MagicWand, InputProcess = InputProcessType.Click, OutputProcess = OutputProcessType.MagicWand }]
        },
        new()
        {
            Name = "Fill", DefaultEngine = ToolPresetEngine.Fill, Shortcut = new(Key.G),
            Presets = [new() { Name = "Fill", Engine = ToolPresetEngine.Fill, InputProcess = InputProcessType.Click, OutputProcess = OutputProcessType.FloodFill }]
        },
        new()
        {
            Name = "Lasso Fill", DefaultEngine = ToolPresetEngine.LassoFill, Shortcut = new(Key.L),
            Presets = [new() { Name = "Lasso Fill", Engine = ToolPresetEngine.LassoFill, InputProcess = InputProcessType.Lasso, OutputProcess = OutputProcessType.ClosedAreaFill, Stabilization = 0.3, Antialiasing = true }]
        },
        new()
        {
            Name = "Move", DefaultEngine = ToolPresetEngine.Move, Shortcut = new(Key.V),
            Presets = [new() { Name = "Move", Engine = ToolPresetEngine.Move, InputProcess = InputProcessType.Drag, OutputProcess = OutputProcessType.MoveLayer }]
        },
        new()
        {
            Name = "Eyedropper", DefaultEngine = ToolPresetEngine.Eyedropper, Shortcut = new(Key.I),
            Presets = [new() { Name = "Eyedropper", Engine = ToolPresetEngine.Eyedropper, InputProcess = InputProcessType.Click, OutputProcess = OutputProcessType.Eyedropper }]
        },
        new()
        {
            Name = "Gradient", DefaultEngine = ToolPresetEngine.Gradient,
            Presets =
            [
                new() { Name = "Linear", Engine = ToolPresetEngine.Gradient, InputProcess = InputProcessType.Drag, OutputProcess = OutputProcessType.Gradient, GradientType = GradientType.Linear },
                new() { Name = "Radial", Engine = ToolPresetEngine.Gradient, InputProcess = InputProcessType.Drag, OutputProcess = OutputProcessType.Gradient, GradientType = GradientType.Radial },
            ]
        },
        new()
        {
            Name = "Shape", DefaultEngine = ToolPresetEngine.Shape,
            Presets =
            [
                new() { Name = "Rectangle", Engine = ToolPresetEngine.Shape, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.Stroke, ShapeKind = ShapeKind.Rectangle },
                new() { Name = "Ellipse",   Engine = ToolPresetEngine.Shape, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.Stroke, ShapeKind = ShapeKind.Ellipse },
                new() { Name = "Line",      Engine = ToolPresetEngine.Shape, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.Stroke, ShapeKind = ShapeKind.Line, ShapeDrawMode = ShapeDrawMode.Stroke },
            ]
        },
        new()
        {
            Name = "Polyline", DefaultEngine = ToolPresetEngine.Polyline,
            Presets =
            [
                new() { Name = "Open",   Engine = ToolPresetEngine.Polyline, InputProcess = InputProcessType.Polyline, OutputProcess = OutputProcessType.Stroke, PolylineClosePath = false },
                new() { Name = "Closed", Engine = ToolPresetEngine.Polyline, InputProcess = InputProcessType.Polyline, OutputProcess = OutputProcessType.Stroke, PolylineClosePath = true },
            ]
        },
        new()
        {
            Name = "Liquify", DefaultEngine = ToolPresetEngine.Liquify,
            Presets =
            [
                new() { Name = "Push",      Engine = ToolPresetEngine.Liquify, InputProcess = InputProcessType.Liquify, OutputProcess = OutputProcessType.Liquify, LiquifyMode = LiquifyMode.Push,      LiquifySize = 80,  LiquifyStrength = 0.3 },
                new() { Name = "Expand",    Engine = ToolPresetEngine.Liquify, InputProcess = InputProcessType.Liquify, OutputProcess = OutputProcessType.Liquify, LiquifyMode = LiquifyMode.Expand,    LiquifySize = 60,  LiquifyStrength = 0.4 },
                new() { Name = "Pinch",     Engine = ToolPresetEngine.Liquify, InputProcess = InputProcessType.Liquify, OutputProcess = OutputProcessType.Liquify, LiquifyMode = LiquifyMode.Pinch,     LiquifySize = 60,  LiquifyStrength = 0.4 },
                new() { Name = "Push Left", Engine = ToolPresetEngine.Liquify, InputProcess = InputProcessType.Liquify, OutputProcess = OutputProcessType.Liquify, LiquifyMode = LiquifyMode.PushLeft,  LiquifySize = 80,  LiquifyStrength = 0.3 },
                new() { Name = "Push Right",Engine = ToolPresetEngine.Liquify, InputProcess = InputProcessType.Liquify, OutputProcess = OutputProcessType.Liquify, LiquifyMode = LiquifyMode.PushRight, LiquifySize = 80,  LiquifyStrength = 0.3 },
                new() { Name = "Twirl CW",  Engine = ToolPresetEngine.Liquify, InputProcess = InputProcessType.Liquify, OutputProcess = OutputProcessType.Liquify, LiquifyMode = LiquifyMode.TwirlCW,   LiquifySize = 80,  LiquifyStrength = 0.5 },
                new() { Name = "Twirl CCW", Engine = ToolPresetEngine.Liquify, InputProcess = InputProcessType.Liquify, OutputProcess = OutputProcessType.Liquify, LiquifyMode = LiquifyMode.TwirlCCW,  LiquifySize = 80,  LiquifyStrength = 0.5 },
            ]
        },
    ];
}
