using System;

namespace Floss.App.Canvas;

/// <summary>
/// Managed fallback for <see cref="Native.FlossCompositeNative"/> when the Rust
/// cdylib is not on the load path.
/// </summary>
internal static unsafe class CompositeNormalRowManaged
{
    public static void Composite(byte* dst, byte* src, int width, uint opacity)
    {
        var fullOpacity = opacity == 255;
        for (var i = 0; i < width; i++)
        {
            var off = i * 4;
            uint rawA = src[off + 3];
            if (rawA == 0) continue;

            uint srcA = fullOpacity ? rawA : (rawA * opacity + 127) / 255;
            var dstPtr = dst + off;

            if (srcA == 255)
            {
                dstPtr[0] = src[off];
                dstPtr[1] = src[off + 1];
                dstPtr[2] = src[off + 2];
                dstPtr[3] = 255;
                continue;
            }

            uint invSrcA = 255 - srcA;
            uint dstA = dstPtr[3];
            uint dstCont = (dstA * invSrcA + 127) / 255;
            uint outA = srcA + dstCont;
            if (outA == 0) continue;

            uint half = outA >> 1;
            dstPtr[0] = (byte)((src[off] * srcA + dstPtr[0] * dstCont + half) / outA);
            dstPtr[1] = (byte)((src[off + 1] * srcA + dstPtr[1] * dstCont + half) / outA);
            dstPtr[2] = (byte)((src[off + 2] * srcA + dstPtr[2] * dstCont + half) / outA);
            dstPtr[3] = (byte)outA;
        }
    }

    public static void CompositeRegion(
        byte* dst, int dstStride, byte* src, int srcStride, int width, int height, uint opacity)
    {
        for (var y = 0; y < height; y++)
        {
            var dstRow = dst + y * dstStride;
            var srcRow = src + y * srcStride;
            Composite(dstRow, srcRow, width, opacity);
        }
    }

    public static void ClearRegion(byte* dst, int dstStride, int width, int height, uint clearValue)
    {
        for (var y = 0; y < height; y++)
        {
            var row = (uint*)(dst + y * dstStride);
            for (var x = 0; x < width; x++)
                row[x] = clearValue;
        }
    }
}
