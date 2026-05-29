using Floss.App.Canvas.Engine;

namespace Floss.App.Canvas.Compositing;

internal static unsafe class CompositeNormalRowManaged
{
    public static void Composite(byte* dst, byte* src, int width, uint opacity)
        => SimdPixelOps.SrcOverRow(dst, src, width, opacity);

    public static void CompositeRegion(
        byte* dst, int dstStride, byte* src, int srcStride, int width, int height, uint opacity)
        => SimdPixelOps.SrcOverRegion(dst, dstStride, src, srcStride, width, height, opacity);

    /// <summary>
    /// Drawpile DP_blend_mode_clip: blend src colors into dst, preserve dst alpha.
    /// </summary>
    public static void CompositeAlphaPreserving(byte* dst, byte* src, int width, uint opacity)
        => SimdPixelOps.SrcOverColorOnlyRow(dst, src, width, opacity);

    public static void ClearRegion(byte* dst, int dstStride, int width, int height, uint clearValue)
        => SimdPixelOps.ClearRegion(dst, dstStride, width, height, clearValue);
}
