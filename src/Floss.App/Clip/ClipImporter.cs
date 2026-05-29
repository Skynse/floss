using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Floss.App.Document;
using Microsoft.Data.Sqlite;

namespace Floss.App.Clip;

/// <summary>
/// Imports CLIP Studio Paint (.clip) files into Floss DrawingDocuments.
/// CLIP files are SQLite databases wrapped in a CSFCHUNK container.
/// </summary>
public static class ClipImporter
{
    private const int BlockSize = 256; // each pixel block is 256×256

    public static DrawingDocument Load(Stream stream)
    {
        var container = ClipChunkReader.Read(stream);

        using var conn = OpenInMemory(container.SqliteDatabase);
        var (canvas, layers) = ReadMetadata(conn);

        var doc = new DrawingDocument(canvas.Width, canvas.Height);
        doc.ClearForImport();

        ImportLayers(doc, conn, layers, canvas.RootFolderId, parent: null, container.ExtaChunks);

        doc.FinalizeImport();
        return doc;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SQLite helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static SqliteConnection OpenInMemory(byte[] sqliteBytes)
    {
        var connStr = "Data Source=" + Path.GetTempFileName() + ";Mode=ReadOnly";
        var tempPath = connStr.Split('=')[1].Split(';')[0];
        File.WriteAllBytes(tempPath, sqliteBytes);
        var conn = new SqliteConnection(connStr);
        conn.Open();
        // Clean up temp file when connection closes
        conn.Disposed += (_, _) => { try { File.Delete(tempPath); } catch { } };
        return conn;
    }

    private sealed record CanvasInfo(int Width, int Height, double Dpi, int RootFolderId);

    private sealed record LayerInfo(
        long MainId, string? LayerName, int LayerType, int LayerComposite,
        double LayerOpacity, int LayerVisibility, int LayerFolder,
        int LayerClip, int LayerLock,
        long? LayerFirstChildIndex, long? LayerNextIndex,
        long? LayerRenderMipmap, long? LayerLayerMaskMipmap,
        int LayerOffsetX, int LayerOffsetY,
        int LayerRenderOffscrOffsetX, int LayerRenderOffscrOffsetY,
        int LayerMaskOffsetX, int LayerMaskOffsetY);

    private sealed record OffscreenInfo(long MainId, long? LayerId, string BlockData, byte[] Attribute);

    private sealed record MipmapInfo(long MainId, long BaseMipmapInfo);

    private sealed record MipmapLevelInfo(long MainId, long Offscreen);

    private static (CanvasInfo, List<LayerInfo>) ReadMetadata(SqliteConnection conn)
    {
        CanvasInfo? canvas = null;
        var layers = new List<LayerInfo>();

        using var cmd = conn.CreateCommand();

        // Canvas
        cmd.CommandText = "SELECT CanvasWidth, CanvasHeight, CanvasResolution, CanvasRootFolder FROM Canvas";
        using (var r = cmd.ExecuteReader())
        {
            if (r.Read())
            {
                canvas = new CanvasInfo(
                    r.GetInt32(0), r.GetInt32(1),
                    r.GetDouble(2), (int)r.GetInt64(3));
            }
        }

        // Layers
        cmd.CommandText = "SELECT MainId, LayerName, LayerType, LayerComposite, LayerOpacity, "
            + "LayerVisibility, LayerFolder, LayerClip, LayerLock, "
            + "LayerFirstChildIndex, LayerNextIndex, LayerRenderMipmap, LayerLayerMaskMipmap, "
            + "LayerOffsetX, LayerOffsetY, LayerRenderOffscrOffsetX, LayerRenderOffscrOffsetY, "
            + "LayerMaskOffsetX, LayerMaskOffsetY FROM Layer";
        using (var r = cmd.ExecuteReader())
        {
            while (r.Read())
            {
                layers.Add(new LayerInfo(
                    r.GetInt64(0), SafeString(r, 1), SafeInt(r, 2), SafeInt(r, 3),
                    SafeDouble(r, 4), SafeInt(r, 5), SafeInt(r, 6),
                    SafeInt(r, 7), SafeInt(r, 8),
                    SafeLong(r, 9), SafeLong(r, 10),
                    SafeLong(r, 11), SafeLong(r, 12),
                    SafeInt(r, 13), SafeInt(r, 14),
                    SafeInt(r, 15), SafeInt(r, 16),
                    SafeInt(r, 17), SafeInt(r, 18)));
            }
        }

        if (canvas == null)
            throw new InvalidDataException("CLIP file missing Canvas table");

        return (canvas, layers);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Layer tree traversal
    // ═══════════════════════════════════════════════════════════════════════════

    private static void ImportLayers(DrawingDocument doc, SqliteConnection conn,
        List<LayerInfo> allLayers, int rootFolderId, DrawingLayer? parent,
        Dictionary<string, byte[]> extaChunks)
    {
        var byId = allLayers.ToDictionary(l => l.MainId);
        var mipmaps = ReadMipmaps(conn);
        var mipmapInfos = ReadMipmapInfos(conn);
        var offscreens = ReadOffscreens(conn);

        // Traverse tree starting from root folder
        TraverseFolder(doc, byId, rootFolderId, parent: null, depth: 0,
            mipmaps, mipmapInfos, offscreens, extaChunks, isRoot: true);
    }

    private static void TraverseFolder(DrawingDocument doc,
        Dictionary<long, LayerInfo> byId, long folderId,
        DrawingLayer? parent, int depth,
        Dictionary<long, MipmapInfo> mipmaps,
        Dictionary<long, MipmapLevelInfo> mipmapInfos,
        Dictionary<long, OffscreenInfo> offscreens,
        Dictionary<string, byte[]> extaChunks,
        bool isRoot = false)
    {
        if (!byId.TryGetValue(folderId, out var folder)) return;
        var currentId = folder.LayerFirstChildIndex;

        while (currentId != null && byId.TryGetValue(currentId.Value, out var layer))
        {
            var isFolder = layer.LayerFolder != 0;
            var indent = isRoot ? depth : depth + 1;

            if (isFolder)
            {
                var group = doc.CreateLayerForImport(layer.LayerName ?? "Folder", isGroup: true);
                group.IsVisible = layer.LayerVisibility != 0;
                group.Opacity = layer.LayerOpacity / 256.0;
                group.IsOpen = (layer.LayerFolder & 16) == 0;
                group.BlendMode = MapBlendMode(layer.LayerComposite, isFolder: true);
                group.IsLocked = layer.LayerLock != 0;
                group.IsClipping = layer.LayerClip != 0;
                group.IndentLevel = indent;
                group.OffsetX = layer.LayerOffsetX + layer.LayerRenderOffscrOffsetX;
                group.OffsetY = layer.LayerOffsetY + layer.LayerRenderOffscrOffsetY;
                if (parent != null) { group.Parent = parent; parent.Children.Add(group); }

                TraverseFolder(doc, byId, currentId.Value, group, depth + 1,
                    mipmaps, mipmapInfos, offscreens, extaChunks);

                doc.AppendLayerForImport(group);
            }
            else
            {
                // CSP paper/background layer — convert to Floss native paper layer
                var isPaper = layer.LayerType == 1584;
                var lw = doc.Width;
                var lh = doc.Height;

                var paint = doc.AddLayerForImport(layer.LayerName ?? "Layer",
                    bitmapWidth: lw, bitmapHeight: lh);
                paint.IsVisible = layer.LayerVisibility != 0;
                paint.Opacity = layer.LayerOpacity / 256.0;
                paint.BlendMode = MapBlendMode(layer.LayerComposite, isFolder: false);
                paint.IsLocked = layer.LayerLock != 0 || isPaper;
                paint.IsClipping = layer.LayerClip != 0;
                paint.IndentLevel = indent;
                paint.OffsetX = layer.LayerOffsetX + layer.LayerRenderOffscrOffsetX;
                paint.OffsetY = layer.LayerOffsetY + layer.LayerRenderOffscrOffsetY;
                if (parent != null) { paint.Parent = parent; parent.Children.Add(paint); }

                if (isPaper)
                {
                    paint.IsPaper = true;
                    // Compositor fills paper background via PaperColor — no pixel data needed
                    doc.PaperColor = new Avalonia.Media.Color(255, 255, 255, 255);
                }
                else
                {
                    ImportLayerPixels(paint, layer, mipmaps, mipmapInfos, offscreens, extaChunks);
                }
            }

            currentId = layer.LayerNextIndex;
        }
    }

    /// <summary>Reads layer pixel dimensions and default fill from offscreen attribute.</summary>
    private static (int width, int height, bool defaultWhite) GetLayerDimensions(LayerInfo layer,
        Dictionary<long, MipmapInfo> mipmaps,
        Dictionary<long, MipmapLevelInfo> mipmapInfos,
        Dictionary<long, OffscreenInfo> offscreens)
    {
        if (layer.LayerRenderMipmap == null) return (0, 0, false);
        if (!mipmaps.TryGetValue(layer.LayerRenderMipmap.Value, out var mm)) return (0, 0, false);
        if (!mipmapInfos.TryGetValue(mm.BaseMipmapInfo, out var mmi)) return (0, 0, false);
        if (!offscreens.TryGetValue(mmi.Offscreen, out var off)) return (0, 0, false);

        var parsed = ParseOffscreenAttribute(off.Attribute);
        return (parsed.bw, parsed.bh, parsed.defaultWhite);
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Pixel data extraction
    // ═══════════════════════════════════════════════════════════════════════════

    /// <returns>True if pixel data was successfully imported.</returns>
    private static bool ImportLayerPixels(DrawingLayer layer, LayerInfo info,
        Dictionary<long, MipmapInfo> mipmaps,
        Dictionary<long, MipmapLevelInfo> mipmapInfos,
        Dictionary<long, OffscreenInfo> offscreens,
        Dictionary<string, byte[]> extaChunks)
    {
        var result = GetBlocks(info.LayerRenderMipmap,
            mipmaps, mipmapInfos, offscreens, extaChunks);
        if (result == null) return false;
        var (blocks, attribute) = result.Value;
        if (attribute == null || attribute.Length == 0) return false;

        var parsed = ParseOffscreenAttribute(attribute);
        var (bw, bh, gridW, gridH, defaultWhite, _) = parsed;

        var bgra = DecompressLayer(blocks, gridW, gridH, defaultWhite, bw, bh);
        if (bgra == null || bgra.Length == 0) return false;

        layer.Pixels.CopyFromBgra(bgra, bw, bh);
        layer.MarkThumbnailDirty();
        return true;
    }

    /// <summary>
    /// Parses Exta chunk data to extract compressed pixel blocks.
    /// Exta layout: [idLen:8][id:idLen bytes][secondSize:8][binaryData...]
    /// binaryData layout: [header:8][sections...]
    /// Each section: [chunkSize:4][data:chunkSize bytes]
    /// For block sections: [0:4][BlockDataBeginChunk:20][blockHeader:28][blockData...][BlockDataEndChunk:21]
    /// Block header: index(4), unk0(4), unk1(4), unk2(4), hasData(4), dataSizeBE(4), dataSizeLE(4)
    /// Block data is zlib-compressed (skip 2-byte zlib header, then raw deflate).
    /// </summary>
    private static (byte[][] blocks, byte[]? attribute)? GetBlocks(
        long? mipmapId,
        Dictionary<long, MipmapInfo> mipmaps,
        Dictionary<long, MipmapLevelInfo> mipmapInfos,
        Dictionary<long, OffscreenInfo> offscreens,
        Dictionary<string, byte[]> extaChunks)
    {
        if (mipmapId == null) return null;
        if (!mipmaps.TryGetValue(mipmapId.Value, out var mm)) return null;
        if (!mipmapInfos.TryGetValue(mm.BaseMipmapInfo, out var mmi)) return null;
        if (!offscreens.TryGetValue(mmi.Offscreen, out var off)) return null;
        if (!extaChunks.TryGetValue(off.BlockData, out var chunkData)) return null;

        // Parse Exta header: 8-byte ID length (big-endian), then ID string, then 8-byte second size
        var idLen = (int)ReadBE64(chunkData, 0);
        var binaryOffset = 8 + idLen + 8;
        if (binaryOffset >= chunkData.Length) return null;
        var binaryData = new byte[chunkData.Length - binaryOffset];
        Array.Copy(chunkData, binaryOffset, binaryData, 0, binaryData.Length);
        var blocks = ParseChunkBlocks(binaryData);
        if (blocks == null) return null;

        return (blocks, off.Attribute);
    }

    private static byte[][]? ParseChunkBlocks(byte[] data)
    {
        var beginMarker = Encoding.BigEndianUnicode.GetBytes("BlockDataBeginChunk");
        var endMarker = Encoding.BigEndianUnicode.GetBytes("BlockDataEndChunk");

        // The binary data starts with an 8-byte header (size or padding).
        // The first actual section starts at offset 8, matching the Python
        // check: chunk_binary_data[8:8+len(BlockDataBeginChunk)].
        // However, parse_chunk_with_blocks starts at offset 0 and uses
        // offset+4+len(BlockStatus) / offset+4+len(BlockCheckSum) checks
        // which need the leading 4-byte size prefix. So we parse from 0
        // but verify structure at offset+8.
        var offset = 0;
        var result = new List<byte[]>();

        while (offset < data.Length)
        {
            if (offset + 4 > data.Length) break;
            var chunkSize = (int)ReadBE32(data, offset);
            if (chunkSize <= 0 || offset + chunkSize > data.Length + 4) break;

            // Check BlockStatus marker (11 bytes: \0\0\0\0x0B + marker)
            var isStatus = offset + 4 + 20 < data.Length
                && (int)ReadBE32(data, offset) == 11;
            // BlockCheckSum: \0\0\0\0x0D + marker
            var isChecksum = offset + 4 + 22 < data.Length
                && (int)ReadBE32(data, offset) == 13;

            if (isStatus || isChecksum)
            {
                offset += chunkSize;
                continue;
            }

            // BlockData: marker at offset+8
            var markerOffset = offset + 8;
            var isBegin = markerOffset + beginMarker.Length <= data.Length;
            if (isBegin)
            {
                for (var j = 0; j < beginMarker.Length; j++)
                    if (data[markerOffset + j] != beginMarker[j]) { isBegin = false; break; }
            }

            if (!isBegin)
            {
                // Unknown section type, skip
                offset += chunkSize;
                continue;
            }

            // Block header: first 5 int32s (20 bytes) are always present.
            // Fields 6-7 (dataSizeBE, dataSizeLE) are only present when hasData=1.
            var headerStart = markerOffset + beginMarker.Length;
            var headerEnd = offset + chunkSize - (4 + endMarker.Length);
            if (headerStart + 20 > headerEnd)
            {
                result.Add(Array.Empty<byte>());
                offset += chunkSize;
                continue;
            }

            var hasData = (int)ReadBE32(data, headerStart + 16);
            if (hasData == 1 && headerStart + 28 <= headerEnd)
            {
                var dataSize = (int)ReadBE32(data, headerStart + 20);
                var pixelStart = headerStart + 28;
                var pixelEnd = Math.Min(pixelStart + dataSize, headerEnd);
                var compressed = pixelEnd - pixelStart;
                if (compressed > 2)
                {
                    try
                    {
                        using var deflate = new DeflateStream(
                            new MemoryStream(data, pixelStart + 2, compressed - 2),
                            CompressionMode.Decompress);
                        using var output = new MemoryStream();
                        deflate.CopyTo(output);
                        result.Add(output.ToArray());
                        offset += chunkSize;
                        continue;
                    }
                    catch
                    {
                        // Decompression failed
                    }
                }
            }
            result.Add(Array.Empty<byte>());
            offset += chunkSize;
        }

        return result.Count > 0 ? result.ToArray() : null;
    }

    private static byte[]? DecompressLayer(byte[][] blocks,
        int gridW, int gridH, bool defaultWhite, int bw, int bh)
    {
        var expected = gridW * gridH;
        if (blocks.Length != expected) return null;

        var blockPixels = BlockSize * BlockSize;
        var defaultAlpha = (byte)(defaultWhite ? 255 : 0);
        var bgra = new byte[bw * bh * 4];

        // Fill with default color
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = defaultAlpha;     // B
            bgra[i + 1] = defaultAlpha; // G
            bgra[i + 2] = defaultAlpha; // R
            bgra[i + 3] = defaultAlpha; // A
        }

        for (var by = 0; by < gridH; by++)
        {
            for (var bx = 0; bx < gridW; bx++)
            {
                var block = blocks[by * gridW + bx];
                if (block == null || block.Length == 0) continue;

                if (block.Length == 5 * blockPixels)
                {
                    // RGBA block: 256² alpha + 4×256² interleaved B,G,R,x
                    var px = bx * BlockSize;
                    var py = by * BlockSize;
                    for (var y = 0; y < BlockSize; y++)
                    {
                        var rowOffset = (py + y) * bw * 4;
                        if (rowOffset >= bgra.Length) break;
                        for (var x = 0; x < BlockSize; x++)
                        {
                            var dstX = px + x;
                            if (dstX >= bw) continue;
                            var srcAlphaIdx = y * BlockSize + x;
                            var srcRgbIdx = blockPixels + y * BlockSize * 4 + x * 4;

                            var a = block[srcAlphaIdx];
                            var b = block[srcRgbIdx];
                            var g = block[srcRgbIdx + 1];
                            var r = block[srcRgbIdx + 2];
                            // +3 is unused (x)

                            var dst = rowOffset + dstX * 4;
                            bgra[dst] = b;
                            bgra[dst + 1] = g;
                            bgra[dst + 2] = r;
                            bgra[dst + 3] = a;
                        }
                    }
                }
                else if (block.Length == blockPixels)
                {
                    // Single-channel block (mask) — write as grayscale with alpha
                    var px = bx * BlockSize;
                    var py = by * BlockSize;
                    for (var y = 0; y < BlockSize; y++)
                    {
                        var rowOffset = (py + y) * bw * 4;
                        if (rowOffset >= bgra.Length) break;
                        for (var x = 0; x < BlockSize; x++)
                        {
                            var dstX = px + x;
                            if (dstX >= bw) continue;
                            var v = block[y * BlockSize + x];
                            var dst = rowOffset + dstX * 4;
                            bgra[dst] = v;
                            bgra[dst + 1] = v;
                            bgra[dst + 2] = v;
                            bgra[dst + 3] = 255;
                        }
                    }
                }
            }
        }

        return bgra;
    }

    private static int ReadAttrInt(byte[] data, ref int offset)
    {
        var val = (int)((uint)data[offset] << 24 | (uint)data[offset + 1] << 16
            | (uint)data[offset + 2] << 8 | data[offset + 3]);
        offset += 4;
        return val;
    }

    /// <summary>
    /// Reads a length-prefixed UTF-16BE string: 4-byte BE int = char count,
    /// then 2*count bytes of UTF-16BE data.
    /// </summary>
    private static string ReadAttrStr(byte[] data, ref int offset)
    {
        var charCount = ReadAttrInt(data, ref offset);
        if (charCount <= 0) return "";
        var byteLen = charCount * 2;
        var str = Encoding.BigEndianUnicode.GetString(data, offset, byteLen);
        offset += byteLen;
        return str;
    }

    private static (int bw, int bh, int gridW, int gridH, bool defaultWhite, int[]? attr)
        ParseOffscreenAttribute(byte[] data)
    {
        try
        {
            var offset = 0;
            var headerSize = ReadAttrInt(data, ref offset);
            var infoSectionSize = ReadAttrInt(data, ref offset);
            var extraInfoSize = ReadAttrInt(data, ref offset);
            ReadAttrInt(data, ref offset); // unknown

            // "Parameter\0" UTF-16BE string
            var paramStr = ReadAttrStr(data, ref offset);
            var bw = ReadAttrInt(data, ref offset);
            var bh = ReadAttrInt(data, ref offset);
            var gridW = ReadAttrInt(data, ref offset);
            var gridH = ReadAttrInt(data, ref offset);

            var attrArray = new int[16];
            for (var i = 0; i < 16; i++)
                attrArray[i] = ReadAttrInt(data, ref offset);

            // "InitColor\0" UTF-16BE string
            var initStr = ReadAttrStr(data, ref offset);
            ReadAttrInt(data, ref offset); // unknown
            var defaultWhite = ReadAttrInt(data, ref offset) != 0;
            ReadAttrInt(data, ref offset); // unknown
            ReadAttrInt(data, ref offset); // unknown
            ReadAttrInt(data, ref offset); // unknown

            return (bw, bh, gridW, gridH, defaultWhite, attrArray);
        }
        catch
        {
            return (0, 0, 0, 0, false, null);
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // SQLite query helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static Dictionary<long, MipmapInfo> ReadMipmaps(SqliteConnection conn)
    {
        var result = new Dictionary<long, MipmapInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MainId, BaseMipmapInfo FROM Mipmap";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetInt64(0)] = new MipmapInfo(r.GetInt64(0), r.GetInt64(1));
        return result;
    }

    private static Dictionary<long, MipmapLevelInfo> ReadMipmapInfos(SqliteConnection conn)
    {
        var result = new Dictionary<long, MipmapLevelInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MainId, Offscreen FROM MipmapInfo";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            result[r.GetInt64(0)] = new MipmapLevelInfo(r.GetInt64(0), r.GetInt64(1));
        return result;
    }

    private static Dictionary<long, OffscreenInfo> ReadOffscreens(SqliteConnection conn)
    {
        var result = new Dictionary<long, OffscreenInfo>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT MainId, LayerId, BlockData, Attribute FROM Offscreen";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            // BlockData is BLOB containing ASCII string (e.g., "extrnlid...")
            var blockData = r.IsDBNull(2) ? "" :
                Encoding.ASCII.GetString((byte[])r.GetValue(2));
            var attrBytes = r.IsDBNull(3) ? Array.Empty<byte>() : GetBytes(r, 3);
            result[r.GetInt64(0)] = new OffscreenInfo(r.GetInt64(0),
                r.IsDBNull(1) ? null : r.GetInt64(1), blockData, attrBytes);
        }
        return result;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Blend mode mapping
    // ═══════════════════════════════════════════════════════════════════════════

    private static string MapBlendMode(int composite, bool isFolder)
    {
        return composite switch
        {
            0 => "Normal",
            1 => "Darken",
            2 => "Multiply",
            3 => "ColorDodge",      // CSP "Divide"? idiv -> ColorDodge
            4 => "LinearBurn",
            5 => "Subtract",         // fsub
            6 => "DarkerColor",
            7 => "Lighten",
            8 => "Screen",
            9 => "Divide",           // div → ColorDodge? Actually mapped to "div " in PSD
            10 => "LinearDodge",     // "Add(Glow)"
            11 => "LinearDodge",
            12 => "EasyDodge",       // "Glow Dodge"
            13 => "LighterColor",
            14 => "Overlay",
            15 => "SoftLight",
            16 => "HardLight",
            17 => "VividLight",
            18 => "LinearLight",
            19 => "PinLight",
            20 => "HardMix",
            21 => "Difference",
            22 => "Dissolve",        // "smud" → exclude?
            23 => "Hue",
            24 => "Saturation",
            25 => "Color",
            26 => "Luminosity",
            30 => "PassThrough",
            36 => "Subtract",        // fdiv
            _ => "Normal"
        };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Data helpers
    // ═══════════════════════════════════════════════════════════════════════════

    private static ulong ReadBE64(byte[] buf, int o)
        => ((ulong)buf[o] << 56) | ((ulong)buf[o + 1] << 48) | ((ulong)buf[o + 2] << 40)
         | ((ulong)buf[o + 3] << 32) | ((ulong)buf[o + 4] << 24) | ((ulong)buf[o + 5] << 16)
         | ((ulong)buf[o + 6] << 8) | buf[o + 7];

    private static uint ReadBE32(byte[] buf, int o)
        => ((uint)buf[o] << 24) | ((uint)buf[o + 1] << 16) | ((uint)buf[o + 2] << 8) | buf[o + 3];

    private static string? SafeString(SqliteDataReader r, int idx) => r.IsDBNull(idx) ? null : r.GetString(idx);
    private static int SafeInt(SqliteDataReader r, int idx) => r.IsDBNull(idx) ? 0 : r.GetInt32(idx);
    private static double SafeDouble(SqliteDataReader r, int idx) => r.IsDBNull(idx) ? 0 : r.GetDouble(idx);
    private static long? SafeLong(SqliteDataReader r, int idx) => r.IsDBNull(idx) ? null : r.GetInt64(idx);
    private static byte[] GetBytes(SqliteDataReader r, int idx) => (byte[])r.GetValue(idx);
}
