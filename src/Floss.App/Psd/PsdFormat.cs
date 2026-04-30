using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Floss.App.Canvas;
using Floss.App.Document;

namespace Floss.App.Psd;

public static class PsdExporter
{
    public static void Export(Stream stream, DrawingDocument document)
    {
        var writer = new PsdBinaryWriter(stream);
        var width = document.Width;
        var height = document.Height;
        var layers = document.Layers.Reverse().ToList();

        WriteHeader(writer, width, height);

        writer.WriteUInt32(0); // Color Mode Data length = 0

        // Image Resources
        var resourcesStream = new MemoryStream();
        WriteImageResources(new PsdBinaryWriter(resourcesStream));
        writer.WriteUInt32((uint)resourcesStream.Length);
        writer.WriteStream(resourcesStream);

        // Layer and Mask Info
        var layerMaskStream = new MemoryStream();
        WriteLayerAndMaskInfo(new PsdBinaryWriter(layerMaskStream), layers, width, height);
        writer.WriteUInt32((uint)layerMaskStream.Length);
        writer.WriteStream(layerMaskStream);

        // Composite image data (flattened)
        WriteCompositeImageData(writer, document, width, height);
    }

    private static void WriteHeader(PsdBinaryWriter w, int width, int height)
    {
        w.WriteAscii("8BPS");
        w.WriteUInt16(1);       // Version 1 = PSD
        w.WriteZeros(6);        // Reserved
        w.WriteUInt16(4);       // Channels (RGBA)
        w.WriteUInt32((uint)height);
        w.WriteUInt32((uint)width);
        w.WriteUInt16(8);       // Bit depth
        w.WriteUInt16(3);       // RGB color mode
    }

    private static void WriteImageResources(PsdBinaryWriter w)
    {
        // Resolution info (resource 1005)
        w.WriteAscii("8BIM");
        w.WriteUInt16(1005);
        w.WritePascalString("");
        w.WriteUInt32(16);
        w.WriteUInt32(72 << 16);  // HResolution (fixed-point)
        w.WriteUInt16(1);          // HResUnit
        w.WriteUInt16(1);          // WidthUnit
        w.WriteUInt32(72 << 16);  // VResolution
        w.WriteUInt16(1);          // VResUnit
        w.WriteUInt16(1);          // HeightUnit
    }

    private static void WriteLayerAndMaskInfo(PsdBinaryWriter w, List<DrawingLayer> layers, int width, int height)
    {
        var layerInfoStream = new MemoryStream();
        var layerW = new PsdBinaryWriter(layerInfoStream);

        layerW.WriteInt16((short)(-layers.Count));

        var channelPositions = new List<long[]>();
        foreach (var layer in layers)
            channelPositions.Add(WriteLayerRecord(layerW, layer));

        for (int i = 0; i < layers.Count; i++)
            WriteLayerChannels(layerW, layers[i], channelPositions[i]);

        var layerInfoData = layerInfoStream.ToArray();
        if (layerInfoData.Length % 2 != 0)
            Array.Resize(ref layerInfoData, layerInfoData.Length + 1);

        w.WriteUInt32((uint)layerInfoData.Length);
        w.WriteBytes(layerInfoData);

        w.WriteUInt32(0); // Global layer mask info: empty
    }

    private static long[] WriteLayerRecord(PsdBinaryWriter w, DrawingLayer layer)
    {
        var layerWidth = layer.Width;
        var layerHeight = layer.Height;

        w.WriteInt32(layer.OffsetY);
        w.WriteInt32(layer.OffsetX);
        w.WriteInt32(layer.OffsetY + layerHeight);
        w.WriteInt32(layer.OffsetX + layerWidth);

        w.WriteUInt16(4); // 4 channels

        var positions = new long[4];
        for (int c = -1; c < 3; c++)
        {
            w.WriteInt16((short)c);
            positions[c + 1] = w.Position;
            w.WriteUInt32(0); // length placeholder
        }

        w.WriteAscii("8BIM");
        w.WriteAscii(BlendModeKey(layer.BlendMode));
        w.WriteByte((byte)Math.Clamp((int)(layer.Opacity * 255), 0, 255));
        w.WriteByte((byte)(layer.IsClipping ? 1 : 0));
        w.WriteByte((byte)(layer.IsVisible ? 0 : 2)); // flags: bit 1 = hidden
        w.WriteByte(0); // filler

        // Extra data: just the layer name
        var extra = new MemoryStream();
        var extraW = new PsdBinaryWriter(extra);
        var nameBytes = Encoding.ASCII.GetBytes(layer.Name);
        var nameLen = Math.Min(nameBytes.Length, 255);
        extraW.WriteByte((byte)nameLen);
        extraW.WriteBytes(nameBytes.AsSpan(0, nameLen).ToArray());
        var padded = (1 + nameLen + 3) & ~3;
        extraW.WriteZeros(padded - (1 + nameLen));

        w.WriteUInt32((uint)extra.Length);
        w.WriteStream(extra);

        return positions;
    }

    private static void WriteLayerChannels(PsdBinaryWriter w, DrawingLayer layer, long[] lengthPositions)
    {
        var width = layer.Width;
        var height = layer.Height;

        for (int ch = -1; ch < 3; ch++)
        {
            var data = CompressChannelRle(layer, width, height, ch);

            var savedPos = w.Position;
            w.Position = lengthPositions[ch + 1];
            w.WriteUInt32((uint)data.Length);
            w.Position = savedPos;

            w.WriteBytes(data);
        }
    }

    private static byte[] CompressChannelRle(DrawingLayer layer, int width, int height, int channel)
    {
        var result = new MemoryStream();
        var w = new PsdBinaryWriter(result);

        w.WriteUInt16(1); // RLE

        var rowCountsPos = result.Position;
        for (int i = 0; i < height; i++)
            w.WriteUInt16(0);

        var rowCounts = new ushort[height];

        for (int y = 0; y < height; y++)
        {
            var rowStart = (int)result.Position;
            var values = new byte[width];
            for (int x = 0; x < width; x++)
            {
                layer.Pixels.GetPixel(x, y, out var b, out var g, out var r, out var a);
                values[x] = channel == -1
                    ? a
                    : channel switch
                    {
                        0 => r,
                        1 => g,
                        _ => b
                    };
            }
            PackBitsEncode(w, values);
            rowCounts[y] = (ushort)((int)result.Position - rowStart);
        }

        var finalData = result.ToArray();
        for (int y = 0; y < height; y++)
        {
            var bytes = new byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(bytes, rowCounts[y]);
            Array.Copy(bytes, 0, finalData, (int)rowCountsPos + y * 2, 2);
        }

        return finalData;
    }

    private static void PackBitsEncode(PsdBinaryWriter w, byte[] data)
    {
        int i = 0;
        while (i < data.Length)
        {
            int runLen = 1;
            while (i + runLen < data.Length && data[i + runLen] == data[i] && runLen < 128)
                runLen++;

            if (runLen >= 3)
            {
                w.WriteByte((byte)(257 - runLen));
                w.WriteByte(data[i]);
                i += runLen;
            }
            else
            {
                int litLen = 1;
                while (i + litLen < data.Length && litLen < 128)
                {
                    if (i + litLen + 2 < data.Length &&
                        data[i + litLen] == data[i + litLen + 1] &&
                        data[i + litLen] == data[i + litLen + 2])
                        break;
                    litLen++;
                }
                w.WriteByte((byte)(litLen - 1));
                for (int j = 0; j < litLen; j++)
                    w.WriteByte(data[i + j]);
                i += litLen;
            }
        }
    }

    private static unsafe void WriteCompositeImageData(PsdBinaryWriter w, DrawingDocument document, int width, int height)
    {
        var compositor = new LayerCompositor();
        compositor.Composite(document.Layers, width, height, document.PaperColor, document.PaperVisible);

        w.WriteUInt16(0); // Raw (uncompressed)

        using var frame = compositor.Bitmap.Lock();
        var pixels = (byte*)frame.Address;
        var stride = frame.RowBytes;

        // PSD composite: R, G, B, A channels planar. Header declares four channels.
        int[] channelBytes = [2, 1, 0, 3]; // BGRA → R, G, B, A
        foreach (var ch in channelBytes)
        {
            for (int y = 0; y < height; y++)
            {
                var row = pixels + y * stride;
                for (int x = 0; x < width; x++)
                    w.WriteByte(row[x * 4 + ch]);
            }
        }
    }

    private static string BlendModeKey(string blendMode) => blendMode switch
    {
        "Normal" => "norm",
        "Dissolve" => "diss",
        "Darken" => "dark",
        "Multiply" => "mul ",
        "ColorBurn" => "idiv",
        "LinearBurn" => "lbrn",
        "DarkerColor" => "dkCl",
        "Lighten" => "lite",
        "Screen" => "scrn",
        "ColorDodge" => "div ",
        "LinearDodge" => "lddg",
        "LighterColor" => "lgCl",
        "Overlay" => "over",
        "SoftLight" => "sLit",
        "HardLight" => "hLit",
        "VividLight" => "vLit",
        "LinearLight" => "lLit",
        "PinLight" => "pLit",
        "HardMix" => "hMix",
        "Difference" => "diff",
        "Exclusion" => "smud",
        "Subtract" => "fsub",
        "Divide" => "fdiv",
        "Hue" => "hue ",
        "Saturation" => "sat ",
        "Color" => "colr",
        "Luminosity" => "lum ",
        "PassThrough" => "pass",
        _ => "norm"
    };
}

public class PsdBinaryWriter
{
    private readonly Stream _stream;

    public PsdBinaryWriter(Stream stream)
    {
        _stream = stream;
    }

    public long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public void WriteByte(byte value) => _stream.WriteByte(value);
    public void WriteBytes(byte[] data) => _stream.Write(data, 0, data.Length);

    public void WriteZeros(int count)
    {
        for (int i = 0; i < count; i++) _stream.WriteByte(0);
    }

    public void WriteUInt16(ushort value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteInt16(short value)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteInt16BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteUInt32(uint value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteInt32(int value)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(b, value);
        _stream.Write(b);
    }

    public void WriteAscii(string text) => _stream.Write(Encoding.ASCII.GetBytes(text));

    public void WritePascalString(string text)
    {
        var bytes = Encoding.ASCII.GetBytes(text);
        var len = Math.Min(bytes.Length, 255);
        _stream.WriteByte((byte)len);
        _stream.Write(bytes, 0, len);
        if ((1 + len) % 2 != 0) _stream.WriteByte(0);
    }

    public void WriteStream(Stream stream)
    {
        stream.Position = 0;
        stream.CopyTo(_stream);
    }
}
