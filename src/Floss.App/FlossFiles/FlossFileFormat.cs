using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Floss.App.Canvas.Compositing;
using Floss.App.Document;
using Floss.App.Document.Assistants;
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
            if (layer.Adjustment == null)
                WriteTilePayload(archive, $"layers/layer{i}.bgra", layer.Pixels.CaptureTiles());
            if (layer.HasMask)
                WriteTilePayload(archive, $"layers/layer{i}.mask.bgra", layer.MaskPixels!.CaptureTiles());
        }

        using var merged = DocumentRasterizer.RenderFlattenedBitmap(document);
        WriteMergedImage(archive, merged, "mergedimage.png");
        WriteMergedImage(archive, merged, "preview.png", maxSide: 512);
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
        if (manifest.Dpi is > 0)
            document.SetDpi(manifest.Dpi);

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
            layer.BlendMode = string.IsNullOrWhiteSpace(info.BlendMode) ? BlendMode.Normal : BlendModeExtensions.FromString(info.BlendMode);
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

            if (info.Adjustment is { } adj)
            {
                layer.Adjustment = new AdjustmentLayerData
                {
                    Kind = adj.Kind,
                    Brightness = adj.Brightness, Contrast = adj.Contrast,
                    Hue = adj.Hue, Saturation = adj.Saturation, Luminosity = adj.Luminosity,
                    Levels = adj.Levels,
                    LevelInBlack = adj.LevelInBlack, LevelInWhite = adj.LevelInWhite,
                    LevelGamma = adj.LevelGamma,
                    LevelOutBlack = adj.LevelOutBlack, LevelOutWhite = adj.LevelOutWhite,
                    CurveAll = (float[])adj.CurveAll.Clone(),
                    CurveR = (float[])adj.CurveR.Clone(),
                    CurveG = (float[])adj.CurveG.Clone(),
                    CurveB = (float[])adj.CurveB.Clone(),
                    ShadowR = adj.ShadowR, ShadowG = adj.ShadowG, ShadowB = adj.ShadowB,
                    MidtoneR = adj.MidtoneR, MidtoneG = adj.MidtoneG, MidtoneB = adj.MidtoneB,
                    HighlightR = adj.HighlightR, HighlightG = adj.HighlightG, HighlightB = adj.HighlightB,
                    Threshold = adj.Threshold,
                    GradientStops = (float[])adj.GradientStops.Clone()
                };
            }

            if (!layer.IsGroup && layer.Adjustment == null && !string.IsNullOrWhiteSpace(info.PixelPath))
                LoadLayerTiles(archive, layer.Pixels, info.PixelPath, () => layer.MarkThumbnailDirty());

            if (!string.IsNullOrWhiteSpace(info.MaskPath))
            {
                layer.MaskPixels = new TiledPixelBuffer(layer.Width, layer.Height);
                LoadLayerTiles(archive, layer.MaskPixels, info.MaskPath, () => layer.MarkMaskThumbnailDirty());
                layer.IsMaskVisible = info.IsMaskVisible;
            }

            if (info.RulerSet is { Rulers.Count: > 0 } rulerSetInfo)
            {
                layer.RulerSet = new LayerRulerSet
                {
                    ShowScope = rulerSetInfo.ShowScope,
                    LinkToLayer = rulerSetInfo.LinkToLayer,
                    RulersVisible = rulerSetInfo.RulersVisible,
                };
                layer.RulerSet.Rulers.AddRange(rulerSetInfo.Rulers.Select(AssistantFromManifest));
            }
            else if (info.IsObjectLayer && info.ObjectLayer is { } objectLayerInfo)
            {
                layer.RulerSet = new LayerRulerSet
                {
                    ShowScope = objectLayerInfo.ShowScope,
                    LinkToLayer = objectLayerInfo.LinkToLayer,
                };
                layer.RulerSet.Rulers.Add(AssistantFromManifest(objectLayerInfo.Ruler));
            }
            else if (info.IsObjectLayer)
            {
                layer.IsObjectLayer = true;
                layer.ObjectContent = new ObjectLayerData();
            }

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

        if (manifest.Assistants is { Count: > 0 })
        {
            var targetIndex = Math.Clamp(manifest.ActiveLayerIndex, 0, Math.Max(0, layers.Count - 1));
            document.Assistants.AttachLegacyRulers(
                manifest.Assistants.Select(AssistantFromManifest),
                targetIndex);
        }

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
                BlendMode = layer.BlendMode.ToLegacyString(),
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
                IsObjectLayer = layer.IsObjectLayer,
                PixelPath = !layer.IsGroup && layer.Adjustment == null && !layer.IsObjectLayer ? $"layers/layer{i}.bgra" : null,
                MaskPath = layer.HasMask ? $"layers/layer{i}.mask.bgra" : null,
                IsMaskVisible = layer.IsMaskVisible,
                LayerColor = layer.LayerColor is { } lc ? new SerializableColor(lc.R, lc.G, lc.B) : null,
                ExpressionColor = layer.ExpressionColor,
                Adjustment = layer.Adjustment == null ? null : new AdjustmentLayerManifest
                {
                    Kind = layer.Adjustment.Kind,
                    Brightness = layer.Adjustment.Brightness, Contrast = layer.Adjustment.Contrast,
                    Hue = layer.Adjustment.Hue, Saturation = layer.Adjustment.Saturation, Luminosity = layer.Adjustment.Luminosity,
                    Levels = layer.Adjustment.Levels,
                    LevelInBlack = layer.Adjustment.LevelInBlack, LevelInWhite = layer.Adjustment.LevelInWhite,
                    LevelGamma = layer.Adjustment.LevelGamma,
                    LevelOutBlack = layer.Adjustment.LevelOutBlack, LevelOutWhite = layer.Adjustment.LevelOutWhite,
                    CurveAll = (float[])layer.Adjustment.CurveAll.Clone(),
                    CurveR = (float[])layer.Adjustment.CurveR.Clone(),
                    CurveG = (float[])layer.Adjustment.CurveG.Clone(),
                    CurveB = (float[])layer.Adjustment.CurveB.Clone(),
                    ShadowR = layer.Adjustment.ShadowR, ShadowG = layer.Adjustment.ShadowG, ShadowB = layer.Adjustment.ShadowB,
                    MidtoneR = layer.Adjustment.MidtoneR, MidtoneG = layer.Adjustment.MidtoneG, MidtoneB = layer.Adjustment.MidtoneB,
                    HighlightR = layer.Adjustment.HighlightR, HighlightG = layer.Adjustment.HighlightG, HighlightB = layer.Adjustment.HighlightB,
                    Threshold = layer.Adjustment.Threshold,
                    GradientStops = (float[])layer.Adjustment.GradientStops.Clone()
                },
                RulerSet = layer.RulerSet is not { HasRulers: true } rulerSet ? null : new FlossLayerRulerSetManifest
                {
                    ShowScope = rulerSet.ShowScope,
                    LinkToLayer = rulerSet.LinkToLayer,
                    RulersVisible = rulerSet.RulersVisible,
                    Rulers = rulerSet.Rulers.Select(AssistantToManifest).ToList(),
                },
            });
        }

        return new FlossManifest
        {
            MimeType = MimeType,
            FormatVersion = 1,
            App = "Floss",
            Width = document.Width,
            Height = document.Height,
            Dpi = document.Dpi,
            ActiveLayerIndex = document.ActiveLayerIndex,
            PaperColor = new SerializableColor(document.PaperColor.R, document.PaperColor.G, document.PaperColor.B, document.PaperColor.A),
            Layers = layers,
            TimelapseSessionId = string.IsNullOrWhiteSpace(timelapseSessionId) ? null : timelapseSessionId.Trim()
        };
    }

    private static PaintingAssistant AssistantFromManifest(FlossAssistantManifest a)
    {
        var assistant = new PaintingAssistant
        {
            Id = string.IsNullOrWhiteSpace(a.Id) ? Guid.NewGuid().ToString("N")[..8] : a.Id,
            TypeId = string.IsNullOrWhiteSpace(a.TypeId) ? PaintingAssistant.RulerType : a.TypeId,
            HandleA = new Avalonia.Point(a.HandleAX, a.HandleAY),
            HandleB = new Avalonia.Point(a.HandleBX, a.HandleBY),
            HandleC = new Avalonia.Point(a.HandleCX, a.HandleCY),
            HandleD = new Avalonia.Point(a.HandleDX, a.HandleDY),
            IsVisible = a.IsVisible,
            SnapEnabled = a.SnapEnabled,
            PerspectiveMode = a.PerspectiveMode,
            FisheyeEnabled = a.FisheyeEnabled,
            FovDegrees = a.FovDegrees,
            GridSubdivisions = a.GridSubdivisions,
        };
        assistant.NormalizeLegacyType();
        return assistant;
    }

    private static FlossAssistantManifest AssistantToManifest(PaintingAssistant a)
        => new()
        {
            Id = a.Id,
            TypeId = a.TypeId,
            HandleAX = a.HandleA.X,
            HandleAY = a.HandleA.Y,
            HandleBX = a.HandleB.X,
            HandleBY = a.HandleB.Y,
            HandleCX = a.HandleC.X,
            HandleCY = a.HandleC.Y,
            HandleDX = a.HandleD.X,
            HandleDY = a.HandleD.Y,
            IsVisible = a.IsVisible,
            SnapEnabled = a.SnapEnabled,
            PerspectiveMode = a.PerspectiveMode,
            FisheyeEnabled = a.FisheyeEnabled,
            FovDegrees = a.FovDegrees,
            GridSubdivisions = a.GridSubdivisions,
        };

    private static void WriteTilePayload(ZipArchive archive, string path, Dictionary<(int X, int Y), byte[]> tiles)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Optimal);
        using var entryStream = entry.Open();
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

    private static void LoadLayerTiles(ZipArchive archive, TiledPixelBuffer pixels, string path, Action markDirty)
    {
        var entry = archive.GetEntry(path)
            ?? throw new InvalidDataException($"Floss document is missing layer payload '{path}'.");

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

        if (totalLength == 0)
        {
            markDirty();
            return;
        }

        bool isNewFormat;
        if (totalLength >= 8 + tileLength)
        {
            var tx = BitConverter.ToInt32(data, 0);
            var ty = BitConverter.ToInt32(data, 4);
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
            pixels.RestoreTiles(tiles);
        }
        else
        {
            var expected = pixels.Width * pixels.Height * 4;
            if (data.Length != expected)
                throw new InvalidDataException($"Layer payload '{path}' has {data.Length} bytes but expected {expected} for flat pixel data.");
            pixels.CopyFromBgra(data, pixels.Width, pixels.Height);
        }

        markDirty();
    }

    private static void WriteMergedImage(ZipArchive archive, SKBitmap source, string path, int? maxSide = null)
    {
        SKBitmap? owned = null;
        var bitmap = maxSide.HasValue ? owned = ResizeForPreview(source, maxSide.Value) : source;
        try
        {
            using var image = SKImage.FromBitmap(bitmap);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            if (data == null) return;

            var entry = archive.CreateEntry(path, CompressionLevel.NoCompression);
            using var entryStream = entry.Open();
            data.SaveTo(entryStream);
        }
        finally
        {
            owned?.Dispose();
        }
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
        [JsonPropertyName("dpi")] public int Dpi { get; set; } = 72;
        [JsonPropertyName("activeLayerIndex")] public int ActiveLayerIndex { get; set; }
        [JsonPropertyName("paperColor")] public SerializableColor? PaperColor { get; set; }
        [JsonPropertyName("layers")] public List<FlossLayerManifest> Layers { get; set; } = [];
        [JsonPropertyName("timelapseSessionId")] public string? TimelapseSessionId { get; set; }
        [JsonPropertyName("assistants")] public List<FlossAssistantManifest>? Assistants { get; set; }
    }

    private sealed class FlossAssistantManifest
    {
        [JsonPropertyName("id")] public string Id { get; set; } = "";
        [JsonPropertyName("typeId")] public string TypeId { get; set; } = PaintingAssistant.RulerType;
        [JsonPropertyName("handleAX")] public double HandleAX { get; set; }
        [JsonPropertyName("handleAY")] public double HandleAY { get; set; }
        [JsonPropertyName("handleBX")] public double HandleBX { get; set; }
        [JsonPropertyName("handleBY")] public double HandleBY { get; set; }
        [JsonPropertyName("handleCX")] public double HandleCX { get; set; }
        [JsonPropertyName("handleCY")] public double HandleCY { get; set; }
        [JsonPropertyName("handleDX")] public double HandleDX { get; set; }
        [JsonPropertyName("handleDY")] public double HandleDY { get; set; }
        [JsonPropertyName("isVisible")] public bool IsVisible { get; set; } = true;
        [JsonPropertyName("snapEnabled")] public bool SnapEnabled { get; set; } = true;
        [JsonPropertyName("perspectiveMode")] public PerspectiveAssistantMode PerspectiveMode { get; set; } = PerspectiveAssistantMode.FreeQuad;
        [JsonPropertyName("fisheyeEnabled")] public bool FisheyeEnabled { get; set; }
        [JsonPropertyName("fovDegrees")] public double FovDegrees { get; set; } = 180;
        [JsonPropertyName("gridSubdivisions")] public int GridSubdivisions { get; set; } = 4;
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
        [JsonPropertyName("maskPath")] public string? MaskPath { get; set; }
        [JsonPropertyName("isMaskVisible")] public bool IsMaskVisible { get; set; } = true;
        [JsonPropertyName("layerColor")] public SerializableColor? LayerColor { get; set; }
        [JsonPropertyName("expressionColor")] public ExpressionColorMode ExpressionColor { get; set; } = ExpressionColorMode.Color;
        [JsonPropertyName("adjustment")] public AdjustmentLayerManifest? Adjustment { get; set; }
        [JsonPropertyName("isObjectLayer")] public bool IsObjectLayer { get; set; }
        [JsonPropertyName("objectLayer")] public FlossObjectLayerManifest? ObjectLayer { get; set; }
        [JsonPropertyName("rulerSet")] public FlossLayerRulerSetManifest? RulerSet { get; set; }
    }

    private sealed class FlossObjectLayerManifest
    {
        [JsonPropertyName("showScope")] public RulerShowScope ShowScope { get; set; } = RulerShowScope.AllLayers;
        [JsonPropertyName("linkToLayer")] public bool LinkToLayer { get; set; } = true;
        [JsonPropertyName("ruler")] public FlossAssistantManifest Ruler { get; set; } = new();
    }

    private sealed class FlossLayerRulerSetManifest
    {
        [JsonPropertyName("showScope")] public RulerShowScope ShowScope { get; set; } = RulerShowScope.AllLayers;
        [JsonPropertyName("linkToLayer")] public bool LinkToLayer { get; set; } = true;
        [JsonPropertyName("rulersVisible")] public bool RulersVisible { get; set; } = true;
        [JsonPropertyName("rulers")] public List<FlossAssistantManifest> Rulers { get; set; } = [];
    }

    private sealed class AdjustmentLayerManifest
    {
        [JsonPropertyName("kind")] public AdjustmentKind Kind { get; set; }
        [JsonPropertyName("brightness")] public float Brightness { get; set; }
        [JsonPropertyName("contrast")] public float Contrast { get; set; }
        [JsonPropertyName("hue")] public float Hue { get; set; }
        [JsonPropertyName("saturation")] public float Saturation { get; set; }
        [JsonPropertyName("luminosity")] public float Luminosity { get; set; }
        [JsonPropertyName("levels")] public int Levels { get; set; } = 4;
        [JsonPropertyName("levelInBlack")] public float LevelInBlack { get; set; }
        [JsonPropertyName("levelInWhite")] public float LevelInWhite { get; set; } = 255f;
        [JsonPropertyName("levelGamma")] public float LevelGamma { get; set; } = 1f;
        [JsonPropertyName("levelOutBlack")] public float LevelOutBlack { get; set; }
        [JsonPropertyName("levelOutWhite")] public float LevelOutWhite { get; set; } = 255f;
        [JsonPropertyName("curveAll")] public float[] CurveAll { get; set; } = [0f, 0f, 255f, 255f];
        [JsonPropertyName("curveR")] public float[] CurveR { get; set; } = [0f, 0f, 255f, 255f];
        [JsonPropertyName("curveG")] public float[] CurveG { get; set; } = [0f, 0f, 255f, 255f];
        [JsonPropertyName("curveB")] public float[] CurveB { get; set; } = [0f, 0f, 255f, 255f];
        [JsonPropertyName("shadowR")] public float ShadowR { get; set; }
        [JsonPropertyName("shadowG")] public float ShadowG { get; set; }
        [JsonPropertyName("shadowB")] public float ShadowB { get; set; }
        [JsonPropertyName("midtoneR")] public float MidtoneR { get; set; }
        [JsonPropertyName("midtoneG")] public float MidtoneG { get; set; }
        [JsonPropertyName("midtoneB")] public float MidtoneB { get; set; }
        [JsonPropertyName("highlightR")] public float HighlightR { get; set; }
        [JsonPropertyName("highlightG")] public float HighlightG { get; set; }
        [JsonPropertyName("highlightB")] public float HighlightB { get; set; }
        [JsonPropertyName("threshold")] public float Threshold { get; set; } = 127f;
        [JsonPropertyName("gradientStops")] public float[] GradientStops { get; set; } = [0f, 0f, 0f, 0f, 1f, 1f, 1f, 1f];
    }

    public sealed record SerializableColor(byte R, byte G, byte B, byte A = 255);
}
