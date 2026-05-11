using System;
using System.IO;
using Floss.App.Canvas;
using Floss.App.Document;
using SkiaSharp;

namespace Floss.App.ImageFiles;

public static class ImageFileImporter
{
    public static DrawingDocument Load(Stream stream, string? name = null)
    {
        using var bitmap = SKBitmap.Decode(stream)
            ?? throw new InvalidDataException("Image file could not be decoded.");

        var width = Math.Max(1, bitmap.Width);
        var height = Math.Max(1, bitmap.Height);
        var document = new DrawingDocument(width, height);
        document.ClearForImport();

        var layer = document.AddLayerForImport(
            string.IsNullOrWhiteSpace(name) ? "Imported Image" : Path.GetFileNameWithoutExtension(name),
            bitmapWidth: width,
            bitmapHeight: height);

        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                var offset = (y * width + x) * 4;
                pixels[offset + 0] = c.Blue;
                pixels[offset + 1] = c.Green;
                pixels[offset + 2] = c.Red;
                pixels[offset + 3] = c.Alpha;
            }
        }

        layer.Pixels.CopyFromBgra(pixels, width, height);
        layer.MarkThumbnailDirty();
        document.FinalizeImport();
        return document;
    }
}

public static class ImageFileExporter
{
    public static void Export(Stream stream, DrawingDocument document, string path)
    {
        var format = FormatFromPath(path);
        var quality = format is SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp ? 95 : 100;

        using var bitmap = DocumentRasterizer.RenderFlattenedBitmap(document);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, quality)
            ?? throw new InvalidDataException($"Image format '{format}' is not supported by this runtime.");

        data.SaveTo(stream);
    }

    public static bool IsSupportedPath(string path) => TryFormatFromPath(path, out _);

    private static SKEncodedImageFormat FormatFromPath(string path)
        => TryFormatFromPath(path, out var format)
            ? format
            : throw new InvalidDataException($"Unsupported image extension '{Path.GetExtension(path)}'.");

    private static bool TryFormatFromPath(string path, out SKEncodedImageFormat format)
    {
        switch (Path.GetExtension(path).ToLowerInvariant())
        {
            case ".png":
                format = SKEncodedImageFormat.Png;
                return true;
            case ".jpg":
            case ".jpeg":
            case ".jpe":
                format = SKEncodedImageFormat.Jpeg;
                return true;
            case ".webp":
                format = SKEncodedImageFormat.Webp;
                return true;
            case ".bmp":
            case ".dib":
                format = SKEncodedImageFormat.Bmp;
                return true;
            case ".gif":
                format = SKEncodedImageFormat.Gif;
                return true;
            case ".tif":
            case ".tiff":
                format = default;
                return false;
            case ".ico":
                format = SKEncodedImageFormat.Ico;
                return true;
            case ".wbmp":
                format = SKEncodedImageFormat.Wbmp;
                return true;
            default:
                format = default;
                return false;
        }
    }
}

public static class DocumentRasterizer
{
    public static unsafe SKBitmap RenderFlattenedBitmap(DrawingDocument document)
    {
        var width = Math.Max(1, document.Width);
        var height = Math.Max(1, document.Height);

        var pc = document.PaperColor;
        uint paper = (uint)(pc.B | (pc.G << 8) | (pc.R << 16) | (pc.A << 24));
        var bgra = new LayerCompositor().CompositeToBgra(document.Layers, width, height, paper);

        var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        fixed (byte* src = bgra)
        {
            var dst = (byte*)bitmap.GetPixels().ToPointer();
            Buffer.MemoryCopy(src, dst, bgra.Length, bgra.Length);
        }

        return bitmap;
    }
}
