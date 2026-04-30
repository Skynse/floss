using System;
using System.IO;
using System.IO.Compression;
using System.Xml.Linq;
using Floss.App.Document;
using Floss.App.ImageFiles;
using SkiaSharp;

namespace Floss.App.Kra;

public static class KraImporter
{
    private const string MimeType = "application/x-kra";

    public static DrawingDocument Load(Stream stream)
    {
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        // Validate mimetype
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

        // Read maindoc.xml for dimensions and name
        var manifestEntry = archive.GetEntry("maindoc.xml")
            ?? throw new InvalidDataException("KRA file is missing maindoc.xml.");

        XDocument doc;
        using (var manifestStream = manifestEntry.Open())
            doc = XDocument.Load(manifestStream);

        var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
        var imageElement = doc.Root?.Element(ns + "IMAGE")
            ?? throw new InvalidDataException("KRA file maindoc.xml is missing IMAGE element.");

        var widthAttr = imageElement.Attribute("width");
        var heightAttr = imageElement.Attribute("height");
        var nameAttr = imageElement.Attribute("name");

        if (widthAttr == null || heightAttr == null)
            throw new InvalidDataException("KRA file IMAGE element is missing width or height.");

        var width = int.Parse(widthAttr.Value);
        var height = int.Parse(heightAttr.Value);
        var imageName = nameAttr?.Value;

        // Try mergedimage.png first, then preview.png
        var pngEntry = archive.GetEntry("mergedimage.png")
            ?? archive.GetEntry("preview.png");

        if (pngEntry == null)
            throw new InvalidDataException("KRA file does not contain a readable image preview.");

        using var pngStream = pngEntry.Open();
        using var memStream = new MemoryStream();
        pngStream.CopyTo(memStream);
        memStream.Position = 0;

        return ImageFileImporter.Load(memStream, imageName ?? "Krita Image");
    }
}
