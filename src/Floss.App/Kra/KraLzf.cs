using System;

namespace Floss.App.Kra;

/// <summary>
/// 's LZF variant used for KRA tile payloads (see kis_lzf_compression.cpp).
/// </summary>
internal static class KraLzf
{
    public static int Decompress(ReadOnlySpan<byte> input, Span<byte> output)
    {
        var ip = 0;
        var op = 0;
        var ipLimit = input.Length - 1;

        while (ip < ipLimit)
        {
            var ctrl = input[ip] + 1;
            var ofs = (input[ip] & 31) << 8;
            var len = input[ip] >> 5;
            ip++;

            if (ctrl < 33)
            {
                if (op + ctrl > output.Length)
                    return 0;

                while (ctrl > 0)
                {
                    output[op++] = input[ip++];
                    ctrl--;
                }
            }
            else
            {
                len--;
                var refPos = op - ofs - 1;

                if (len == 7 - 1)
                    len += input[ip++];

                refPos -= input[ip++];

                if (op + len + 3 > output.Length)
                    return 0;
                if (refPos < 0)
                    return 0;

                output[op++] = output[refPos++];
                output[op++] = output[refPos++];
                output[op++] = output[refPos++];
                while (len > 0)
                {
                    output[op++] = output[refPos++];
                    len--;
                }
            }
        }

        return op;
    }

    /// <summary>
    /// Converts planar channel data (BBBB..GGGG..RRRR..AAAA) to interleaved BGRA.
    /// </summary>
    public static void DelinearizeColors(ReadOnlySpan<byte> input, Span<byte> output, int pixelSize)
    {
        var dataSize = output.Length;
        var strideSize = dataSize / pixelSize;
        var startByte = 0;

        for (var outIndex = 0; outIndex < dataSize;)
        {
            var inputByte = startByte;
            for (var channel = 0; channel < pixelSize; channel++)
            {
                output[outIndex++] = input[inputByte];
                inputByte += strideSize;
            }

            startByte++;
        }
    }
}
