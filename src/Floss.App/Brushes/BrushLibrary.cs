using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Floss.App;

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
        yield return BrushAsset.FromPreset(
            new BrushPreset("Technical Pen", 8, 1.0, 0.96, 0.09, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.42f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.18f },
                OpacityDynamics = new() { PressureEnabled = false },
                Smoothing = 0.45,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            }, category: "Pens");

        yield return BrushAsset.FromPreset(
            new BrushPreset("Round Sable", 18, 0.88, 0.62, 0.12, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.34f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.35f },
                OpacityDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.34f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.18f },
                Smoothing = 0.52,
                Flow = 0.82,
                Tip = new ProceduralBrushTip(BrushTipShape.Ellipse, 0.78f)
            }, category: "Brushes");

        yield return BrushAsset.FromPreset(
            new BrushPreset("Soft Graphite", 24, 0.55, 0.24, 0.16, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.47f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.42f },
                OpacityDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.47f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.35f },
                Smoothing = 0.18,
                Grain = 0.55,
                Flow = 0.75,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            }, category: "Pencils");

        yield return BrushAsset.FromPreset(
            new BrushPreset("Chisel Marker", 42, 0.68, 0.46, 0.18, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.54f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.12f },
                OpacityDynamics = new() { PressureEnabled = false },
                Flow = 0.58,
                Smoothing = 0.55,
                Tip = new ProceduralBrushTip(BrushTipShape.Rectangle, 2.8f)
            }, category: "Markers");

        yield return BrushAsset.FromPreset(
            new BrushPreset("Soft Airbrush", 64, 0.34, 0.08, 0.12, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                OpacityDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                Flow = 0.22,
                Smoothing = 0.68,
                Grain = 0.03,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            }, category: "Airbrush");

        yield return BrushAsset.FromPreset(
            new BrushPreset("Smudge", 24, 0.68, 0.75, 0.10, Color.Parse("#000000"), 0)
            {
                SizeDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                OpacityDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                Flow = 0.58,
                ColorMix = true,
                ColorLoad = 1.0,
                ColorStretch = 0.79,
                BlurAmount = 0.81,
                MixingMode = MixingMode.Perceptual,
                AmountOfPaint = 0.74,
                DensityOfPaint = 1.0,
                TipThickness = 0.42,
                TipDirection = BrushTipDirection.Horizontal,
                SmudgeMode = SmudgeMode.Smudge,
                Smoothing = 0.45,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            }, category: "Mixing");

        yield return BrushAsset.FromPreset(
            new BrushPreset("Blend", 24, 0.68, 0.75, 0.10, Color.Parse("#000000"), 0)
            {
                SizeDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                OpacityDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                Flow = 0.58,
                ColorMix = true,
                ColorLoad = 1.0,
                ColorStretch = 0.2,
                BlurAmount = 0.81,
                AmountOfPaint = 0.0,
                DensityOfPaint = 0.0,
                TipThickness = 0.42,
                TipDirection = BrushTipDirection.Horizontal,
                SmudgeMode = SmudgeMode.Blend,
                Smoothing = 0.45,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            }, category: "Mixing");

        yield return BrushAsset.FromPreset(
            new BrushPreset("Smear", 24, 0.68, 0.75, 0.10, Color.Parse("#000000"), 0)
            {
                SizeDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                OpacityDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                Flow = 0.58,
                ColorMix = true,
                ColorLoad = 1.0,
                ColorStretch = 0.6,
                BlurAmount = 0.5,
                AmountOfPaint = 0.0,
                DensityOfPaint = 0.2,
                TipThickness = 0.42,
                TipDirection = BrushTipDirection.Horizontal,
                SmudgeMode = SmudgeMode.Smear,
                Smoothing = 0.45,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            }, category: "Mixing");

        yield return BrushAsset.FromPreset(
            new BrushPreset("Eraser", 40, 1.0, 0.9, 0.10, Color.Parse("#000000"), 0)
            {
                BlendMode = SkiaSharp.SKBlendMode.DstOut,
                Smoothing = 0.3,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            }, category: "Erasers");
    }

}
