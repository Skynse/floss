using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
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

    public static void Save(Stream stream, DrawingDocument document)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true);

        WriteTextEntry(archive, "mimetype", MimeType, CompressionLevel.NoCompression);

        var manifest = CreateManifest(document);
        WriteJsonEntry(archive, ManifestPath, manifest);

        for (var i = 0; i < document.Layers.Count; i++)
        {
            var layer = document.Layers[i];
            if (layer.IsGroup) continue;

            var entry = archive.CreateEntry($"layers/layer{i}.bgra", CompressionLevel.Optimal);
            using var entryStream = entry.Open();
            entryStream.Write(layer.CapturePixels());
        }

        WriteMergedImage(archive, document, "mergedimage.png");
        WriteMergedImage(archive, document, "preview.png", maxSide: 512);
    }

    public static DrawingDocument Load(Stream stream)
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

        if (!string.Equals(manifest.MimeType, MimeType, StringComparison.Ordinal))
            throw new InvalidDataException("File is not a Floss document.");

        var document = new DrawingDocument(Math.Max(1, manifest.Width), Math.Max(1, manifest.Height));
        document.ClearForImport();

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
            layer.IndentLevel = Math.Max(0, info.IndentLevel);

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
        return document;
    }

    private static FlossManifest CreateManifest(DrawingDocument document)
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
                IndentLevel = layer.IndentLevel,
                ParentIndex = layer.Parent != null && layerIndexes.TryGetValue(layer.Parent, out var parentIndex) ? parentIndex : null,
                PixelPath = layer.IsGroup ? null : $"layers/layer{i}.bgra"
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
            Layers = layers
        };
    }

    private static void LoadLayerPixels(ZipArchive archive, DrawingLayer layer, string path)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Floss document is missing layer payload '{path}'.");

        var expectedLength = checked(layer.Width * layer.Height * 4);
        var pixels = new byte[expectedLength];
        using var entryStream = entry.Open();
        var offset = 0;
        while (offset < pixels.Length)
        {
            var read = entryStream.Read(pixels, offset, pixels.Length - offset);
            if (read == 0) break;
            offset += read;
        }

        if (offset != pixels.Length)
            throw new InvalidDataException($"Layer payload '{path}' is truncated.");

        layer.Pixels.CopyFromBgra(pixels, layer.Width, layer.Height);
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
        [JsonPropertyName("layers")] public List<FlossLayerManifest> Layers { get; set; } = [];
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
        [JsonPropertyName("indentLevel")] public int IndentLevel { get; set; }
        [JsonPropertyName("parentIndex")] public int? ParentIndex { get; set; }
        [JsonPropertyName("pixelPath")] public string? PixelPath { get; set; }
    }
}
