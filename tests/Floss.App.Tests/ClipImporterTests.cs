using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Floss.App.Canvas.Compositing;
using Xunit;

namespace Floss.App.Tests;

public class ClipImporterTests
{
    // ═══════════════════════════════════════════════════════════════════════════
    // CSFCHUNK container parsing
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ChunkReader_ValidFile_ExtractsSqliteAndExta()
    {
        var sqliteDb = new byte[] { 0x53, 0x51, 0x4c, 0x69, 0x74, 0x65 }; // "SQLite"
        var extaPayload = BuildExtaChunk("extrnlid00000000000000000000000000000001");

        var stream = BuildClipStream(
            ("SQLi", sqliteDb),
            ("Exta", extaPayload));

        // Use reflection to call internal ClipChunkReader.Read
        var method = typeof(Floss.App.Clip.ClipImporter).Assembly
            .GetType("Floss.App.Clip.ClipChunkReader")!
            .GetMethod("Read", [typeof(Stream)])!;
        dynamic result = method.Invoke(null, [stream])!;
        var container = result;

        Assert.NotNull(container);
        var sqlite = (byte[])container.SqliteDatabase;
        Assert.Equal(sqliteDb, sqlite);

        var extaChunks = (Dictionary<string, byte[]>)container.ExtaChunks;
        Assert.Contains("extrnlid00000000000000000000000000000001", extaChunks.Keys);
    }

    [Fact]
    public void ChunkReader_MissingSqliteChunk_Throws()
    {
        var stream = BuildClipStream(
            ("Exta", BuildExtaChunk("extrnlid00000000000000000000000000000001")));

        var method = typeof(Floss.App.Clip.ClipImporter).Assembly
            .GetType("Floss.App.Clip.ClipChunkReader")!
            .GetMethod("Read", [typeof(Stream)])!;

        Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [stream]));
    }

    [Fact]
    public void ChunkReader_InvalidHeader_Throws()
    {
        var buf = new byte[100];
        Encoding.ASCII.GetBytes("NOTACLIP").CopyTo(buf, 0);
        using var stream = new MemoryStream(buf);

        var method = typeof(Floss.App.Clip.ClipImporter).Assembly
            .GetType("Floss.App.Clip.ClipChunkReader")!
            .GetMethod("Read", [typeof(Stream)])!;

        var ex = Assert.Throws<TargetInvocationException>(() =>
            method.Invoke(null, [stream]));
        Assert.Contains("Not a CLIP file", ex.InnerException!.Message);
    }

    [Fact]
    public void ChunkReader_MultipleExtaChunks_AllExtracted()
    {
        var sqliteDb = new byte[] { 1, 2, 3 };
        var id1 = "extrnlid00000000000000000000000000000001";
        var id2 = "extrnlid00000000000000000000000000000002";

        var stream = BuildClipStream(
            ("SQLi", sqliteDb),
            ("Exta", BuildExtaChunk(id1)),
            ("Exta", BuildExtaChunk(id2)));

        var method = typeof(Floss.App.Clip.ClipImporter).Assembly
            .GetType("Floss.App.Clip.ClipChunkReader")!
            .GetMethod("Read", [typeof(Stream)])!;
        dynamic result = method.Invoke(null, [stream])!;

        var extaChunks = (Dictionary<string, byte[]>)result.ExtaChunks;
        Assert.Equal(2, extaChunks.Count);
        Assert.Contains(id1, extaChunks.Keys);
        Assert.Contains(id2, extaChunks.Keys);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Offscreen attribute parsing (using real extracted attribute from anat2.clip)
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ParseOffscreenAttribute_RealFile_ExtractsCorrectDimensions()
    {
        var path = "/tmp/clip_offscreen_attr.bin";
        if (!File.Exists(path)) return;

        var attr = File.ReadAllBytes(path);
        Assert.NotEmpty(attr);

        var method = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("ParseOffscreenAttribute",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = (ValueTuple<int, int, int, int, bool, int[]>)method.Invoke(null, [attr])!;

        // anat2.clip: 2500x3000 canvas, block grid 10x12
        Assert.Equal(2500, result.Item1);
        Assert.Equal(3000, result.Item2);
        Assert.True(result.Item3 >= 1, $"gridW={result.Item3}");
        Assert.True(result.Item4 >= 1, $"gridH={result.Item4}");
    }

    [Fact]
    public void ParseOffscreenAttribute_InvalidData_ReturnsZero()
    {
        var attr = new byte[] { 0, 1, 2, 3 };

        var method = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("ParseOffscreenAttribute",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = (ValueTuple<int, int, int, int, bool, int[]>)method.Invoke(null, [attr])!;

        Assert.Equal(0, result.Item1);
    }

    [Fact]
    public void ParseChunkBlocks_EmptyBlocks_ReturnsArray()
    {
        // Use the real Exta chunk from a clip file to test block parsing
        var container = typeof(Floss.App.Clip.ClipImporter).Assembly
            .GetType("Floss.App.Clip.ClipChunkReader")!
            .GetMethod("Read", [typeof(Stream)])!;
        using var stream = File.OpenRead(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CSP", "anat2.clip"));
        dynamic clip = container.Invoke(null, [stream])!;
        var extaChunks = (Dictionary<string, byte[]>)clip.ExtaChunks;

        Assert.NotEmpty(extaChunks);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Real file integration test
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Importer_LoadsRealClipFile()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CSP", "anat2.clip");
        if (!File.Exists(path)) return;

        using var stream = File.OpenRead(path);
        var doc = Floss.App.Clip.ClipImporter.Load(stream);

        Assert.NotNull(doc);
        Assert.Equal(2500, doc.Width);
        Assert.Equal(3000, doc.Height);
        Assert.NotEmpty(doc.Layers);

        // Layer 1 should exist with pixel data
        var layer1 = doc.Layers.FirstOrDefault(l => l.Name == "Layer 1");
        Assert.NotNull(layer1);
        Assert.False(layer1!.IsGroup);
        // Width/Height should match the document dimensions
        Assert.Equal(2500, layer1.Width);
        Assert.Equal(3000, layer1.Height);
    }

    [Fact]
    public void ParseOffscreenAttribute_InvalidData_ReturnsZeros()
    {
        var attr = new byte[] { 0, 0, 0, 1, 2, 3, 4 }; // Too short, invalid

        var method = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("ParseOffscreenAttribute",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        dynamic result = method.Invoke(null, [attr])!;

        Assert.Equal(0, result.Item1);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Block decompression
    // ═══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DecompressLayer_RealBlocks_ProducesNonZeroPixels()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "CSP", "anat2.clip");
        if (!File.Exists(path)) return;

        var readerType = typeof(Floss.App.Clip.ClipImporter).Assembly
            .GetType("Floss.App.Clip.ClipChunkReader")!;
        var readMethod = readerType.GetMethod("Read", [typeof(Stream)])!;
        using var stream = File.OpenRead(path);
        dynamic container = readMethod.Invoke(null, [stream])!;
        var extaChunks = (System.Collections.Generic.Dictionary<string, byte[]>)container.ExtaChunks;

        var key = "extrnlid6EC132F3065F4324B76A11805657408D";
        var chunk = extaChunks[key];
        var idLen = 40;
        var binaryStart = 8 + idLen + 8;
        var binary = new byte[chunk.Length - binaryStart];
        Array.Copy(chunk, binaryStart, binary, 0, binary.Length);

        var parseMethod = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("ParseChunkBlocks",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var blocks = (byte[][])parseMethod.Invoke(null, [binary])!;

        Assert.Equal(120, blocks.Length);
        var withData = blocks.Count(b => b.Length > 0);
        Assert.True(withData > 0);

        var decompressMethod = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("DecompressLayer",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var bgra = (byte[])decompressMethod.Invoke(null, [blocks, 10, 12, false, 2500, 3000])!;

        Assert.NotNull(bgra);
        Assert.Equal(2500 * 3000 * 4, bgra.Length);

        // Check that some pixels are non-zero (not all transparent)
        var nonZeroCount = 0;
        for (var i = 0; i < bgra.Length; i++)
        {
            if (bgra[i] != 0) nonZeroCount++;
            if (nonZeroCount > 100) break;
        }
        Assert.True(nonZeroCount > 0, "Expected non-zero pixel data in decompressed layer");
    }

    [Fact]
    public void DecompressLayer_SingleBlock_FillsCorrectPixels()
    {
        // Build a 256x256 RGBA block: red pixel (R=255, G=0, B=0, A=128)
        // CLIP interleaved format: B, G, R, x (unused)
        var blockSize = 256 * 256;
        var alphaData = new byte[blockSize];
        var rgbData = new byte[blockSize * 4];
        Array.Fill(alphaData, (byte)128);
        // B=0 (already zero-filled), G=0 (already), R=255 at offset 2,5,8,...
        for (var i = 2; i < rgbData.Length; i += 4) rgbData[i] = 255; // R=255

        var block = new byte[blockSize + blockSize * 4];
        Array.Copy(alphaData, 0, block, 0, blockSize);
        Array.Copy(rgbData, 0, block, blockSize, blockSize * 4);

        var blocks = new[] { block };

        var method = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("DecompressLayer",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var bgra = (byte[])method.Invoke(null, [blocks, 1, 1, false, 256, 256])!;

        Assert.Equal(256 * 256 * 4, bgra.Length);

        // Check first pixel: R=255, G=0, B=0, A=128
        Assert.Equal(0, bgra[0]);       // B
        Assert.Equal(0, bgra[1]);       // G
        Assert.Equal(255, bgra[2]);     // R
        Assert.Equal(128, bgra[3]);     // A

        // Check last pixel
        var last = (256 * 256 - 1) * 4;
        Assert.Equal(0, bgra[last]);
        Assert.Equal(0, bgra[last + 1]);
        Assert.Equal(255, bgra[last + 2]);
        Assert.Equal(128, bgra[last + 3]);
    }

    [Fact]
    public void DecompressLayer_DefaultWhite_FillsWhite()
    {
        var blocks = new[] { Array.Empty<byte>() }; // Empty block (not null)

        var method = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("DecompressLayer",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var bgra = (byte[])method.Invoke(null, [blocks, 1, 1, true, 256, 256])!;

        // All pixels should be white (255,255,255,255)
        Assert.Equal(255, bgra[0]);
        Assert.Equal(255, bgra[1]);
        Assert.Equal(255, bgra[2]);
        Assert.Equal(255, bgra[3]);
    }

    [Fact]
    public void DecompressLayer_NullBlock_PreservesDefault()
    {
        var blocks = new byte[][] { null! }; // single null slot in the grid

        var method = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("DecompressLayer",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var bgra = (byte[])method.Invoke(null, [blocks, 1, 1, false, 256, 256])!;

        // With defaultWhite=false, all pixels should be transparent black
        Assert.Equal(0, bgra[0]);
        Assert.Equal(0, bgra[1]);
        Assert.Equal(0, bgra[2]);
        Assert.Equal(0, bgra[3]);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Blend mode mapping
    // ═══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData(0, "Normal")]
    [InlineData(1, "Darken")]
    [InlineData(2, "Multiply")]
    [InlineData(7, "Lighten")]
    [InlineData(8, "Screen")]
    [InlineData(14, "Overlay")]
    [InlineData(15, "SoftLight")]
    [InlineData(21, "Difference")]
    [InlineData(23, "Hue")]
    [InlineData(24, "Saturation")]
    [InlineData(25, "Color")]
    [InlineData(26, "Luminosity")]
    [InlineData(99, "Normal")] // Unknown
    public void MapBlendMode_MapsCorrectly(int cspComposite, string expectedFlossMode)
    {
        var method = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("MapBlendMode",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = (BlendMode)method.Invoke(null, [cspComposite, false])!;
        Assert.Equal(BlendModeExtensions.FromString(expectedFlossMode), result);
    }

    [Fact]
    public void MapBlendMode_Folder_RespectsCompositeMode()
    {
        var method = typeof(Floss.App.Clip.ClipImporter)
            .GetMethod("MapBlendMode",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var result = (BlendMode)method.Invoke(null, [0, true])!;
        Assert.Equal(BlendMode.Normal, result);

        var passThrough = (BlendMode)method.Invoke(null, [30, true])!;
        Assert.Equal(BlendMode.PassThrough, passThrough);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Helpers for building test data
    // ═══════════════════════════════════════════════════════════════════════════

    private static Stream BuildClipStream(params (string, byte[])[] chunks)
    {
        var ms = new MemoryStream();
        // Header: "CSFCHUNK" + 16 zero bytes
        ms.Write(Encoding.ASCII.GetBytes("CSFCHUNK"));
        ms.Write(new byte[16]);

        foreach (var (name, data) in chunks)
        {
            // Each chunk starts with "CHNK" marker
            ms.Write(Encoding.ASCII.GetBytes("CHNK"));
            var nameBytes = Encoding.ASCII.GetBytes(name.PadRight(4).Substring(0, 4));
            ms.Write(nameBytes);
            ms.Write(new byte[4]); // zeros
            ms.Write(ToBigEndian(data.Length));
            ms.Write(data);
        }

        ms.Position = 0;
        return ms;
    }

    private static byte[] ToBigEndian(int val)
    {
        return [(byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val];
    }

    private static byte[] BuildExtaChunk(string id)
    {
        // Exta layout: [idLen:8, big-endian][id: ASCII bytes][secondSize:8, big-endian][binaryData...]
        var idBytes = Encoding.ASCII.GetBytes(id);
        var ms = new MemoryStream();
        ms.Write(ToBigEndian64(idBytes.Length));
        ms.Write(idBytes);
        ms.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 8 }); // secondSize = 8
        ms.Write(new byte[8]); // empty binary data
        return ms.ToArray();
    }

    private static byte[] ToBigEndian64(int val)
    {
        return [(byte)(val >> 56), (byte)(val >> 48), (byte)(val >> 40), (byte)(val >> 32),
                (byte)(val >> 24), (byte)(val >> 16), (byte)(val >> 8), (byte)val];
    }

    private static byte[] BuildOffscreenAttribute(
        int bitmapWidth, int bitmapHeight,
        int blockGridWidth, int blockGridHeight,
        bool defaultWhite)
    {
        var ms = new MemoryStream();

        // Header
        WriteAttrInt(ms, 16);  // headerSize
        WriteAttrInt(ms, 102); // infoSectionSize
        WriteAttrInt(ms, 42);  // extraInfoSize
        WriteAttrInt(ms, 0);   // unknown

        // Parameter string
        WriteAttrStr(ms, "Parameter");

        WriteAttrInt(ms, bitmapWidth);
        WriteAttrInt(ms, bitmapHeight);
        WriteAttrInt(ms, blockGridWidth);
        WriteAttrInt(ms, blockGridHeight);

        // 16 attribute values
        var attrs = new int[] { 0, 1, 4, 2, 0, 0, 0, 0, 1, 2, 0, 0, 0, 0, 0, 0 };
        foreach (var a in attrs) WriteAttrInt(ms, a);

        // InitColor string
        WriteAttrStr(ms, "InitColor");
        WriteAttrInt(ms, 0);                          // unknown
        WriteAttrInt(ms, defaultWhite ? 1 : 0);        // fill
        WriteAttrInt(ms, 0);                          // unknown
        WriteAttrInt(ms, 0);                          // unknown

        // last unknown (extra int when extra_info_section_size == 42)
        if (false) // 58 case — has 4-byte init color
            WriteAttrInt(ms, 0);

        return ms.ToArray();
    }

    private static byte[] BuildBlockChunk(int gridW, int gridH, byte[]?[] blocks)
    {
        var beginMarker = Encoding.BigEndianUnicode.GetBytes("BlockDataBeginChunk");
        var endMarker = Encoding.BigEndianUnicode.GetBytes("BlockDataEndChunk");
        var ms = new MemoryStream();

        for (var i = 0; i < blocks.Length; i++)
        {
            var blockData = blocks[i];
            var hasData = blockData != null && blockData.Length > 0 ? 1 : 0;

            // Compress block data
            byte[] compressed;
            if (hasData == 1 && blockData != null)
            {
                using var cm = new MemoryStream();
                // Write 2-byte zlib header
                cm.WriteByte(0x78);
                cm.WriteByte(0x9C);
                using var deflate = new DeflateStream(cm, CompressionLevel.Fastest, true);
                deflate.Write(blockData);
                deflate.Flush();
                compressed = cm.ToArray();
            }
            else
            {
                compressed = Array.Empty<byte>();
            }

            // Compute chunk size
            var blockSectionSize =
                4 + // chunkSize field
                beginMarker.Length +
                28 + // block header
                compressed.Length +
                4 + endMarker.Length;
            // Round up to 4-byte alignment
            blockSectionSize = (blockSectionSize + 3) & ~3;

            WriteAttrInt(ms, blockSectionSize);
            ms.Write(beginMarker);

            // Block header (28 bytes = 7 × 4)
            WriteAttrInt(ms, i);     // block index
            WriteAttrInt(ms, 0);
            WriteAttrInt(ms, 0);
            WriteAttrInt(ms, 0);
            WriteAttrInt(ms, hasData);
            WriteAttrInt(ms, compressed.Length + 4); // data size BE
            WriteAttrInt(ms, compressed.Length);      // data size LE

            ms.Write(compressed);
            ms.Write(endMarker);
            ms.Write([0, 0, 0, 0]); // end marker size prefix

            // Padding to 4-byte boundary
            while (ms.Length % 4 != 0) ms.WriteByte(0);
        }

        var raw = ms.ToArray();
        var result = new MemoryStream();
        // Write 8-byte header (unknown, seems to be padding)
        result.Write([0, 0, 0, 8, 0, 0, 0, 0]);
        result.Write(raw);
        return result.ToArray();
    }

    private static void WriteAttrInt(MemoryStream ms, int val)
    {
        ms.Write(ToBigEndian(val));
    }

    private static void WriteAttrStr(MemoryStream ms, string val)
    {
        // Length-prefixed UTF-16BE: [charCount:4 BE][2*charCount bytes of UTF-16BE]
        WriteAttrInt(ms, val.Length);
        ms.Write(Encoding.BigEndianUnicode.GetBytes(val));
    }
}
