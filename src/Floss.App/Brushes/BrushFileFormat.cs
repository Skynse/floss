using System;
using System.IO;
using System.Text;
using Avalonia.Media;

namespace Floss.App.Brushes;

public static class BrushFileFormat
{
    public const string Extension = ".flbr";
    private const uint Magic = 0x52424C46; // FLBR, little endian
    private const int Version = 1;

    public static BrushAsset Load(string path)
    {
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);

        if (reader.ReadUInt32() != Magic)
            throw new InvalidDataException("Not a Floss brush file.");
        var version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException($"Unsupported brush file version {version}.");

        var asset = new BrushAsset
        {
            Id = reader.ReadString(),
            FilePath = path
        };

        var name = reader.ReadString();
        var kind = (BrushKind)reader.ReadInt32();
        var size = reader.ReadDouble();
        var opacity = reader.ReadDouble();
        var hardness = reader.ReadDouble();
        var spacing = reader.ReadDouble();
        var pressureCurve = reader.ReadDouble();
        var velocitySize = reader.ReadDouble();
        var velocityOpacity = reader.ReadDouble();
        var color = Color.FromUInt32(reader.ReadUInt32());
        var pressureToSize = reader.ReadBoolean();
        var pressureToOpacity = reader.ReadBoolean();
        var velocityToSize = reader.ReadBoolean();
        var velocityToOpacity = reader.ReadBoolean();
        var flow = reader.ReadDouble();
        var grain = reader.ReadDouble();
        var smoothing = reader.ReadDouble();

        asset.Tip = ReadTip(reader);
        asset.Preset = new BrushPreset(name, kind, size, opacity, hardness, spacing, pressureCurve, velocitySize, velocityOpacity, color)
        {
            PressureToSize = pressureToSize,
            PressureToOpacity = pressureToOpacity,
            VelocityToSize = velocityToSize,
            VelocityToOpacity = velocityToOpacity,
            Flow = flow,
            Grain = grain,
            Smoothing = smoothing,
            Tip = asset.Tip.CreateTip()
        };

        return asset;
    }

    public static void Save(string path, BrushAsset asset)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        var preset = asset.Preset;
        var tip = asset.Tip;

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(string.IsNullOrWhiteSpace(asset.Id) ? Guid.NewGuid().ToString("N") : asset.Id);
        writer.Write(preset.Name);
        writer.Write((int)preset.Kind);
        writer.Write(preset.Size);
        writer.Write(preset.Opacity);
        writer.Write(preset.Hardness);
        writer.Write(preset.Spacing);
        writer.Write(preset.PressureCurve);
        writer.Write(preset.VelocitySize);
        writer.Write(preset.VelocityOpacity);
        writer.Write(preset.Color.ToUInt32());
        writer.Write(preset.PressureToSize);
        writer.Write(preset.PressureToOpacity);
        writer.Write(preset.VelocityToSize);
        writer.Write(preset.VelocityToOpacity);
        writer.Write(preset.Flow);
        writer.Write(preset.Grain);
        writer.Write(preset.Smoothing);
        WriteTip(writer, tip);
    }

    private static BrushTipData ReadTip(BinaryReader reader)
    {
        var kind = (BrushTipStorageKind)reader.ReadInt32();
        var shape = (BrushTipShape)reader.ReadInt32();
        var aspect = reader.ReadSingle();
        var pngLength = reader.ReadInt32();
        var png = pngLength > 0 ? reader.ReadBytes(pngLength) : [];

        return new BrushTipData
        {
            Kind = kind,
            Shape = shape,
            AspectRatio = aspect,
            PngBytes = png
        };
    }

    private static void WriteTip(BinaryWriter writer, BrushTipData tip)
    {
        writer.Write((int)tip.Kind);
        writer.Write((int)tip.Shape);
        writer.Write(tip.AspectRatio);
        writer.Write(tip.PngBytes.Length);
        if (tip.PngBytes.Length > 0)
            writer.Write(tip.PngBytes);
    }
}
