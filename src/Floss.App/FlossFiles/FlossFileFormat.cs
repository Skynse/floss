using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Floss.App.Document;
using Floss.App.ImageFiles;
using SkiaSharp;

namespace Floss.App.FlossFiles;

public static class FlossFileFormat
{
    public const string Extension = ".floss";
    private const string MimeType = "application/x-floss";
    private const string ManifestPath = "document.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public sealed record LoadedDocument(DrawingDocument Document, string? TimelapseSessionId);

    public static void Save(Stream stream, DrawingDocument document, string? timelapseSessionId = null)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        WriteTextEntry(archive, "mimetype", MimeType, CompressionLevel.NoCompression);

        var manifest = CreateManifest(document, timelapseSessionId);
        WriteJsonEntry(archive, ManifestPath, manifest);

        for (var i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            if (layer.IsGroup) continue;

            var entry = archive.CreateEntry($"layers/layer{i}.bgra", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            var tiles = layer.CaptureTiles();
            var txBuf = new byte[4];
            var tyBuf = new byte[4];
            foreach (var ((tx, ty), tile) in tiles)
            {
                BitConverter.TryWriteBytes(txBuf, tx);
                BitConverter.TryWriteBytes(tyBuf, ty);
                entryStream.Write(txBuf);
                entryStream.Write(tyBuf);
                entryStream.Write(tile);
            }
        }

        WriteMergedImage(archive, document, "mergedimage.png");
        WriteMergedImage(archive, document, "preview.png", maxSide: 512);
    }

    public static LoadedDocument LoadDocument(Stream stream)
    {
        var document = LoadCore(stream, out var timelapseSessionId);
        return new LoadedDocument(document, timelapseSessionId);
    }

    public static DrawingDocument Load(Stream stream)
        => LoadDocument(stream).Document;

    private static DrawingDocument LoadCore(Stream stream, out string? timelapseSessionId)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        var manifestEntry = archive.GetEntry(ManifestPath)
            ?? throw new InvalidDataException("Floss document is missing document.json.");

        FlossManifest manifest;
        using (var manifestStream = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<FlossManifest>(manifestStream, JsonOptions)
                ?? throw new InvalidDataException("Floss document manifest could not be read.");
        }

        timelapseSessionId = manifest.TimelapseSessionId;

        if (!string.Equals(manifest.MimeType, MimeType, StringComparison.Ordinal))
            throw new InvalidDataException("File is not a Floss document.");

        var document = new DrawingDocument(Math.Max(1, manifest.Width), Math.Max(1, manifest.Height));
        document.ClearForImport();

        if (manifest.PaperColor is { } pc)
            document.PaperColor = Avalonia.Media.Color.FromArgb(pc.A, pc.R, pc.G, pc.B);

        var layers = new List<DrawingLayer>(manifest.Layers.Count);
        for (var i = 0; i < manifest.Layers.Count; i++)
        {
            var info = manifest.Layers[i];
            var layer = document.CreateLayerForImport(
                string.IsNullOrWhiteSpace(info.Name) ? $"Layer {i + 1}" : info.Name,
                info.IsGroup,
                Math.Max(1, info.Width),
                Math.Max(1, info.Height));

            layer.IsVisible = info.IsVisible;
            layer.IsLocked = info.IsLocked;
            layer.Opacity = Math.Clamp(info.Opacity, 0, 1);
            layer.BlendMode = string.IsNullOrWhiteSpace(info.BlendMode) ? "Normal" : info.BlendMode;
            layer.OffsetX = info.OffsetX;
            layer.OffsetY = info.OffsetY;
            layer.IsOpen = info.IsOpen;
            layer.IsClipping = info.IsClipping;
            layer.IsReference = info.IsReference;
            layer.IsPaper = info.IsPaper;
            layer.IndentLevel = Math.Max(0, info.IndentLevel);
            if (info.LayerColor is { } lc)
                layer.LayerColor = Avalonia.Media.Color.FromArgb(255, (byte)lc.R, (byte)lc.G, (byte)lc.B);
            layer.ExpressionColor = info.ExpressionColor;

            if (!layer.IsGroup && !string.IsNullOrWhiteSpace(info.PixelPath))
                LoadLayerPixels(archive, layer, info.PixelPath);

            layers.Add(layer);
            document.AppendLayerForImport(layer);
        }

        for (var i = 0; i < manifest.Layers.Count; i++)
        {
            var parentIndex = manifest.Layers[i].ParentIndex;
            if (parentIndex is < 0 or null || parentIndex >= layers.Count) continue;

            layers[i].Parent = layers[parentIndex.Value];
            layers[parentIndex.Value].Children.Add(layers[i]);
        }

        document.FinalizeImport(Math.Clamp(manifest.ActiveLayerIndex, 0, Math.Max(0, layers.Count - 1)));
        document.PaperLayer = layers.FirstOrDefault(l => l.IsPaper);
        return document;
    }

    private static FlossManifest CreateManifest(DrawingDocument document, string? timelapseSessionId = null)
    {
        var layerIndexes = new Dictionary<DrawingLayer, int>();
        for (var i = 0; i < document.Layers.Count; i++)
            layerIndexes[document.Layers[i]] = i;

        var layers = new List<FlossLayerManifest>(document.Layers.Count);
        for (var i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            layers.Add(new FlossLayerManifest
            {
                Name = layer.Name,
                IsGroup = layer.IsGroup,
                IsVisible = layer.IsVisible,
                IsLocked = layer.IsLocked,
                Opacity = layer.Opacity,
                BlendMode = layer.BlendMode,
                OffsetX = layer.OffsetX,
                OffsetY = layer.OffsetY,
                Width = layer.Width,
                Height = layer.Height,
                IsOpen = layer.IsOpen,
                IsClipping = layer.IsClipping,
                IsReference = layer.IsReference,
                IsPaper = layer.IsPaper,
                IndentLevel = layer.IndentLevel,
                ParentIndex = layer.Parent != null && layerIndexes.TryGetValue(layer.Parent, out var pIdx) ? pIdx : null,
                PixelPath = !layer.IsGroup ? $"layers/layer{i}.bgra" : null,
                LayerColor = layer.LayerColor is { } lc ? new SerializableColor(lc.R, lc.G, lc.B) : null,
                ExpressionColor = layer.ExpressionColor
            });
        }

        return new FlossManifest
        {
            MimeType = MimeType,
            FormatVersion = 1,
            App = "Floss",
            Width = document.Width,
            Height = document.Height,
            ActiveLayerIndex = document.ActiveLayerIndex,
            PaperColor = new SerializableColor(document.PaperColor.R, document.PaperColor.G, document.PaperColor.B, document.PaperColor.A),
            Layers = layers,
            TimelapseSessionId = string.IsNullOrWhiteSpace(timelapseSessionId) ? null : timelapseSessionId.Trim()
        };
    }

    private static void LoadLayerPixels(ZipArchive archive, DrawingLayer layer, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Floss document is missing layer payload '{path}'.");

        // Read entire entry into memory — ZipArchiveEntry streams don't support seeking.
        using var entryStream = entry.Open();
        var totalLength = (int)entry.Length;
        var data = new byte[totalLength];
        var offset = 0;
        while (offset < totalLength)
        {
            var read = entryStream.Read(data, offset, totalLength - offset);
            if (read == 0) break;
            offset += read;
        }

        var tileLength = Document.TiledPixelBuffer.TileSize
            * Document.TiledPixelBuffer.TileSize * 4;

        // Empty layer — nothing to restore
        if (totalLength == 0)
        {
            layer.MarkThumbnailDirty();
            return;
        }

        // Detect format: new tile-keyed vs old flat BGRA
        // New format: stream is sequence of (tx:int32, ty:int32, tile:tileLength)
        // Old format: flat BGRA array of width*height*4 bytes
        bool isNewFormat;
        if (totalLength >= 8 + tileLength)
        {
            var tx = BitConverter.ToInt32(data, 0);
            var ty = BitConverter.ToInt32(data, 4);
            // Heuristic: if tx,ty look like reasonable tile coordinates (small ints),
            // and the total length is an exact multiple of (header + tile), it's new format.
            isNewFormat = tx is >= -1000 and <= 1000 && ty is >= -1000 and <= 1000
                && totalLength % (8 + tileLength) == 0;
        }
        else
        {
            isNewFormat = false;
        }

        if (isNewFormat)
        {
            var tiles = new Dictionary<(int, int), byte[]>();
            var pos = 0;
            while (pos + 8 + tileLength <= totalLength)
            {
                var tx = BitConverter.ToInt32(data, pos);
                var ty = BitConverter.ToInt32(data, pos + 4);
                pos += 8;

                var tile = new byte[tileLength];
                Buffer.BlockCopy(data, pos, tile, 0, tileLength);
                pos += tileLength;

                tiles[(tx, ty)] = tile;
            }
            layer.Pixels.RestoreTiles(tiles);
        }
        else
        {
            // Old format: flat BGRA array
            var expected = layer.Width * layer.Height * 4;
            if (data.Length != expected)
                throw new InvalidDataException($"Layer payload '{path}' has {data.Length} bytes but expected {expected} for flat pixel data.");
            layer.Pixels.CopyFromBgra(data, layer.Width, layer.Height);
        }

        layer.MarkThumbnailDirty();
    }

    private static void WriteMergedImage(ZipArchive archive, DrawingDocument document, string path, int? maxSide = null)
    {
        using var bitmap = DocumentRasterizer.RenderFlattenedBitmap(document);
        using var output = maxSide.HasValue ? ResizeForPreview(bitmap, maxSide.Value) : bitmap.Copy();
        using var image = SKImage.FromBitmap(output);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        if (data == null) return;

        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
        data.SaveTo(entryStream);
    }

    private static SKBitmap ResizeForPreview(SKBitmap source, int maxSide)
    {
        var scale = Math.Min(1.0, maxSide / (double)Math.Max(source.Width, source.Height));
        var width = Math.Max(1, (int)Math.Round(source.Width * scale));
        var height = Math.Max(1, (int)Math.Round(source.Height * scale));
        var preview = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));

        using var canvas = new SKCanvas(preview);
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint { IsAntialias = true };
        canvas.DrawBitmap(source, new SKRect(0, 0, width, height), paint);
        return preview;
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string value, CompressionLevel compressionLevel)
    {
        var entry = archive.CreateEntry(path, compressionLevel);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(value);
    }

    private static void WriteJsonEntry(ZipArchive archive, string path, FlossManifest manifest)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var stream = entry.Open();
        JsonSerializer.Serialize(stream, manifest, JsonOptions);
    }

    private sealed class FlossManifest
    {
        [JsonPropertyName("mimetype")] public string MimeType { get; set; } = FlossFileFormat.MimeType;
        [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; } = 1;
        [JsonPropertyName("app")] public string App { get; set; } = "Floss";
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("activeLayerIndex")] public int ActiveLayerIndex { get; set; }
        [JsonPropertyName("paperColor")] public SerializableColor? PaperColor { get; set; }
        [JsonPropertyName("layers")] public List<FlossLayerManifest> Layers { get; set; } = [];
        [JsonPropertyName("timelapseSessionId")] public string? TimelapseSessionId { get; set; }
    }

    private sealed class FlossLayerManifest
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("isGroup")] public bool IsGroup { get; set; }
        [JsonPropertyName("isVisible")] public bool IsVisible { get; set; } = true;
        [JsonPropertyName("isLocked")] public bool IsLocked { get; set; }
        [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1;
        [JsonPropertyName("blendMode")] public string BlendMode { get; set; } = "Normal";
        [JsonPropertyName("offsetX")] public int OffsetX { get; set; }
        [JsonPropertyName("offsetY")] public int OffsetY { get; set; }
        [JsonPropertyName("width")] public int Width { get; set; }
        [JsonPropertyName("height")] public int Height { get; set; }
        [JsonPropertyName("isOpen")] public bool IsOpen { get; set; } = true;
        [JsonPropertyName("isClipping")] public bool IsClipping { get; set; }
        [JsonPropertyName("isReference")] public bool IsReference { get; set; }
        [JsonPropertyName("isPaper")] public bool IsPaper { get; set; }
        [JsonPropertyName("indentLevel")] public int IndentLevel { get; set; }
        [JsonPropertyName("parentIndex")] public int? ParentIndex { get; set; }
        [JsonPropertyName("pixelPath")] public string? PixelPath { get; set; }
        [JsonPropertyName("layerColor")] public SerializableColor? LayerColor { get; set; }
        [JsonPropertyName("expressionColor")] public ExpressionColorMode ExpressionColor { get; set; } = ExpressionColorMode.Color;
    }

    public sealed record SerializableColor(byte R, byte G, byte B, byte A = 255);
}
