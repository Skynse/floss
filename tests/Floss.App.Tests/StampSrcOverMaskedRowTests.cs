using Floss.App.Canvas.Engine;

namespace Floss.App.Tests;

public unsafe class StampSrcOverMaskedRowTests
{
    [Fact]
    public void MaskedRow_SoftEdge_WritesFullBlueRgbNotZeroAlpha()
    {
        Span<byte> tile = stackalloc byte[16];
        Span<byte> mask = stackalloc byte[4] { 64, 128, 192, 255 };

        fixed (byte* dst = tile)
        fixed (byte* maskPtr = mask)
        {
            SimdPixelOps.StampSrcOverMaskedRow(dst, maskPtr, 4, srcB: 255, srcG: 0, srcR: 0, opacity: 255, alphaLocked: false);
        }

        for (var i = 0; i < 4; i++)
        {
            var off = i * 4;
            Assert.True(tile[off + 3] > 0, $"pixel {i} should have alpha");
            Assert.Equal(255, tile[off + 0]); // full blue channel on first stamp
            Assert.Equal(0, tile[off + 1]);
            Assert.Equal(0, tile[off + 2]);
        }
    }

    [Fact]
    public void MaskedRow_OverSemiTransparentDst_KeepsAssociatedAlphaColor()
    {
        Span<byte> tile = stackalloc byte[16];
        tile[0] = 255;
        tile[3] = 128;
        Span<byte> mask = stackalloc byte[4] { 128, 0, 0, 0 };

        fixed (byte* dst = tile)
        fixed (byte* maskPtr = mask)
        {
            SimdPixelOps.StampSrcOverMaskedRow(dst, maskPtr, 1, srcB: 255, srcG: 0, srcR: 0, opacity: 255, alphaLocked: false);
        }

        Assert.True(tile[3] > 128);
        Assert.Equal(255, tile[0]);
    }
}
