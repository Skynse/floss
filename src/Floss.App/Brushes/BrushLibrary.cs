using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Floss.App;
using SkiaSharp;

namespace Floss.App.Brushes;

public sealed class BrushLibrary
{
    private readonly PresetStore _store;

    public BrushLibrary(string directory)
    {
        _store = PresetStore.OpenDefault();
        SeedDefaults();
    }

    public IReadOnlyList<BrushAsset> Load()
    {
        var assets = _store.LoadBrushAssets();
        if (assets.Count == 0)
        {
            SeedDefaults(force: true);
            assets = _store.LoadBrushAssets();
        }

        var defaultCategories = DefaultAssets().ToDictionary(a => a.Id, a => a.Category);
        foreach (var asset in assets)
        {
            if (asset.Category == null && defaultCategories.TryGetValue(asset.Id, out var cat))
                asset.Category = cat;
        }

        return assets;
    }

    public void Save(BrushAsset asset)
    {
        asset.FilePath = "";
        _store.SaveBrushAsset(asset);
    }

    public void Delete(BrushAsset asset)
    {
        _store.DeleteBrushAsset(asset.Id);
    }

    private void SeedDefaults(bool force = false)
    {
        if (!force && _store.LoadBrushAssets().Count > 0) return;
        foreach (var asset in DefaultAssets())
        {
            asset.FilePath = "";
            _store.SaveBrushAsset(asset);
        }
    }

    internal static IEnumerable<BrushAsset> DefaultAssets()
    {
        foreach (var asset in EnumerateDefaultAssets())
            yield return asset;
    }

    public static IReadOnlyList<string> DefaultBrushCategoryNames { get; } =
        ["Ink", "Paint", "Sketch", "Markers", "Mixing"];

    public static IReadOnlyList<string> DefaultEraserCategoryNames { get; } =
        ["Erasers"];

    private static IEnumerable<BrushAsset> EnumerateDefaultAssets()
    {
        const string ink = "Ink";
        const string paint = "Paint";
        const string sketch = "Sketch";
        const string markers = "Markers";
        const string mixing = "Mixing";
        const string erasers = "Erasers";

        // ── Ink ───────────────────────────────────────────────────────────────
        yield return Asset(
            WithGap(
                new BrushPreset("Technical Pen", 8, 1.0, 0.96, 0.09, Colors.Black, 100)
                {
                    Dynamics = InkDynamics(),
                    OpacityDynamics = new() { PressureEnabled = false },
                    Smoothing = 0.42,
                    Tip = new ProceduralBrushTip(BrushTipShape.Circle)
                },
                BrushGapMode.Narrow),
            ink);

        yield return Asset(
            WithGap(
                new BrushPreset("Fine Liner", 4, 1.0, 0.99, 0.06, Colors.Black, 100)
                {
                    Dynamics = InkDynamics(sizeGamma: 1.05f),
                    OpacityDynamics = new() { PressureEnabled = false },
                    Smoothing = 0.18,
                    Tip = new ProceduralBrushTip(BrushTipShape.Circle)
                },
                BrushGapMode.Narrow),
            ink);

        yield return Asset(
            WithGap(
                new BrushPreset("G-Pen", 14, 0.98, 0.90, 0.10, Colors.Black, 100)
                {
                    Dynamics = InkDynamics(sizeGamma: 1.65f),
                    OpacityDynamics = new() { PressureEnabled = false },
                    Smoothing = 0.35,
                    Tip = new ProceduralBrushTip(BrushTipShape.Circle)
                },
                BrushGapMode.Narrow),
            ink);

        // ── Paint ─────────────────────────────────────────────────────────────
        yield return Asset(
            WithGap(
                new BrushPreset("Round Sable", 20, 0.90, 0.68, 0.25, Colors.Black, 100)
                {
                    Dynamics = PaintDynamics(sizeGamma: 1.55f),
                    Flow = 0.86,
                    Smoothing = 0.46,
                    Tip = new ProceduralBrushTip(BrushTipShape.Ellipse, 0.82f)
                }),
            paint);

        yield return Asset(
            WithGap(
                new BrushPreset("Soft Round", 32, 0.78, 0.42, 0.25, Colors.Black, 100)
                {
                    Dynamics = PaintDynamics(sizeGamma: 1.45f),
                    Flow = 0.72,
                    Smoothing = 0.38,
                    Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
                }),
            paint);

        yield return Asset(
            WithGap(
                new BrushPreset("Flat Brush", 36, 0.82, 0.58, 0.25, Colors.Black, 100)
                {
                    Dynamics = PaintDynamics(sizeGamma: 1.35f),
                    Flow = 0.68,
                    Smoothing = 0.32,
                    Tip = new ProceduralBrushTip(BrushTipShape.Flat)
                }),
            paint);

        yield return Asset(
            WithGap(
                new BrushPreset("Bristle", 28, 0.74, 0.52, 0.25, Colors.Black, 100)
                {
                    Dynamics = PaintDynamics(sizeGamma: 1.25f),
                    Flow = 0.62,
                    Smoothing = 0.28,
                    Tip = new ProceduralBrushTip(BrushTipShape.Bristle)
                }),
            paint);

        yield return Asset(
            WithGap(
                new BrushPreset("Soft Airbrush", 64, 0.34, 0.08, 0.25, Colors.Black, 100)
                {
                    Dynamics = new BrushDynamics
                    {
                        Size = CurveOption.Pressure(1.0f),
                        Opacity = CurveOption.Pressure(1.0f)
                    },
                    Flow = 0.24,
                    Smoothing = 0.62,
                    Grain = 0.04,
                    Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
                },
                continuousSpraying: true),
            paint);

        // ── Sketch ────────────────────────────────────────────────────────────
        yield return Asset(
            WithGap(
                new BrushPreset("Soft Graphite", 22, 0.58, 0.26, 0.25, Colors.Black, 100)
                {
                    Dynamics = SketchDynamics(),
                    Flow = 0.74,
                    Smoothing = 0.16,
                    Grain = 0.52,
                    Tip = new ProceduralBrushTip(BrushTipShape.Circle)
                }),
            sketch);

        yield return Asset(
            WithGap(
                new BrushPreset("Hard Pencil", 12, 0.88, 0.82, 0.12, Colors.Black, 100)
                {
                    Dynamics = SketchDynamics(sizeGamma: 1.15f),
                    Flow = 0.92,
                    Smoothing = 0.12,
                    Grain = 0.12,
                    Tip = new ProceduralBrushTip(BrushTipShape.Circle)
                },
                BrushGapMode.Narrow),
            sketch);

        yield return Asset(
            WithGap(
                new BrushPreset("Chalk", 38, 0.62, 0.34, 0.25, Colors.Black, 100)
                {
                    Dynamics = SketchDynamics(sizeGamma: 1.05f),
                    Flow = 0.58,
                    Smoothing = 0.14,
                    Grain = 0.68,
                    Tip = new ProceduralBrushTip(BrushTipShape.Chalk)
                }),
            sketch);

        // ── Markers ───────────────────────────────────────────────────────────
        yield return Asset(
            WithGap(
                new BrushPreset("Chisel Marker", 42, 0.68, 0.46, 0.25, Colors.Black, 100)
                {
                    Dynamics = new BrushDynamics
                    {
                        Size = CurveOption.Pressure(0.95f),
                        Opacity = CurveOption.Off()
                    },
                    Flow = 0.58,
                    Smoothing = 0.50,
                    Tip = new ProceduralBrushTip(BrushTipShape.Rectangle, 2.8f)
                }),
            markers);

        yield return Asset(
            WithGap(
                new BrushPreset("Brush Pen", 26, 0.82, 0.54, 0.25, Colors.Black, 100)
                {
                    Dynamics = PaintDynamics(sizeGamma: 1.2f),
                    Flow = 0.66,
                    Smoothing = 0.44,
                    Tip = new ProceduralBrushTip(BrushTipShape.SoftRound, 1.15f)
                }),
            markers);

        // ── Mixing ────────────────────────────────────────────────────────────
        yield return Asset(MixingPreset("Smudge", SmudgeMode.Smudge, colorStretch: 0.79, amountOfPaint: 0.74, densityOfPaint: 1.0, blur: 0.81), mixing);
        yield return Asset(MixingPreset("Blend", SmudgeMode.Blend, colorStretch: 0.20, amountOfPaint: 0.0, densityOfPaint: 0.0, blur: 0.81), mixing);
        yield return Asset(MixingPreset("Smear", SmudgeMode.Smear, colorStretch: 0.60, amountOfPaint: 0.0, densityOfPaint: 0.20, blur: 0.50), mixing);

        // ── Erasers ───────────────────────────────────────────────────────────
        yield return Asset(
            WithGap(
                new BrushPreset("Soft Eraser", 48, 1.0, 0.22, 0.25, Colors.Black, 0)
                {
                    BlendMode = SKBlendMode.DstOut,
                    Dynamics = new BrushDynamics
                    {
                        Size = CurveOption.Pressure(1.0f),
                        Opacity = CurveOption.Pressure(0.85f)
                    },
                    Smoothing = 0.38,
                    Tip = new ProceduralBrushTip(BrushTipShape.SoftRound)
                }),
            erasers);

        yield return Asset(
            WithGap(
                new BrushPreset("Hard Eraser", 24, 1.0, 0.94, 0.12, Colors.Black, 0)
                {
                    BlendMode = SKBlendMode.DstOut,
                    Dynamics = new BrushDynamics
                    {
                        Size = CurveOption.Pressure(1.0f),
                        Opacity = CurveOption.Off()
                    },
                    Smoothing = 0.22,
                    Tip = new ProceduralBrushTip(BrushTipShape.Circle)
                },
                BrushGapMode.Narrow),
            erasers);
    }

    private static BrushAsset Asset(BrushPreset preset, string category)
        => BrushAsset.FromPreset(preset, category);

    private static BrushDynamics InkDynamics(float sizeGamma = 1.25f)
        => new()
        {
            Size = CurveOption.Pressure(sizeGamma),
            Opacity = CurveOption.Off()
        };

    private static BrushDynamics PaintDynamics(float sizeGamma)
        => new()
        {
            Size = CurveOption.Pressure(sizeGamma),
            Opacity = CurveOption.Pressure(sizeGamma * 0.9f)
        };

    private static BrushDynamics SketchDynamics(float sizeGamma = 1.1f)
        => new()
        {
            Size = CurveOption.Pressure(sizeGamma),
            Opacity = CurveOption.Pressure(sizeGamma)
        };

    private static BrushPreset MixingPreset(
        string name,
        SmudgeMode mode,
        double colorStretch,
        double amountOfPaint,
        double densityOfPaint,
        double blur)
        => WithGap(new(name, 24, 0.68, 0.75, 0.25, Colors.Black, 0)
        {
            Dynamics = new BrushDynamics
            {
                Size = CurveOption.Pressure(1.0f),
                Opacity = CurveOption.Pressure(1.0f)
            },
            Flow = 0.58,
            ColorMix = true,
            ColorLoad = 1.0,
            ColorStretch = colorStretch,
            BlurAmount = blur,
            MixingMode = mode == SmudgeMode.Smudge ? MixingMode.Perceptual : MixingMode.Standard,
            AmountOfPaint = amountOfPaint,
            DensityOfPaint = densityOfPaint,
            TipThickness = 0.42,
            TipDirection = BrushTipDirection.Horizontal,
            SmudgeMode = mode,
            Smoothing = 0.45,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle)
        });

    private static BrushPreset WithGap(
        BrushPreset preset,
        BrushGapMode gap = BrushGapMode.Normal,
        bool continuousSpraying = false)
    {
        var spacing = gap switch
        {
            BrushGapMode.Fixed => preset.Spacing,
            BrushGapMode.Narrow => BrushSpacing.NarrowGapFraction,
            BrushGapMode.Wide => BrushSpacing.WideGapFraction,
            _ => BrushSpacing.NormalGapFraction
        };

        return preset with
        {
            GapMode = gap,
            Spacing = spacing,
            ContinuousSpraying = continuousSpraying
        };
    }
}
