using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes.IO;

public static class BrushFileFormat
{
    public const string Extension = ".flbr";
    private const uint Magic = 0x52424C46; // FLBR, little endian

    // Version 13 adds material-tip id/label on library entries.
    private const int Version = 13;

    public static BrushAsset Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a Floss brush file.");
        var version = reader.ReadInt32();
        if (version < 1 || version > Version)
            throw new InvalidDataException($"Unsupported brush file version {version}.");

        var asset = new BrushAsset { Id = reader.ReadString(), FilePath = path };

        var name = reader.ReadString();
        reader.ReadInt32(); // legacy BrushKind — discarded
        var size = reader.ReadDouble();
        var opacity = reader.ReadDouble();
        var hardness = reader.ReadDouble();
        var spacing = reader.ReadDouble();
        var angle = version >= 6 ? reader.ReadDouble() : 0.0;

        if (version >= 6)
        {
            var color = Color.FromUInt32(reader.ReadUInt32());
            var flow = reader.ReadDouble();
            var grain = reader.ReadDouble();
            var smoothing = reader.ReadDouble();

            // 2. Safely read Version 7 properties
            var baseAngleSource = AngleSource.None;
            var angleJitter = 0f;
            if (version >= 7)
            {
                baseAngleSource = (AngleSource)reader.ReadInt32();
                angleJitter = reader.ReadSingle();
            }

            // 3. Safely read Version 8 properties
            var quality = BrushQuality.High;
            if (version >= 8)
                quality = (BrushQuality)reader.ReadInt32();

            // 4. Safely read Version 9 properties
            string? texture = null;
            if (version >= 9)
            {
                var t = reader.ReadString();
                texture = t.Length > 0 ? t : null;
            }

            var dynJson = reader.ReadString();
            var dynamics = BrushDynamics.Deserialize(dynJson);
            asset.Tip = ReadTip(reader, version);
            var tipDensity = 1.0;
            var tipThickness = 1.0;
            var tipDirection = BrushTipDirection.Horizontal;
            var tipSelectionMode = BrushTipSelectionMode.Single;
            var flipHorizontal = false;
            var flipVertical = false;
            List<BrushTipData> tips = [];
            if (version >= 10)
            {
                tipDensity = reader.ReadDouble();
                tipThickness = reader.ReadDouble();
                tipDirection = (BrushTipDirection)reader.ReadInt32();
                tipSelectionMode = (BrushTipSelectionMode)reader.ReadInt32();
                flipHorizontal = reader.ReadBoolean();
                flipVertical = reader.ReadBoolean();
                var tipCount = Math.Clamp(reader.ReadInt32(), 0, 256);
                for (var i = 0; i < tipCount; i++)
                    tips.Add(ReadTip(reader, version));
            }
            var parameterGraphs = ReadParameterGraphs(reader, version);
            asset.Preset = new BrushPreset(name, size, opacity, hardness, spacing, color, angle)
            {
                Dynamics = dynamics,
                Flow = flow,
                Grain = grain,
                Smoothing = smoothing,
                BaseAngleSource = baseAngleSource,
                AngleJitter = angleJitter,
                Quality = quality,
                Texture = texture,
                TipDensity = tipDensity,
                TipThickness = tipThickness <= 0 ? 1.0 : tipThickness,
                TipDirection = tipDirection,
                TipSelectionMode = tipSelectionMode,
                FlipHorizontal = flipHorizontal,
                FlipVertical = flipVertical,
                Tips = tips,
                ParameterGraphs = parameterGraphs,
                Tip = asset.Tip.CreateTip()
            };
        }
        else if (version == 5)
        {
            // ... (Legacy V5 loading logic remains exactly the same)
            var color = Color.FromUInt32(reader.ReadUInt32());
            var flow = reader.ReadDouble();
            var grain = reader.ReadDouble();
            var smoothing = reader.ReadDouble();
            var sizeDyn = ReadParameterDynamics(reader);
            var opacDyn = ReadParameterDynamics(reader);
            asset.Tip = ReadTip(reader, version);
            asset.Preset = new BrushPreset(name, size, opacity, hardness, spacing, color, angle)
            {
                Dynamics = BrushDynamics.FromLegacy(sizeDyn, opacDyn),
                Flow = flow,
                Grain = grain,
                Smoothing = smoothing,
                Tip = asset.Tip.CreateTip()
            };
        }
        else
        {
            // ... (Legacy V1-V4 loading logic remains exactly the same)
            var pressureCurve = reader.ReadDouble();
            var velocitySize = reader.ReadDouble();
            var velocityOpac = reader.ReadDouble();
            var color = Color.FromUInt32(reader.ReadUInt32());
            var pressToSize = reader.ReadBoolean();
            var pressToOpac = reader.ReadBoolean();
            var velToSize = reader.ReadBoolean();
            var velToOpac = reader.ReadBoolean();
            var flow = reader.ReadDouble();
            var grain = reader.ReadDouble();
            var smoothing = reader.ReadDouble();

            double prSzMin = 0, prSzMax = 1, prOpMin = 0, prOpMax = 1;
            if (version >= 2)
            {
                prSzMin = reader.ReadDouble();
                prSzMax = reader.ReadDouble();
                prOpMin = reader.ReadDouble();
                prOpMax = reader.ReadDouble();
            }

            var curveKind = ResponseCurveKind.Power;
            float bx1 = 0.25f, by1 = 0.25f, bx2 = 0.75f, by2 = 0.75f;
            if (version >= 3)
            {
                curveKind = (ResponseCurveKind)reader.ReadInt32();
                bx1 = reader.ReadSingle();
                by1 = reader.ReadSingle();
                bx2 = reader.ReadSingle();
                by2 = reader.ReadSingle();
            }

            var sizeDyn = OldParamsToNew(pressToSize, curveKind, (float)pressureCurve, bx1, by1, bx2, by2,
                (float)prSzMin, (float)prSzMax, velToSize, (float)velocitySize);
            var opacDyn = OldParamsToNew(pressToOpac, curveKind, (float)pressureCurve, bx1, by1, bx2, by2,
                (float)prOpMin, (float)prOpMax, velToOpac, (float)velocityOpac);

            asset.Tip = ReadTip(reader, version);
            asset.Preset = new BrushPreset(name, size, opacity, hardness, spacing, color, angle)
            {
                Dynamics = BrushDynamics.FromLegacy(sizeDyn, opacDyn),
                Flow = flow,
                Grain = grain,
                Smoothing = smoothing,
                Tip = asset.Tip.CreateTip()
            };
        }

        return asset;
    }

    public static void Save(string path, BrushAsset asset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        var p = asset.Preset;

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(string.IsNullOrWhiteSpace(asset.Id) ? Guid.NewGuid().ToString("N") : asset.Id);
        writer.Write(p.Name);
        writer.Write(0); // legacy BrushKind, now removed — write 0 for compat
        writer.Write(p.Size);
        writer.Write(p.Opacity);
        writer.Write(p.Hardness);
        writer.Write(p.Spacing);
        writer.Write(p.Angle);
        writer.Write(p.Color.ToUInt32());
        writer.Write(p.Flow);
        writer.Write(p.Grain);
        writer.Write(p.Smoothing);

        // 3. Write Version 7 properties
        writer.Write((int)p.BaseAngleSource);
        writer.Write(p.AngleJitter);

        // 4. Write Version 8 properties
        writer.Write((int)p.Quality);

        // 5. Write Version 9 properties
        writer.Write(p.Texture ?? string.Empty);

        writer.Write(p.Dynamics.Serialize());
        WriteTip(writer, BrushTipData.FromTip(p.Tip));
        writer.Write(p.TipDensity);
        writer.Write(p.TipThickness);
        writer.Write((int)p.TipDirection);
        writer.Write((int)p.TipSelectionMode);
        writer.Write(p.FlipHorizontal);
        writer.Write(p.FlipVertical);
        writer.Write(p.Tips.Count);
        foreach (var tip in p.Tips)
            WriteTip(writer, tip);
        WriteParameterGraphs(writer, p.ParameterGraphs);
    }

    // ── Helpers (kept exactly the same) ────────────────────────────────────

    private static ParameterDynamics ReadParameterDynamics(BinaryReader r)
    {
        var pressureEnabled = r.ReadBoolean();
        var curveKind = (ResponseCurveKind)r.ReadInt32();
        var gamma = r.ReadSingle();
        var x1 = r.ReadSingle();
        var y1 = r.ReadSingle();
        var x2 = r.ReadSingle();
        var y2 = r.ReadSingle();

        return OldParamsToNew(pressureEnabled, curveKind, gamma, x1, y1, x2, y2,
            r.ReadSingle(), r.ReadSingle(), r.ReadBoolean(), r.ReadSingle());
    }

    private static ParameterDynamics OldParamsToNew(
        bool pressureEnabled, ResponseCurveKind curveKind, float gamma,
        float x1, float y1, float x2, float y2,
        float min, float max, bool velocityEnabled, float velocityStrength)
    {
        var curveData = new List<float> { 0f, 0f, 1f, 1f };
        if (curveKind == ResponseCurveKind.Bezier)
        {
            const int steps = 9;
            curveData.Clear();
            for (var i = 0; i < steps; i++)
            {
                var t = i / (float)(steps - 1);
                var bx = CubicBez(t, 0, x1, x2, 1);
                var by = Math.Clamp(CubicBez(t, 0, y1, y2, 1), 0, 1);
                curveData.Add(bx);
                curveData.Add(by);
            }
        }
        else if (curveKind == ResponseCurveKind.Power && Math.Abs(gamma - 1.0) > 0.01f)
        {
            const int steps = 9;
            curveData.Clear();
            for (var i = 0; i < steps; i++)
            {
                var x = i / (float)(steps - 1);
                var y = Math.Clamp(MathF.Pow(x, gamma), 0, 1);
                curveData.Add(x);
                curveData.Add(y);
            }
        }

        return new ParameterDynamics
        {
            PressureEnabled = pressureEnabled,
            CurveData = [.. curveData],
            Min = min,
            Max = max,
            VelocityEnabled = velocityEnabled,
            VelocityStrength = velocityStrength,
            VelocityCurveData = ParameterDynamics.VelocityCurveFromStrength(velocityStrength)
        };
    }

    private static float CubicBez(float t, float p0, float p1, float p2, float p3)
    {
        var inv = 1 - t;
        return inv * inv * inv * p0 + 3 * inv * inv * t * p1 + 3 * inv * t * t * p2 + t * t * t * p3;
    }

    private static BrushTipData ReadTip(BinaryReader reader, int version)
    {
        var kind = (BrushTipStorageKind)reader.ReadInt32();
        var shape = (BrushTipShape)reader.ReadInt32();
        var aspect = reader.ReadSingle();
        var pngLen = reader.ReadInt32();
        var png = pngLen > 0 ? reader.ReadBytes(pngLen) : [];
        BrushTipNodeGraph? nodeGraph = null;
        if (version >= 11)
        {
            var graphJson = reader.ReadString();
            if (!string.IsNullOrWhiteSpace(graphJson))
                nodeGraph = JsonSerializer.Deserialize<BrushTipNodeGraph>(graphJson);
        }

        var tip = new BrushTipData
        {
            Kind = kind,
            Shape = shape,
            AspectRatio = aspect,
            PngBytes = png,
            NodeGraph = nodeGraph
        };
        if (version >= 13)
        {
            tip.Id = reader.ReadString();
            tip.Label = reader.ReadString();
        }

        if ((int)kind == 2) // legacy Compound — no longer exists
        {
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                reader.ReadInt32(); // blend (legacy)
                reader.ReadSingle(); // opacity
                reader.ReadSingle(); // scale
                reader.ReadSingle(); // rotation
                ReadTip(reader, version); // sub-tip (recursive)
            }
            kind = BrushTipStorageKind.Procedural;
        }
        if (kind == BrushTipStorageKind.NodeGraph && nodeGraph == null)
            kind = BrushTipStorageKind.Procedural;
        tip.Kind = kind;
        return tip;
    }

    private static void WriteTip(BinaryWriter writer, BrushTipData tip)
    {
        writer.Write((int)tip.Kind);
        writer.Write((int)tip.Shape);
        writer.Write(tip.AspectRatio);
        var pngLen = tip.PngBytes?.Length ?? 0;
        writer.Write(pngLen);
        if (pngLen > 0)
            writer.Write(tip.PngBytes!);
        writer.Write(tip.Kind == BrushTipStorageKind.NodeGraph && tip.NodeGraph != null
            ? JsonSerializer.Serialize(tip.NodeGraph)
            : string.Empty);
        writer.Write(tip.Id ?? "");
        writer.Write(tip.Label ?? "");
    }

    private static IReadOnlyList<BrushParameterGraph> ReadParameterGraphs(BinaryReader reader, int version)
    {
        if (version < 12)
            return [];
        var json = reader.ReadString();
        if (string.IsNullOrWhiteSpace(json))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<BrushParameterGraph>>(json)?
                .Where(g => g.Validate().Count == 0)
                .Select(g => g.DeepClone())
                .ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void WriteParameterGraphs(BinaryWriter writer, IReadOnlyList<BrushParameterGraph> graphs)
        => writer.Write(graphs.Count == 0
            ? string.Empty
            : JsonSerializer.Serialize(graphs.Select(g => g.DeepClone()).ToList()));
}
