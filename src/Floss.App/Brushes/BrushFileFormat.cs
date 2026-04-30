using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Avalonia.Media;

namespace Floss.App.Brushes;

public static class BrushFileFormat
{
    public const string Extension = ".flbr";
    private const uint Magic   = 0x52424C46; // FLBR, little endian
    private const int  Version = 5;

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

        var name     = reader.ReadString();
        var kind     = (BrushKind)reader.ReadInt32();
        var size     = reader.ReadDouble();
        var opacity  = reader.ReadDouble();
        var hardness = reader.ReadDouble();
        var spacing  = reader.ReadDouble();

        if (version >= 5)
        {
            var color     = Color.FromUInt32(reader.ReadUInt32());
            var flow      = reader.ReadDouble();
            var grain     = reader.ReadDouble();
            var smoothing = reader.ReadDouble();
            var dynJson   = reader.ReadString();
            var dynamics  = BrushDynamics.Deserialize(dynJson);
            asset.Tip = ReadTip(reader);
            asset.Preset = new BrushPreset(name, kind, size, opacity, hardness, spacing, color)
            {
                Dynamics  = dynamics,
                Flow      = flow,
                Grain     = grain,
                Smoothing = smoothing,
                Tip       = asset.Tip.CreateTip()
            };
        }
        else if (version == 4)
        {
            var color     = Color.FromUInt32(reader.ReadUInt32());
            var flow      = reader.ReadDouble();
            var grain     = reader.ReadDouble();
            var smoothing = reader.ReadDouble();
            var sizeDyn   = ReadParameterDynamics(reader);
            var opacDyn   = ReadParameterDynamics(reader);
            asset.Tip = ReadTip(reader);
            asset.Preset = new BrushPreset(name, kind, size, opacity, hardness, spacing, color)
            {
                Dynamics  = BrushDynamics.FromLegacy(sizeDyn, opacDyn),
                Flow      = flow,
                Grain     = grain,
                Smoothing = smoothing,
                Tip       = asset.Tip.CreateTip()
            };
        }
        else
        {
            // Legacy v1-v3
            var pressureCurve = reader.ReadDouble();
            var velocitySize  = reader.ReadDouble();
            var velocityOpac  = reader.ReadDouble();
            var color         = Color.FromUInt32(reader.ReadUInt32());
            var pressToSize   = reader.ReadBoolean();
            var pressToOpac   = reader.ReadBoolean();
            var velToSize     = reader.ReadBoolean();
            var velToOpac     = reader.ReadBoolean();
            var flow          = reader.ReadDouble();
            var grain         = reader.ReadDouble();
            var smoothing     = reader.ReadDouble();

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

            var sizeDyn = new ParameterDynamics
            {
                PressureEnabled  = pressToSize,
                Kind             = curveKind,
                Gamma            = (float)pressureCurve,
                Min              = (float)prSzMin,
                Max              = (float)prSzMax,
                X1 = bx1, Y1 = by1, X2 = bx2, Y2 = by2,
                VelocityEnabled  = velToSize,
                VelocityStrength = (float)velocitySize
            };
            var opacDyn = new ParameterDynamics
            {
                PressureEnabled  = pressToOpac,
                Kind             = curveKind,
                Gamma            = (float)pressureCurve,
                Min              = (float)prOpMin,
                Max              = (float)prOpMax,
                X1 = bx1, Y1 = by1, X2 = bx2, Y2 = by2,
                VelocityEnabled  = velToOpac,
                VelocityStrength = (float)velocityOpac
            };

            asset.Tip = ReadTip(reader);
            asset.Preset = new BrushPreset(name, kind, size, opacity, hardness, spacing, color)
            {
                Dynamics  = BrushDynamics.FromLegacy(sizeDyn, opacDyn),
                Flow      = flow,
                Grain     = grain,
                Smoothing = smoothing,
                Tip       = asset.Tip.CreateTip()
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
        writer.Write((int)p.Kind);
        writer.Write(p.Size);
        writer.Write(p.Opacity);
        writer.Write(p.Hardness);
        writer.Write(p.Spacing);
        writer.Write(p.Color.ToUInt32());
        writer.Write(p.Flow);
        writer.Write(p.Grain);
        writer.Write(p.Smoothing);
        writer.Write(p.Dynamics.Serialize());
        WriteTip(writer, asset.Tip);
    }

    // ── Helpers (kept for legacy reading) ────────────────────────────────────

    private static ParameterDynamics ReadParameterDynamics(BinaryReader r) => new()
    {
        PressureEnabled  = r.ReadBoolean(),
        Kind             = (ResponseCurveKind)r.ReadInt32(),
        Gamma            = r.ReadSingle(),
        X1               = r.ReadSingle(),
        Y1               = r.ReadSingle(),
        X2               = r.ReadSingle(),
        Y2               = r.ReadSingle(),
        Min              = r.ReadSingle(),
        Max              = r.ReadSingle(),
        VelocityEnabled  = r.ReadBoolean(),
        VelocityStrength = r.ReadSingle()
    };

    private static BrushTipData ReadTip(BinaryReader reader)
    {
        var kind   = (BrushTipStorageKind)reader.ReadInt32();
        var shape  = (BrushTipShape)reader.ReadInt32();
        var aspect = reader.ReadSingle();
        var pngLen = reader.ReadInt32();
        var png    = pngLen > 0 ? reader.ReadBytes(pngLen) : [];

        var tip = new BrushTipData { Kind = kind, Shape = shape, AspectRatio = aspect, PngBytes = png };

        if (kind == BrushTipStorageKind.Compound)
        {
            var count = reader.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                var blend    = (StampLayerBlend)reader.ReadInt32();
                var layerOpc = reader.ReadSingle();
                var scale    = reader.ReadSingle();
                var rotation = reader.ReadSingle();
                var subTip   = ReadTip(reader);
                tip.SubLayers.Add(new StampLayerData
                {
                    Tip = subTip, Blend = blend, Opacity = layerOpc, Scale = scale, Rotation = rotation
                });
            }
        }

        return tip;
    }

    private static void WriteTip(BinaryWriter writer, BrushTipData tip)
    {
        writer.Write((int)tip.Kind);
        writer.Write((int)tip.Shape);
        writer.Write(tip.AspectRatio);
        writer.Write(tip.PngBytes.Length);
        if (tip.PngBytes.Length > 0)
            writer.Write(tip.PngBytes);

        if (tip.Kind == BrushTipStorageKind.Compound)
        {
            writer.Write(tip.SubLayers.Count);
            foreach (var layer in tip.SubLayers)
            {
                writer.Write((int)layer.Blend);
                writer.Write(layer.Opacity);
                writer.Write(layer.Scale);
                writer.Write(layer.Rotation);
                WriteTip(writer, layer.Tip);
            }
        }
    }
}
