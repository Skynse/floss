using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Floss.App.Clip;

/// <summary>
/// Reads the CLIP Studio Paint container format (CSFCHUNK header + chunks).
/// Extracts the embedded SQLite database and Exta pixel-data chunks.
/// </summary>
internal static class ClipChunkReader
{
    private const int ChunkHeaderSize = 16; // "CHNK" + name(4) + zeros(4) + size(4)

    /// <summary>
    /// Parsed CLIP file structure — the SQLite database bytes and a dictionary
    /// of Exta chunk IDs → binary data.
    /// </summary>
    public sealed record ClipContainer(byte[] SqliteDatabase, Dictionary<string, byte[]> ExtaChunks);

    public static ClipContainer Read(string path)
    {
        using var fs = File.OpenRead(path);
        return Read(fs);
    }

    public static ClipContainer Read(Stream stream)
    {
        var buf = new byte[8];
        ReadExact(stream, buf, 0, 8);
        if (Encoding.ASCII.GetString(buf, 0, 8) != "CSFCHUNK")
            throw new InvalidDataException("Not a CLIP file: expected 'CSFCHUNK' header");

        // Skip 16 bytes of header padding (24-byte total header, 8 already read)
        var padding = new byte[16];
        ReadExact(stream, padding, 0, 16);

        // Read first chunk marker
        ReadExact(stream, buf, 0, 4);
        if (Encoding.ASCII.GetString(buf, 0, 4) != "CHNK")
            throw new InvalidDataException("Expected CHNK marker after header");

        // Back up — the CHNK is part of the first chunk
        stream.Seek(-4, SeekOrigin.Current);

        byte[]? sqliteDb = null;
        var extaChunks = new Dictionary<string, byte[]>();

        var headerBuf = new byte[ChunkHeaderSize];
        while (true)
        {
            var read = stream.Read(headerBuf, 0, ChunkHeaderSize);
            if (read == 0) break;
            if (read != ChunkHeaderSize)
                throw new InvalidDataException($"Truncated chunk header: read {read}/{ChunkHeaderSize} bytes");

            var name = Encoding.ASCII.GetString(headerBuf, 4, 4);
            var size = ReadBigEndianInt32(headerBuf, 12);

            var data = new byte[size];
            ReadExact(stream, data, 0, size);

            if (name == "SQLi")
            {
                sqliteDb = data;
            }
            else if (name == "Exta")
            {
                var id = ReadExtaId(data);
                if (id != null)
                    extaChunks[id] = data;
            }
            // Other chunk types (if any) are ignored
        }

        if (sqliteDb == null)
            throw new InvalidDataException("CLIP file missing SQLi chunk (SQLite database)");

        return new ClipContainer(sqliteDb, extaChunks);
    }

    private static string? ReadExtaId(byte[] data)
    {
        // Exta chunk: 8-byte length (big-endian) of the ID string, then ASCII ID
        if (data.Length < 8) return null;
        var idLen = (int)ReadBigEndianUInt64(data, 0);
        if (idLen <= 0 || idLen > data.Length - 8) return null;
        var idBytes = new byte[idLen];
        Array.Copy(data, 8, idBytes, 0, idLen);
        return Encoding.ASCII.GetString(idBytes);
    }

    private static uint ReadBigEndianUInt64(byte[] buf, int offset)
        => (uint)(((ulong)buf[offset] << 56) | ((ulong)buf[offset + 1] << 48)
                 | ((ulong)buf[offset + 2] << 40) | ((ulong)buf[offset + 3] << 32)
                 | ((ulong)buf[offset + 4] << 24) | ((ulong)buf[offset + 5] << 16)
                 | ((ulong)buf[offset + 6] << 8) | buf[offset + 7]);

    private static int ReadBigEndianInt32(byte[] buf, int offset)
        => (int)(((uint)buf[offset] << 24) | ((uint)buf[offset + 1] << 16)
                | ((uint)buf[offset + 2] << 8) | buf[offset + 3]);

    private static void ReadExact(Stream stream, byte[] buf, int offset, int count)
    {
        while (count > 0)
        {
            var r = stream.Read(buf, offset, count);
            if (r == 0) throw new EndOfStreamException("Unexpected end of CLIP file");
            offset += r;
            count -= r;
        }
    }
}
