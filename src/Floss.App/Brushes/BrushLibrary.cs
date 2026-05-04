using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;

namespace Floss.App.Brushes;

public sealed class BrushLibrary
{
    private readonly string _directory;

    public BrushLibrary(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        SeedDefaults();
    }

    public IReadOnlyList<BrushAsset> Load()
    {
        var assets = new List<BrushAsset>();
        foreach (var path in Directory.EnumerateFiles(_directory, "*" + BrushFileFormat.Extension).OrderBy(Path.GetFileName))
        {
            try
            {
                assets.Add(BrushFileFormat.Load(path));
            }
            catch
            {
                // Keep one bad brush file from breaking the whole brush panel.
            }
        }

        if (assets.Count == 0)
        {
            SeedDefaults(force: true);
            return Load();
        }

        return assets;
    }

    public void Save(BrushAsset asset)
    {
        var path = string.IsNullOrWhiteSpace(asset.FilePath)
            ? Path.Combine(_directory, FileNameFor(asset.Preset.Name))
            : asset.FilePath;

        BrushFileFormat.Save(path, asset);
        asset.FilePath = path;
    }

    private void SeedDefaults(bool force = false)
    {
        foreach (var asset in DefaultAssets())
        {
            var path = Path.Combine(_directory, FileNameFor(asset.Preset.Name));
            if (!force && File.Exists(path)) continue;
            asset.FilePath = path;
            BrushFileFormat.Save(path, asset);
        }
    }

    private static IEnumerable<BrushAsset> DefaultAssets()
    {
        yield return BrushAsset.FromPreset(
            new BrushPreset("Technical Pen", BrushKind.Ink, 8, 1.0, 0.96, 0.09, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.42f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.18f },
                OpacityDynamics = new() { PressureEnabled = false },
                Smoothing = 0.45,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            });

        yield return BrushAsset.FromPreset(
            new BrushPreset("Round Sable", BrushKind.Ink, 18, 0.88, 0.62, 0.12, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.34f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.35f },
                OpacityDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.34f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.18f },
                Smoothing = 0.52,
                Flow = 0.82,
                Tip = new ProceduralBrushTip(BrushTipShape.Ellipse, 0.78f)
            });

        yield return BrushAsset.FromPreset(
            new BrushPreset("Soft Graphite", BrushKind.Pencil, 24, 0.55, 0.24, 0.16, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.47f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.42f },
                OpacityDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.47f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.35f },
                Smoothing = 0.18,
                Grain = 0.55,
                Flow = 0.75,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            });

        yield return BrushAsset.FromPreset(
            new BrushPreset("Chisel Marker", BrushKind.Marker, 42, 0.68, 0.46, 0.18, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, CurveData = [0f, 0f, 0.5f, 0.54f, 1f, 1f], VelocityEnabled = true, VelocityStrength = 0.12f },
                OpacityDynamics = new() { PressureEnabled = false },
                Flow = 0.58,
                Smoothing = 0.55,
                Tip = new ProceduralBrushTip(BrushTipShape.Rectangle, 2.8f)
            });

        yield return BrushAsset.FromPreset(
            new BrushPreset("Soft Airbrush", BrushKind.Airbrush, 64, 0.34, 0.08, 0.12, Color.Parse("#000000"), 100)
            {
                SizeDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                OpacityDynamics = new() { PressureEnabled = true, VelocityEnabled = false },
                Flow = 0.22,
                Smoothing = 0.68,
                Grain = 0.03,
                Tip = new ProceduralBrushTip(BrushTipShape.Circle)
            });
    }

    private static string FileNameFor(string name)
    {
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray()).Trim('-').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(safe)) safe = "brush";
        return safe + BrushFileFormat.Extension;
    }
}
