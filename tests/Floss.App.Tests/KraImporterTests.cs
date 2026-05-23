using System.IO;
using System.IO.Compression;
using System.Text;
using Floss.App.Document;
using Floss.App.Kra;

namespace Floss.App.Tests;

public class KraImporterTests
{
    [Fact]
    public void Load_ImportsPaintLayerTiles()
    {
        var kra = BuildSampleKra();
        using var stream = new MemoryStream(kra);

        var document = KraImporter.Load(stream);

        TestAssertions.Equal(64, document.Width);
        TestAssertions.Equal(64, document.Height);
        TestAssertions.Equal(1, document.Layers.Count);

        var layer = document.Layers[0];
        TestAssertions.Equal("Ink", layer.Name);
        TestAssertions.Equal("Multiply", layer.BlendMode);
        TestAssertions.True(Math.Abs(layer.Opacity - (127 / 255.0)) < 0.001);
        TestAssertions.True(layer.IsVisible);
        TestAssertions.False(layer.IsLocked);

        layer.Pixels.GetPixel(0, 0, out var b, out var g, out var r, out var a);
        TestAssertions.Equal(10, b);
        TestAssertions.Equal(20, g);
        TestAssertions.Equal(30, r);
        TestAssertions.Equal(255, a);
    }

    [Fact]
    public void Load_RealKritaFile_ImportsExpectedTileCounts()
    {
        const string path = "/home/neckles/Downloads/electrichearts_20250824A_kiki.kra";
        if (!File.Exists(path))
            return;

        using var stream = File.OpenRead(path);
        var document = KraImporter.Load(stream);

        var character = FindLayerByName(document.Layers, "Character | 人物");
        TestAssertions.True(character != null, "Character layer not found");
        TestAssertions.True(character!.Pixels.TileCount > 8000,
            $"Character layer should contain ~8431 tiles, found {character.Pixels.TileCount}");
    }

    private static DrawingLayer? FindLayerByName(IReadOnlyList<DrawingLayer> layers, string name)
    {
        foreach (var layer in layers)
        {
            if (layer.IsGroup)
            {
                var match = FindLayerByName(layer.Children, name);
                if (match != null) return match;
            }
            else if (layer.Name == name)
            {
                return layer;
            }
        }

        return null;
    }

    [Fact]
    public void Load_RealKritaFile_ImportsVisiblePixelContent()
    {
        const string path = "/home/neckles/Downloads/electrichearts_20250824A_kiki.kra";
        if (!File.Exists(path))
            return;

        using var stream = File.OpenRead(path);
        var document = KraImporter.Load(stream);

        TestAssertions.Equal(10000, document.Width);
        TestAssertions.Equal(5000, document.Height);

        var layersWithContent = 0;
        CountLayersWithContent(document.Layers, ref layersWithContent);
        TestAssertions.True(layersWithContent > 0, $"Expected paint layers with pixel content, found {layersWithContent}");
    }

    [Fact]
    public void Load_PreservesLayerOffsetWithoutShiftingTileData()
    {
        var kra = BuildOffsetLayerKra();
        using var stream = new MemoryStream(kra);

        var document = KraImporter.Load(stream);

        TestAssertions.Equal(1, document.Layers.Count);
        var layer = document.Layers[0];
        TestAssertions.Equal(100, layer.OffsetX);
        TestAssertions.Equal(50, layer.OffsetY);

        layer.Pixels.GetPixel(320, 160, out var b, out _, out _, out var a);
        TestAssertions.Equal((byte)255, a);
        TestAssertions.Equal((byte)40, b);

        layer.Pixels.GetPixel(220, 110, out _, out _, out _, out var missingAlpha);
        TestAssertions.Equal((byte)0, missingAlpha);
    }

    [Fact]
    public void Load_ImportsGroupHierarchyInKritaOrder()
    {
        var kra = BuildGroupedKra();
        using var stream = new MemoryStream(kra);

        var document = KraImporter.Load(stream);

        TestAssertions.Equal(3, document.Layers.Count);
        TestAssertions.Equal("Background", document.Layers[0].Name);
        TestAssertions.Equal("Child", document.Layers[1].Name);
        TestAssertions.True(document.Layers[2].IsGroup);
        TestAssertions.Equal("Group", document.Layers[2].Name);
        TestAssertions.Equal("PassThrough", document.Layers[2].BlendMode);
        TestAssertions.Equal(1, document.Layers[2].Children.Count);
        TestAssertions.Equal("Child", document.Layers[2].Children[0].Name);
        TestAssertions.True(ReferenceEquals(document.Layers[2], document.Layers[1].Parent));
    }

    private static void CountLayersWithContent(IReadOnlyList<DrawingLayer> layers, ref int count)
    {
        foreach (var layer in layers)
        {
            if (layer.IsGroup)
            {
                CountLayersWithContent(layer.Children, ref count);
                continue;
            }

            var region = new PixelRegion(0, 0, layer.Width, layer.Height);
            if (layer.Pixels.HasContentTiles(region))
                count++;
        }
    }

    private static byte[] BuildOffsetLayerKra()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "mimetype", "application/x-kra");
            WriteTextEntry(archive, "maindoc.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <DOC xmlns="http://www.calligra.org/DTD/krita" syntaxVersion="2">
                  <IMAGE mime="application/x-kra" name="Sample" width="640" height="480" colorspacename="RGBA">
                    <layers>
                      <layer nodetype="paintlayer" name="Shifted" filename="layer1" compositeop="normal"
                             opacity="255" visible="1" x="100" y="50" colorspacename="RGBA"/>
                    </layers>
                  </IMAGE>
                </DOC>
                """);

            WriteBinaryEntry(archive, "Sample/layers/layer1", BuildPositionedTileLayerFile(320, 160, 40, 80, 120, 255));
        }

        return zipStream.ToArray();
    }

    private static byte[] BuildPositionedTileLayerFile(int tileX, int tileY, byte b, byte g, byte r, byte a)
    {
        var tile = new byte[64 * 64 * 4];
        tile[0] = b;
        tile[1] = g;
        tile[2] = r;
        tile[3] = a;

        var payload = new byte[tile.Length + 1];
        payload[0] = 0;
        Buffer.BlockCopy(tile, 0, payload, 1, tile.Length);

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.Write("VERSION 2\n");
        writer.Write("TILEWIDTH 64\n");
        writer.Write("TILEHEIGHT 64\n");
        writer.Write("PIXELSIZE 4\n");
        writer.Write("DATA 1\n");
        writer.Write($"{tileX},{tileY},LZF,{payload.Length}\n");
        writer.Flush();
        stream.Write(payload);
        return stream.ToArray();
    }

    private static byte[] BuildSampleKra()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "mimetype", "application/x-kra");
            WriteTextEntry(archive, "maindoc.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <DOC xmlns="http://www.calligra.org/DTD/krita" syntaxVersion="2">
                  <IMAGE mime="application/x-kra" name="Sample" width="64" height="64" colorspacename="RGBA">
                    <layers>
                      <layer nodetype="paintlayer" name="Ink" filename="layer1" compositeop="multiply"
                             opacity="127" visible="1" locked="0" x="0" y="0" colorspacename="RGBA"/>
                    </layers>
                  </IMAGE>
                </DOC>
                """);

            WriteBinaryEntry(archive, "Sample/layers/layer1", BuildRawTileLayerFile(10, 20, 30, 255));
        }

        return zipStream.ToArray();
    }

    private static byte[] BuildGroupedKra()
    {
        using var zipStream = new MemoryStream();
        using (var archive = new ZipArchive(zipStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            WriteTextEntry(archive, "mimetype", "application/x-kra");
            WriteTextEntry(archive, "maindoc.xml", """
                <?xml version="1.0" encoding="UTF-8"?>
                <DOC xmlns="http://www.calligra.org/DTD/krita" syntaxVersion="2">
                  <IMAGE mime="application/x-kra" name="Sample" width="64" height="64" colorspacename="RGBA">
                    <layers>
                      <layer nodetype="grouplayer" name="Group" passthrough="1" compositeop="normal" opacity="255" visible="1" collapsed="0">
                        <layers>
                          <layer nodetype="paintlayer" name="Child" filename="child" compositeop="normal" opacity="255" visible="1" x="0" y="0" colorspacename="RGBA"/>
                        </layers>
                      </layer>
                      <layer nodetype="paintlayer" name="Background" filename="background" compositeop="normal" opacity="255" visible="1" x="0" y="0" colorspacename="RGBA"/>
                    </layers>
                  </IMAGE>
                </DOC>
                """);

            WriteBinaryEntry(archive, "Sample/layers/child", BuildRawTileLayerFile(1, 2, 3, 255));
            WriteBinaryEntry(archive, "Sample/layers/background", BuildRawTileLayerFile(4, 5, 6, 255));
        }

        return zipStream.ToArray();
    }

    private static byte[] BuildRawTileLayerFile(byte b, byte g, byte r, byte a)
    {
        var tile = new byte[64 * 64 * 4];
        tile[0] = b;
        tile[1] = g;
        tile[2] = r;
        tile[3] = a;

        var payload = new byte[tile.Length + 1];
        payload[0] = 0;
        Buffer.BlockCopy(tile, 0, payload, 1, tile.Length);

        using var stream = new MemoryStream();
        using var writer = new StreamWriter(stream, Encoding.ASCII, leaveOpen: true) { NewLine = "\n" };
        writer.Write("VERSION 2\n");
        writer.Write("TILEWIDTH 64\n");
        writer.Write("TILEHEIGHT 64\n");
        writer.Write("PIXELSIZE 4\n");
        writer.Write("DATA 1\n");
        writer.Write($"0,0,LZF,{payload.Length}\n");
        writer.Flush();
        stream.Write(payload);
        return stream.ToArray();
    }

    private static void WriteTextEntry(ZipArchive archive, string path, string contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
        writer.Write(contents);
    }

    private static void WriteBinaryEntry(ZipArchive archive, string path, byte[] contents)
    {
        var entry = archive.CreateEntry(path, CompressionLevel.Fastest);
        using var stream = entry.Open();
        stream.Write(contents);
    }
}
