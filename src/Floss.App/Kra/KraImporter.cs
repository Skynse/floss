using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using Floss.App.Canvas.Compositing;
using Floss.App.Document;
using Floss.App.ImageFiles;

namespace Floss.App.Kra;

public static class KraImporter
{
    private const string MimeType = "application/x-kra";
    private static readonly object ZipReadLock = new();

    private readonly record struct LayerPixelJob(DrawingLayer Layer, byte[] Data, int OffsetX, int OffsetY);
    private readonly record struct LayerPendingJob(DrawingLayer Layer, string FileName, int OffsetX, int OffsetY);

    public static DrawingDocument Load(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);
        ValidateMimeType(archive);

        var doc = LoadManifest(archive);
        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var imageElement = doc.Root?.Element(ns + "IMAGE")
            ?? throw new InvalidDataException("KRA file maindoc.xml is missing IMAGE element.");

        ValidateImageMime(imageElement);

        var width = ParseRequiredInt(imageElement, "width");
        var height = ParseRequiredInt(imageElement, "height");
        var imageName = imageElement.Attribute("name")?.Value ?? "Krita Image";
        var archiveRoot = ResolveArchiveRoot(archive, imageName);
        var zipIndex = new KraZipIndex(archive);

        var document = new DrawingDocument(width, height);
        document.ClearForImport();

        var pendingJobs = new List<LayerPendingJob>();
        var importedPaintLayers = 0;
        foreach (var layersElement in imageElement.Elements(ns + "layers"))
        {
            ImportLayers(archiveRoot, layersElement, ns, document, parent: null, depth: 0,
                pendingJobs, ref importedPaintLayers);
        }

        var pixelJobs = ReadLayerBytesParallel(archive, archiveRoot, zipIndex, pendingJobs);
        var layersWithPixels = DecodeLayerPixels(pixelJobs);

        if (importedPaintLayers == 0 || layersWithPixels == 0)
            return LoadPreviewFallback(archive, width, height, imageName);

        document.FinalizeImport();
        return document;
    }

    private static List<LayerPixelJob> ReadLayerBytesParallel(
        ZipArchive archive,
        string archiveRoot,
        KraZipIndex zipIndex,
        List<LayerPendingJob> pendingJobs)
    {
        if (pendingJobs.Count == 0)
            return [];

        var slots = new LayerPixelJob?[pendingJobs.Count];
        Parallel.For(0, pendingJobs.Count, i =>
        {
            var pending = pendingJobs[i];
            var bytes = ReadLayerEntryBytes(archive, archiveRoot, zipIndex, pending.FileName);
            if (bytes != null)
                slots[i] = new LayerPixelJob(pending.Layer, bytes, pending.OffsetX, pending.OffsetY);
        });

        var result = new List<LayerPixelJob>(pendingJobs.Count);
        foreach (var job in slots)
        {
            if (job is { } j)
                result.Add(j);
        }

        return result;
    }

    private static int DecodeLayerPixels(List<LayerPixelJob> pixelJobs)
    {
        if (pixelJobs.Count == 0)
            return 0;

        var decoded = new bool[pixelJobs.Count];
        Parallel.For(0, pixelJobs.Count, i =>
        {
            var job = pixelJobs[i];
            decoded[i] = KraLayerDataReader.TryReadIntoLayer(
                new MemoryStream(job.Data, writable: false),
                job.Layer.Pixels,
                job.OffsetX,
                job.OffsetY);
        });

        var count = 0;
        for (var i = 0; i < decoded.Length; i++)
        {
            if (decoded[i])
                count++;
        }

        return count;
    }

    private static void ImportLayers(
        string archiveRoot,
        XElement layersElement,
        XNamespace ns,
        DrawingDocument document,
        DrawingLayer? parent,
        int depth,
        List<LayerPendingJob> pendingJobs,
        ref int importedPaintLayers)
    {
        foreach (var layerElement in layersElement.Elements(ns + "layer").Reverse())
            ImportNode(archiveRoot, layerElement, ns, document, parent, depth, pendingJobs, ref importedPaintLayers);
    }

    private static void ImportNode(
        string archiveRoot,
        XElement element,
        XNamespace ns,
        DrawingDocument document,
        DrawingLayer? parent,
        int depth,
        List<LayerPendingJob> pendingJobs,
        ref int importedPaintLayers)
    {
        var nodeType = element.Attribute("nodetype")?.Value
            ?? element.Attribute("layertype")?.Value
            ?? "paintlayer";

        switch (nodeType)
        {
            case "paintlayer":
                ImportPaintLayer(archiveRoot, element, document, parent, depth, pendingJobs, ref importedPaintLayers);
                break;
            case "grouplayer":
                ImportGroupLayer(archiveRoot, element, ns, document, parent, depth, pendingJobs, ref importedPaintLayers);
                break;
        }
    }

    private static void ImportPaintLayer(
        string archiveRoot,
        XElement element,
        DrawingDocument document,
        DrawingLayer? parent,
        int depth,
        List<LayerPendingJob> pendingJobs,
        ref int importedPaintLayers)
    {
        if (!IsSupportedColorSpace(element.Attribute("colorspacename")?.Value))
            return;

        var layer = document.AddLayerForImport(ReadName(element, "Paint Layer"));
        ApplyCommonLayerMetadata(element, layer, parent, depth);

        var fileName = ResolveLayerFileName(element);
        if (fileName != null)
            pendingJobs.Add(new LayerPendingJob(layer, fileName, layer.OffsetX, layer.OffsetY));

        importedPaintLayers++;
    }

    private static void ImportGroupLayer(
        string archiveRoot,
        XElement element,
        XNamespace ns,
        DrawingDocument document,
        DrawingLayer? parent,
        int depth,
        List<LayerPendingJob> pendingJobs,
        ref int importedPaintLayers)
    {
        var group = document.CreateLayerForImport(ReadName(element, "Group"), isGroup: true);
        ApplyCommonLayerMetadata(element, group, parent, depth);

        if (element.Attribute("passthrough")?.Value is "1")
            group.BlendMode = BlendMode.PassThrough;

        foreach (var childLayers in element.Elements(ns + "layers"))
            ImportLayers(archiveRoot, childLayers, ns, document, group, depth + 1, pendingJobs, ref importedPaintLayers);

        document.AppendLayerForImport(group);
    }

    private static void ApplyCommonLayerMetadata(
        XElement element,
        DrawingLayer layer,
        DrawingLayer? parent,
        int depth)
    {
        layer.Opacity = ParseOpacity(element.Attribute("opacity")?.Value);
        layer.IsVisible = element.Attribute("visible")?.Value != "0";
        layer.IsLocked = element.Attribute("locked")?.Value == "1";
        layer.IsOpen = element.Attribute("collapsed")?.Value != "1";
        layer.BlendMode = KraBlendModes.Map(element.Attribute("compositeop")?.Value);
        layer.IsClipping = element.Attribute("clippingmask")?.Value == "1"
            || element.Attribute("clipping")?.Value == "1";
        layer.OffsetX = ParseInt(element.Attribute("x")?.Value, 0);
        layer.OffsetY = ParseInt(element.Attribute("y")?.Value, 0);
        layer.IndentLevel = depth;
        layer.Parent = parent;
        parent?.Children.Add(layer);
    }

    private static string? ResolveLayerFileName(XElement element)
    {
        var fileName = element.Attribute("filename")?.Value;
        if (string.IsNullOrWhiteSpace(fileName))
            fileName = element.Attribute("name")?.Value;
        return string.IsNullOrWhiteSpace(fileName) ? null : fileName;
    }

    private static byte[]? ReadLayerEntryBytes(
        ZipArchive archive,
        string archiveRoot,
        KraZipIndex zipIndex,
        string fileName)
    {
        var entry = zipIndex.GetLayerEntry(archiveRoot, fileName);
        if (entry == null)
            return null;

        lock (ZipReadLock)
        {
            using var layerStream = new BufferedStream(entry.Open(), 1 << 20);
            using var buffer = new MemoryStream((int)Math.Max(entry.Length, 0));
            layerStream.CopyTo(buffer);
            return buffer.ToArray();
        }
    }

    private static DrawingDocument LoadPreviewFallback(ZipArchive archive, int width, int height, string imageName)
    {
        var pngEntry = archive.GetEntry("mergedimage.png")
            ?? archive.GetEntry("preview.png")
            ?? archive.Entries.FirstOrDefault(e => e.Name is "mergedimage.png" or "preview.png");
        if (pngEntry == null)
            throw new InvalidDataException("KRA file does not contain importable layer data or a preview image.");

        using var pngStream = pngEntry.Open();
        using var memStream = new MemoryStream();
        pngStream.CopyTo(memStream);
        memStream.Position = 0;

        var document = ImageFileImporter.Load(memStream, imageName);
        if (document.Width != width || document.Height != height)
            document.ResizeForImport(width, height);
        return document;
    }

    private static void ValidateMimeType(ZipArchive archive)
    {
        var mimeEntry = archive.GetEntry("mimetype")
            ?? throw new InvalidDataException("KRA file is missing mimetype entry.");

        string mime;
        using (var mimeStream = mimeEntry.Open())
        using (var reader = new StreamReader(mimeStream))
            mime = reader.ReadToEnd().Trim();

        if (!mime.Contains("krita", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(mime, MimeType, StringComparison.Ordinal))
        {
            throw new InvalidDataException("File is not a valid Krita document.");
        }
    }

    private static XDocument LoadManifest(ZipArchive archive)
    {
        var manifestEntry = archive.GetEntry("maindoc.xml")
            ?? throw new InvalidDataException("KRA file is missing maindoc.xml.");

        using var manifestStream = manifestEntry.Open();
        return XDocument.Load(manifestStream);
    }

    private static void ValidateImageMime(XElement imageElement)
    {
        var mime = imageElement.Attribute("mime")?.Value;
        if (mime != null
            && !string.Equals(mime, MimeType, StringComparison.Ordinal)
            && !mime.Contains("krita", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("KRA file IMAGE element has an unsupported mime type.");
        }
    }

    private static string ResolveArchiveRoot(ZipArchive archive, string imageName)
    {
        if (HasLayerDirectory(archive, imageName))
            return imageName;

        var encoded = ExpandEncodedDirectory(imageName);
        if (!string.Equals(encoded, imageName, StringComparison.Ordinal) && HasLayerDirectory(archive, encoded))
            return encoded;

        var discovered = archive.Entries
            .Select(e => e.FullName)
            .Select(path =>
            {
                var marker = "/layers/";
                var index = path.IndexOf(marker, StringComparison.Ordinal);
                return index > 0 ? path[..index] : null;
            })
            .FirstOrDefault(root => !string.IsNullOrEmpty(root));

        if (!string.IsNullOrEmpty(discovered))
            return discovered;

        return imageName;
    }

    private static bool HasLayerDirectory(ZipArchive archive, string root)
        => archive.Entries.Any(e => e.FullName.StartsWith($"{root}/layers/", StringComparison.Ordinal));

    private static string ExpandEncodedDirectory(string intern)
    {
        var result = new StringBuilder();
        while (true)
        {
            var pos = intern.IndexOf('/');
            if (pos < 0)
                break;

            if (intern.Length > 0 && char.IsDigit(intern[0]))
                result.Append("part");

            result.Append(intern.AsSpan(0, pos + 1));
            intern = intern[(pos + 1)..];
        }

        if (intern.Length > 0 && char.IsDigit(intern[0]))
            result.Append("part");
        result.Append(intern);
        return result.ToString();
    }

    private static bool IsSupportedColorSpace(string? colorSpaceName)
    {
        if (string.IsNullOrWhiteSpace(colorSpaceName))
            return true;

        return colorSpaceName.Equals("RGBA", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadName(XElement element, string fallback)
        => string.IsNullOrWhiteSpace(element.Attribute("name")?.Value) ? fallback : element.Attribute("name")!.Value;

    private static int ParseRequiredInt(XElement element, string attributeName)
    {
        var attr = element.Attribute(attributeName)
            ?? throw new InvalidDataException($"KRA file IMAGE element is missing {attributeName}.");
        return int.Parse(attr.Value);
    }

    private static int ParseInt(string? value, int fallback)
        => int.TryParse(value, out var parsed) ? parsed : fallback;

    private static double ParseOpacity(string? value)
    {
        if (!int.TryParse(value, out var opacity))
            return 1.0;

        opacity = Math.Clamp(opacity, 0, 255);
        return opacity / 255.0;
    }

    private sealed class KraZipIndex
    {
        private readonly Dictionary<string, ZipArchiveEntry> _byFullPath;
        private readonly Dictionary<string, ZipArchiveEntry> _byLayerFileName;

        public KraZipIndex(ZipArchive archive)
        {
            _byFullPath = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            _byLayerFileName = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
            foreach (var entry in archive.Entries)
            {
                _byFullPath[entry.FullName] = entry;
                var marker = entry.FullName.LastIndexOf("/layers/", StringComparison.Ordinal);
                if (marker < 0) continue;
                var fileName = entry.FullName[(marker + "/layers/".Length)..];
                if (fileName.Length > 0)
                    _byLayerFileName[fileName] = entry;
            }
        }

        public ZipArchiveEntry? GetLayerEntry(string archiveRoot, string fileName)
        {
            var path = $"{archiveRoot}/layers/{fileName}";
            if (_byFullPath.TryGetValue(path, out var entry))
                return entry;
            return _byLayerFileName.GetValueOrDefault(fileName);
        }
    }
}
