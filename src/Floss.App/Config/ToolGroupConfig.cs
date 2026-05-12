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
    Pen = 1,        // Fine-line freehand stroke (tight, hard-edge cursor)
    Brush = 2,      // Stamp-based freehand stroke (tip-shape cursor)
    Eraser = 3,     // Erasing freehand stroke (eraser cursor)
    Smudge = 4,     // Smear/blend freehand stroke
    Lasso = 5,      // Freehand polygon with stabilization
    Polyline = 6,   // Click-to-add vertices
    Rect = 7,       // Drag rectangle
    Click = 8,      // Single click
    Drag = 9,       // Drag start-to-end
    Liquify = 10,   // Direct raw positions, no stabilization
    Hand = 11,      // Pan canvas (drag)
    Rotate = 12,    // Rotate canvas (drag)
    Zoom = 13,      // Zoom canvas in (drag)
    MoveLayer = 14, // Translate layer (drag)
}

public static class InputProcessTypeExtensions
{
    public static bool IsBrushFamily(this InputProcessType t)
        => t is InputProcessType.Pen or InputProcessType.Brush or InputProcessType.Eraser or InputProcessType.Smudge;
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
    Zoom,            // Zoom canvas in (drag)
    Hand,            // Hand canvas (pan)
    Rotate,          // Rotate canvas
    MagicWand,       // Magic wand selection
    Liquify,         // Warp/distort pixels
    SelectLayer,     // Select layer by clicking or area
}

// Defines which input processes are valid for each output process (CSP-style coupling).
public static class ProcessCompatibility
{
    // Locked outputs: input process is forced to match
    public static readonly HashSet<OutputProcessType> LockedOutputs = new()
    {
        OutputProcessType.Hand,
        OutputProcessType.Rotate,
        OutputProcessType.Zoom,
        OutputProcessType.Eyedropper,
        OutputProcessType.Liquify,
    };

    public static InputProcessType? LockedInputFor(OutputProcessType output) => output switch
    {
        OutputProcessType.Hand => InputProcessType.Hand,
        OutputProcessType.Rotate => InputProcessType.Rotate,
        OutputProcessType.Zoom => InputProcessType.Zoom,
        OutputProcessType.Eyedropper => InputProcessType.Click,
        OutputProcessType.Liquify => InputProcessType.Liquify,
        _ => null
    };

    public static IReadOnlyList<InputProcessType> ValidInputsFor(OutputProcessType output) => output switch
    {
        OutputProcessType.DirectDraw => new[] { InputProcessType.Pen, InputProcessType.Brush, InputProcessType.Eraser, InputProcessType.Smudge, InputProcessType.Lasso, InputProcessType.Rect, InputProcessType.Polyline },
        OutputProcessType.ClosedAreaFill => new[] { InputProcessType.Lasso, InputProcessType.Rect, InputProcessType.Brush, InputProcessType.Pen, InputProcessType.Polyline },
        OutputProcessType.SelectionArea => new[] { InputProcessType.Lasso, InputProcessType.Rect, InputProcessType.Polyline, InputProcessType.Brush, InputProcessType.Pen },
        OutputProcessType.FloodFill => new[] { InputProcessType.Click },
        OutputProcessType.Gradient => new[] { InputProcessType.Drag },
        OutputProcessType.Eyedropper => new[] { InputProcessType.Click },
        OutputProcessType.MoveLayer => new[] { InputProcessType.MoveLayer, InputProcessType.Drag },
        OutputProcessType.MagicWand => new[] { InputProcessType.Click },
        OutputProcessType.Stroke => new[] { InputProcessType.Rect, InputProcessType.Polyline },
        OutputProcessType.Liquify => new[] { InputProcessType.Liquify },
        OutputProcessType.Hand => new[] { InputProcessType.Hand },
        OutputProcessType.Zoom => new[] { InputProcessType.Zoom },
        OutputProcessType.Rotate => new[] { InputProcessType.Rotate },
        OutputProcessType.SelectLayer => new[] { InputProcessType.Rect, InputProcessType.Lasso, InputProcessType.Brush, InputProcessType.Pen },
        _ => Enum.GetValues<InputProcessType>().ToArray()
    };

    public static bool IsValidCombination(InputProcessType input, OutputProcessType output)
    {
        var valid = ValidInputsFor(output);
        return valid.Contains(input);
    }

    public static InputProcessType DefaultInputFor(OutputProcessType output) => output switch
    {
        OutputProcessType.DirectDraw => InputProcessType.Brush,
        OutputProcessType.ClosedAreaFill => InputProcessType.Lasso,
        OutputProcessType.SelectionArea => InputProcessType.Rect,
        OutputProcessType.FloodFill => InputProcessType.Click,
        OutputProcessType.Gradient => InputProcessType.Drag,
        OutputProcessType.Eyedropper => InputProcessType.Click,
        OutputProcessType.MoveLayer => InputProcessType.MoveLayer,
        OutputProcessType.MagicWand => InputProcessType.Click,
        OutputProcessType.Stroke => InputProcessType.Rect,
        OutputProcessType.Liquify => InputProcessType.Liquify,
        OutputProcessType.Hand => InputProcessType.Hand,
        OutputProcessType.Zoom => InputProcessType.Zoom,
        OutputProcessType.Rotate => InputProcessType.Rotate,
        OutputProcessType.SelectLayer => InputProcessType.Rect,
        _ => InputProcessType.Brush
    };
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
    Eyedropper, Move, MoveLayer,
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
    public bool? BrushColorMix { get; set; }
    public double? BrushColorLoad { get; set; }
    public double? BrushColorStretch { get; set; }
    public double? BrushBlurAmount { get; set; }
    public SmudgeMode? BrushSmudgeMode { get; set; }
    public MixingMode? BrushMixingMode { get; set; }
    public double? BrushAmountOfPaint { get; set; }
    public double? BrushDensityOfPaint { get; set; }
    public double? BrushTipDensity { get; set; }
    public double? BrushTipThickness { get; set; }
    public BrushTipDirection? BrushTipDirection { get; set; }
    public SkiaSharp.SKBlendMode? BrushBlendMode { get; set; }
    public string? BrushDynamicsJson { get; set; }

    // Apply overrides to a BrushPreset, returning a modified copy
    public BrushPreset ApplyToBrushPreset(BrushPreset preset)
    {
        var needsUpdate = BrushSize.HasValue || BrushOpacity.HasValue || BrushHardness.HasValue ||
            BrushSpacing.HasValue || BrushFlow.HasValue || BrushSmoothing.HasValue ||
            BrushGrain.HasValue || BrushColorMix.HasValue || BrushColorLoad.HasValue ||
            BrushColorStretch.HasValue || BrushBlurAmount.HasValue || BrushSmudgeMode.HasValue || BrushMixingMode.HasValue ||
            BrushAmountOfPaint.HasValue || BrushDensityOfPaint.HasValue || BrushTipDensity.HasValue ||
            BrushTipThickness.HasValue || BrushTipDirection.HasValue ||
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
            SmudgeMode = BrushSmudgeMode ?? preset.SmudgeMode,
            MixingMode = BrushMixingMode ?? preset.MixingMode,
            AmountOfPaint = BrushAmountOfPaint ?? preset.AmountOfPaint,
            DensityOfPaint = BrushDensityOfPaint ?? preset.DensityOfPaint,
            TipDensity = BrushTipDensity ?? preset.TipDensity,
            TipThickness = BrushTipThickness ?? preset.TipThickness,
            TipDirection = BrushTipDirection ?? preset.TipDirection,
            BlendMode = BrushBlendMode ?? preset.BlendMode,
        };

        if (!string.IsNullOrEmpty(BrushDynamicsJson))
        {
            try
            {
                var dynamics = BrushDynamics.Deserialize(BrushDynamicsJson);
                result = result with { Dynamics = dynamics };
            }
            catch (Exception ex) { CrashLog.Write(ex, $"ToolGroupConfig.Load ({Id})"); }
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
        BrushSmudgeMode = preset.SmudgeMode;
        BrushMixingMode = preset.MixingMode;
        BrushAmountOfPaint = preset.AmountOfPaint;
        BrushDensityOfPaint = preset.DensityOfPaint;
        BrushTipDensity = preset.TipDensity;
        BrushTipThickness = preset.TipThickness;
        BrushTipDirection = preset.TipDirection;
        BrushBlendMode = preset.BlendMode;
        BrushDynamicsJson = preset.Dynamics.Serialize();
    }

    public void ClearBrushOverrides()
    {
        BrushSize = null;
        BrushOpacity = null;
        BrushFlow = null;
        BrushHardness = null;
        BrushSpacing = null;
        BrushSmoothing = null;
        BrushGrain = null;
        BrushColorMix = null;
        BrushColorLoad = null;
        BrushColorStretch = null;
        BrushBlurAmount = null;
        BrushSmudgeMode = null;
        BrushMixingMode = null;
        BrushAmountOfPaint = null;
        BrushDensityOfPaint = null;
        BrushTipDensity = null;
        BrushTipThickness = null;
        BrushTipDirection = null;
        BrushBlendMode = null;
        BrushDynamicsJson = null;
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

    // Zoom
    public double ZoomDirection { get; set; } = 1;   // 1 = zoom-in, -1 = zoom-out

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
            ToolPresetEngine.Brush => (InputProcessType.Brush, OutputProcessType.DirectDraw),
            ToolPresetEngine.Eraser => (InputProcessType.Eraser, OutputProcessType.DirectDraw),
            ToolPresetEngine.Smudge => (InputProcessType.Smudge, OutputProcessType.DirectDraw),
            ToolPresetEngine.Select => SelectMode switch
            {
                SelectMode.Rect => (InputProcessType.Rect, OutputProcessType.SelectionArea),
                SelectMode.Lasso => (InputProcessType.Lasso, OutputProcessType.SelectionArea),
                SelectMode.PolylineLasso => (InputProcessType.Polyline, OutputProcessType.SelectionArea),
                _ => (InputProcessType.Rect, OutputProcessType.SelectionArea)
            },
            // MAGIC WAND MIGRATION: Map to MagicWand output (not SelectionArea).
            // SelectionArea only handles PolygonInput/RectInput; ClickInput does nothing.
            ToolPresetEngine.MagicWand => (InputProcessType.Click, OutputProcessType.MagicWand),
            ToolPresetEngine.Fill => (InputProcessType.Click, OutputProcessType.FloodFill),
            ToolPresetEngine.LassoFill => (InputProcessType.Lasso, OutputProcessType.ClosedAreaFill),
            ToolPresetEngine.Move or ToolPresetEngine.MoveLayer => (InputProcessType.MoveLayer, OutputProcessType.MoveLayer),
            ToolPresetEngine.Eyedropper => (InputProcessType.Click, OutputProcessType.Eyedropper),
            ToolPresetEngine.Gradient => (InputProcessType.Drag, OutputProcessType.Gradient),
            ToolPresetEngine.Shape => (InputProcessType.Rect, OutputProcessType.Stroke),
            ToolPresetEngine.Polyline => (InputProcessType.Polyline, OutputProcessType.Stroke),
            ToolPresetEngine.Liquify => (InputProcessType.Liquify, OutputProcessType.Liquify),
            _ => (InputProcessType.Brush, OutputProcessType.DirectDraw)
        };
    }
}

public sealed class ToolCategory
{
    public string Name { get; set; } = "";
    public List<string> PresetIds { get; set; } = [];
    public string? LastActivePresetId { get; set; }
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
    public string? LastActiveCategoryName { get; set; }

    [JsonIgnore]
    public string ActiveIcon =>
        CustomIcon ?? ActivePreset?.PresetIcon ?? Icons.DefaultIcon(DefaultEngine);

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

    [JsonIgnore]
    private PresetStore? Store { get; set; }

    // ── Persistence ───────────────────────────────────────────────────────────

    public static ToolGroupConfig Load()
    {
        try
        {
            var store = PresetStore.OpenDefault();
            var groups = store.LoadToolGroups();
            var cfg = new ToolGroupConfig { Store = store };
            if (groups.Count > 0)
            {
                cfg.Groups = groups.ToList();
                cfg.EnsureDefaultPresets();
                return cfg;
            }

            cfg.Groups = Defaults();
            cfg.EnsureDefaultPresets();
            cfg.Save();
            return cfg;
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "ToolGroupConfig.Load");
            Console.Error.WriteLine($"[Floss] Failed to load tool group config: {ex.Message}");
            var cfg = new ToolGroupConfig();
            cfg.EnsureDefaultPresets();
            return cfg;
        }
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

            EnsureFallbackCategory(group);
        }
    }

    public void Save()
    {
        try { (Store ??= PresetStore.OpenDefault()).SaveToolGroups(Groups); }
        catch (Exception ex) { CrashLog.Write(ex, "ToolGroupConfig.Save"); Console.Error.WriteLine($"[Floss] Failed to save tool group config: {ex.Message}"); }
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

            if (asset.Preset.BlendMode == SkiaSharp.SKBlendMode.DstOut && eraserGroup != null)
            {
                var preset = new ToolPreset
                {
                    Name = asset.Preset.Name,
                    InputProcess = InputProcessType.Eraser,
                    OutputProcess = OutputProcessType.DirectDraw,
                    BrushId = asset.Id,
                    BrushBlendMode = SkiaSharp.SKBlendMode.DstOut
                };
                eraserGroup.Presets.Add(preset);
                if (asset.Category != null)
                    AddToCategory(eraserGroup, preset.Id, asset.Category);
            }
            else
            {
                var preset = new ToolPreset
                {
                    Name = asset.Preset.Name,
                    InputProcess = InputProcessType.Brush,
                    OutputProcess = OutputProcessType.DirectDraw,
                    BrushId = asset.Id
                };
                brushGroup.Presets.Add(preset);
                if (asset.Category != null)
                    AddToCategory(brushGroup, preset.Id, asset.Category);
            }
        }

        foreach (var asset in assets)
        {
            if (asset.Category == null) continue;
            foreach (var group in Groups)
            {
                var preset = group.Presets.FirstOrDefault(p => p.BrushId == asset.Id);
                if (preset == null) continue;
                if (group.Categories.Any(c => c.PresetIds.Contains(preset.Id))) continue;
                AddToCategory(group, preset.Id, asset.Category);
            }
        }

        // Remove placeholder presets from brush-engine groups that now have
        // asset-backed presets superseding them.
        foreach (var group in Groups)
        {
            if (!group.Presets.Any(p => p.BrushId != null)) continue;
            group.Presets.RemoveAll(p => p.BrushId == null
                && p.OutputProcess == OutputProcessType.DirectDraw);
        }
    }

    private static void AddToCategory(ToolGroup group, string presetId, string categoryName)
    {
        var cat = group.Categories.FirstOrDefault(c => c.Name == categoryName);
        if (cat == null)
        {
            cat = new ToolCategory { Name = categoryName };
            group.Categories.Add(cat);
        }
        if (!cat.PresetIds.Contains(presetId))
            cat.PresetIds.Add(presetId);
    }

    private static void EnsureFallbackCategory(ToolGroup group)
    {
        var uncategorized = group.Presets
            .Where(preset => !group.Categories.Any(category => category.PresetIds.Contains(preset.Id)))
            .Select(preset => preset.Id)
            .ToList();
        if (uncategorized.Count == 0) return;

        var categoryName = DefaultCategoryName(group);
        var category = group.Categories.FirstOrDefault(c => c.Name == categoryName);
        if (category == null)
        {
            category = new ToolCategory { Name = categoryName };
            group.Categories.Insert(0, category);
        }

        foreach (var presetId in uncategorized)
        {
            if (!category.PresetIds.Contains(presetId))
                category.PresetIds.Add(presetId);
        }
    }

    private static string DefaultCategoryName(ToolGroup group)
        => group.Name;

    // ── Default icon for a group ──────────────────────────────────────────────

    public static string DefaultIcon(ToolPresetEngine engine) => Icons.DefaultIcon(engine);

    // ── Stable IDs for built-in presets (referenced by modifier key defaults) ──
    internal const string ViewHandPresetId = "builtin-hand";
    internal const string ViewRotatePresetId = "builtin-rotate";
    internal const string ViewZoomInPresetId = "builtin-zoomin";
    internal const string ViewZoomOutPresetId = "builtin-zoomout";
    internal const string EyedropperPresetId = "builtin-eyedropper";
    internal const string MoveLayerPresetId = "builtin-movelayer";

    // ── Factory defaults ──────────────────────────────────────────────────────

    internal static List<ToolGroup> Defaults() =>
    [
        WithDefaultCategory(new() { Name = "Brush",  DefaultEngine = ToolPresetEngine.Brush,  Shortcut = new(Key.B),
            Presets = [new() { Name = "Brush",  Engine = ToolPresetEngine.Brush, InputProcess = InputProcessType.Brush, OutputProcess = OutputProcessType.DirectDraw }] }),
        WithDefaultCategory(new() { Name = "Eraser", DefaultEngine = ToolPresetEngine.Eraser, Shortcut = new(Key.E),
            Presets = [new() { Name = "Eraser", InputProcess = InputProcessType.Eraser, OutputProcess = OutputProcessType.DirectDraw, BrushBlendMode = SkiaSharp.SKBlendMode.DstOut }] }),
        WithDefaultCategory(new()
        {
            Name = "Smudge", DefaultEngine = ToolPresetEngine.Smudge, Shortcut = new(Key.U),
            Presets = [new() { Name = "Smudge", Engine = ToolPresetEngine.Smudge, InputProcess = InputProcessType.Smudge, OutputProcess = OutputProcessType.DirectDraw, BrushColorMix = true, BrushSmudgeMode = SmudgeMode.Smudge, BrushAmountOfPaint = 0.0, BrushDensityOfPaint = 0.0 }]
        }),
        WithDefaultCategory(new()
        {
            Name = "Select", DefaultEngine = ToolPresetEngine.Select, Shortcut = new(Key.S),
            Presets =
            [
                new() { Name = "Rectangle", Engine = ToolPresetEngine.Select, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.SelectionArea, SelectMode = SelectMode.Rect },
                new() { Name = "Lasso",     Engine = ToolPresetEngine.Select, InputProcess = InputProcessType.Lasso, OutputProcess = OutputProcessType.SelectionArea, SelectMode = SelectMode.Lasso, Stabilization = 0.3 },
                new() { Name = "Polygon",   Engine = ToolPresetEngine.Select, InputProcess = InputProcessType.Polyline, OutputProcess = OutputProcessType.SelectionArea, SelectMode = SelectMode.PolylineLasso },
            ]
        }),
        WithDefaultCategory(new()
        {
            Name = "Magic Wand", DefaultEngine = ToolPresetEngine.MagicWand, Shortcut = new(Key.W),
            Presets = [new() { Name = "Magic Wand", Engine = ToolPresetEngine.MagicWand, InputProcess = InputProcessType.Click, OutputProcess = OutputProcessType.MagicWand }]
        }),
        WithDefaultCategory(new()
        {
            Name = "Fill", DefaultEngine = ToolPresetEngine.Fill, Shortcut = new(Key.G),
            Presets = [
                new() { Name = "Fill", Engine = ToolPresetEngine.Fill, InputProcess = InputProcessType.Click, OutputProcess = OutputProcessType.FloodFill },
                new() { Name = "Lasso Fill", Engine = ToolPresetEngine.LassoFill, InputProcess = InputProcessType.Lasso, OutputProcess = OutputProcessType.ClosedAreaFill, Stabilization = 0.3, Antialiasing = true }]
        }),

        WithDefaultCategory(new()
        {
            Name = "Operation", DefaultEngine = ToolPresetEngine.MoveLayer, Shortcut = new(Key.V),
            Presets =
            [
                new() { Id = MoveLayerPresetId, Name = "Move Layer", Engine = ToolPresetEngine.MoveLayer, InputProcess = InputProcessType.MoveLayer, OutputProcess = OutputProcessType.MoveLayer },
                new() { Name = "Select Layer", Engine = ToolPresetEngine.MoveLayer, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.SelectLayer }
            ]
        }),
        WithDefaultCategory(new()
        {
            Name = "View", DefaultEngine = ToolPresetEngine.Move, CustomIcon = Icons.Hand,
            Presets =
            [
                new() { Id = ViewHandPresetId,    Name = "Hand",     InputProcess = InputProcessType.Hand,    OutputProcess = OutputProcessType.Hand },
                new() { Id = ViewRotatePresetId,  Name = "Rotate",   InputProcess = InputProcessType.Rotate,  OutputProcess = OutputProcessType.Rotate },
                new() { Id = ViewZoomInPresetId,  Name = "Zoom In",  InputProcess = InputProcessType.Zoom,    OutputProcess = OutputProcessType.Zoom, ZoomDirection = 1 },
                new() { Id = ViewZoomOutPresetId, Name = "Zoom Out", InputProcess = InputProcessType.Zoom,    OutputProcess = OutputProcessType.Zoom, ZoomDirection = -1 },
            ]
        }),
        WithDefaultCategory(new()
        {
            Name = "Eyedropper", DefaultEngine = ToolPresetEngine.Eyedropper, Shortcut = new(Key.I),
            Presets = [new() { Id = EyedropperPresetId, Name = "Eyedropper", Engine = ToolPresetEngine.Eyedropper, InputProcess = InputProcessType.Click, OutputProcess = OutputProcessType.Eyedropper }]
        }),
        WithDefaultCategory(new()
        {
            Name = "Gradient", DefaultEngine = ToolPresetEngine.Gradient,
            Presets =
            [
                new() { Name = "Linear", Engine = ToolPresetEngine.Gradient, InputProcess = InputProcessType.Drag, OutputProcess = OutputProcessType.Gradient, GradientType = GradientType.Linear },
                new() { Name = "Radial", Engine = ToolPresetEngine.Gradient, InputProcess = InputProcessType.Drag, OutputProcess = OutputProcessType.Gradient, GradientType = GradientType.Radial },
            ]
        }),
        WithDefaultCategory(new()
        {
            Name = "Shape", DefaultEngine = ToolPresetEngine.Shape,
            Presets =
            [
                new() { Name = "Rectangle", Engine = ToolPresetEngine.Shape, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.Stroke, ShapeKind = ShapeKind.Rectangle, PolylineClosePath = true },
                new() { Name = "Ellipse",   Engine = ToolPresetEngine.Shape, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.Stroke, ShapeKind = ShapeKind.Ellipse,   PolylineClosePath = true },
                new() { Name = "Line",      Engine = ToolPresetEngine.Shape, InputProcess = InputProcessType.Rect, OutputProcess = OutputProcessType.Stroke, ShapeKind = ShapeKind.Line,      ShapeDrawMode = ShapeDrawMode.Stroke },
            ]
        }),
        WithDefaultCategory(new()
        {
            Name = "Polyline", DefaultEngine = ToolPresetEngine.Polyline,
            Presets =
            [
                new() { Name = "PolyLine",   Engine = ToolPresetEngine.Polyline, InputProcess = InputProcessType.Polyline, OutputProcess = OutputProcessType.Stroke, PolylineClosePath = false },
            ]
        }),
        WithDefaultCategory(new()
        {
            Name = "Liquify", DefaultEngine = ToolPresetEngine.Liquify,
            Presets =
            [
                new() { Name = "Liquify",      Engine = ToolPresetEngine.Liquify, InputProcess = InputProcessType.Liquify, OutputProcess = OutputProcessType.Liquify, LiquifyMode = LiquifyMode.Push,      LiquifySize = 80,  LiquifyStrength = 0.3 },

            ]
        }),
    ];

    private static ToolGroup WithDefaultCategory(ToolGroup group)
    {
        EnsureFallbackCategory(group);
        return group;
    }
}
