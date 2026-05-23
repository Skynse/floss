using System;
using System.IO;
using System.Text;
using Floss.App.Document;

namespace Floss.App.Kra;

internal static class KraLayerDataReader
{
    private const int DefaultTileSize = 64;
    private const int DefaultPixelSize = 4;
    private const byte CompressedDataFlag = 1;

    public static bool TryReadIntoLayer(Stream stream, TiledPixelBuffer pixels, int offsetX, int offsetY)
    {
        _ = offsetX;
        _ = offsetY;

        if (!TryReadHeader(stream, out var tilesVersion, out var tileWidth, out var tileHeight, out var pixelSize, out var numTiles))
            return false;

        if (pixelSize != DefaultPixelSize)
            return false;

        var tileDataSize = tileWidth * tileHeight * pixelSize;
        var payload = new byte[tileDataSize + 1];
        var tileBuffer = new byte[tileDataSize];
        var linearBuffer = new byte[tileDataSize];
        var tilesCopied = 0;

        for (uint i = 0; i < numTiles; i++)
        {
            if (!TryReadTile(stream, tilesVersion, tileWidth, tileHeight, pixelSize, tileDataSize, payload, linearBuffer, tileBuffer, out var tileLeft, out var tileTop))
                continue;

            var destX = tileLeft;
            var destY = tileTop;
            if (tileWidth == DefaultTileSize && tileHeight == DefaultTileSize
                && destX % DefaultTileSize == 0 && destY % DefaultTileSize == 0)
            {
                pixels.ImportTile(FloorDiv(destX, DefaultTileSize), FloorDiv(destY, DefaultTileSize), tileBuffer);
            }
            else
            {
                pixels.Restore(new PixelRegion(destX, destY, tileWidth, tileHeight), tileBuffer);
            }
            tilesCopied++;
        }

        return tilesCopied > 0;
    }

    private static int FloorDiv(int value, int divisor)
    {
        var result = value / divisor;
        if ((value ^ divisor) < 0 && value % divisor != 0) result--;
        return result;
    }

    private static bool TryReadHeader(
        Stream stream,
        out int tilesVersion,
        out int tileWidth,
        out int tileHeight,
        out int pixelSize,
        out uint numTiles)
    {
        tilesVersion = 1;
        tileWidth = DefaultTileSize;
        tileHeight = DefaultTileSize;
        pixelSize = DefaultPixelSize;
        numTiles = 0;

        var firstLine = ReadLine(stream);
        if (firstLine.Length == 0)
            return false;

        if (firstLine.StartsWith("VERSION ", StringComparison.Ordinal))
        {
            tilesVersion = int.Parse(firstLine["VERSION ".Length..].Trim());
            if (!TryReadKeywordInt(stream, "TILEWIDTH", out tileWidth)) return false;
            if (!TryReadKeywordInt(stream, "TILEHEIGHT", out tileHeight)) return false;
            if (!TryReadKeywordInt(stream, "PIXELSIZE", out pixelSize)) return false;
            if (!TryReadKeywordInt(stream, "DATA", out var dataCount)) return false;
            numTiles = (uint)dataCount;
            return true;
        }

        numTiles = uint.Parse(firstLine);
        return true;
    }

    private static bool TryReadTile(
        Stream stream,
        int tilesVersion,
        int tileWidth,
        int tileHeight,
        int pixelSize,
        int tileDataSize,
        byte[] payload,
        byte[] linearBuffer,
        byte[] tileBuffer,
        out int tileLeft,
        out int tileTop)
    {
        tileLeft = 0;
        tileTop = 0;

        if (tilesVersion == 1)
        {
            var header = ReadLine(stream);
            if (header.Length == 0)
                return false;

            var parts = header.Split(',');
            if (parts.Length < 4)
                return false;

            tileLeft = int.Parse(parts[0]);
            tileTop = int.Parse(parts[1]);
            _ = int.Parse(parts[2]);
            _ = int.Parse(parts[3]);

            if (ReadExact(stream, tileBuffer, tileDataSize) != tileDataSize)
                return false;

            return true;
        }

        var tileHeader = ReadLine(stream);
        if (tileHeader.Length == 0)
            return false;

        var tileParts = tileHeader.Split(',');
        if (tileParts.Length < 4)
            return false;

        tileLeft = int.Parse(tileParts[0]);
        tileTop = int.Parse(tileParts[1]);
        var compressionName = tileParts[2];
        var dataSize = int.Parse(tileParts[3]);
        if (!string.Equals(compressionName, "LZF", StringComparison.OrdinalIgnoreCase))
            return false;

        if (dataSize <= 0 || dataSize > payload.Length)
            return false;

        if (ReadExact(stream, payload, dataSize) != dataSize)
            return false;

        if (payload[0] == CompressedDataFlag)
        {
            var written = KraLzf.Decompress(payload.AsSpan(1, dataSize - 1), linearBuffer);
            if (written != tileDataSize)
                return false;

            KraLzf.DelinearizeColors(linearBuffer, tileBuffer, pixelSize);
        }
        else
        {
            Buffer.BlockCopy(payload, 1, tileBuffer, 0, Math.Min(tileDataSize, dataSize - 1));
        }

        return true;
    }

    private static bool TryReadKeywordInt(Stream stream, string keyword, out int value)
    {
        value = 0;
        var line = ReadLine(stream);
        if (line.Length == 0)
            return false;

        var space = line.IndexOf(' ');
        if (space <= 0)
            return false;

        if (!string.Equals(line[..space], keyword, StringComparison.Ordinal))
            return false;

        value = int.Parse(line[(space + 1)..].Trim());
        return true;
    }

    private static string ReadLine(Stream stream)
    {
        var builder = new StringBuilder(64);
        while (true)
        {
            var b = stream.ReadByte();
            if (b < 0)
                break;
            if (b == '\n')
                break;
            if (b != '\r')
                builder.Append((char)b);
        }

        return builder.ToString().Trim();
    }

    private static int ReadExact(Stream stream, byte[] buffer, int count)
    {
        var readTotal = 0;
        while (readTotal < count)
        {
            var read = stream.Read(buffer, readTotal, count - readTotal);
            if (read <= 0)
                break;
            readTotal += read;
        }

        return readTotal;
    }
}
