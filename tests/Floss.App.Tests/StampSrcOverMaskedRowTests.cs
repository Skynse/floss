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

    [Fact]
    public void MaskedRow_AlphaLocked_SkipsFullyTransparentDestination()
    {
        Span<byte> tile = stackalloc byte[16]; // all transparent
        Span<byte> mask = stackalloc byte[4] { 255, 255, 0, 0 };

        fixed (byte* dst = tile)
        fixed (byte* maskPtr = mask)
        {
            SimdPixelOps.StampSrcOverMaskedRow(dst, maskPtr, 2, srcB: 0, srcG: 0, srcR: 255, opacity: 255, alphaLocked: true);
        }

        Assert.Equal(0, tile[0]);
        Assert.Equal(0, tile[1]);
        Assert.Equal(0, tile[2]);
        Assert.Equal(0, tile[3]);
        Assert.Equal(0, tile[4]);
        Assert.Equal(0, tile[7]);
    }

    [Fact]
    public void MaskedRow_AlphaLocked_BlendsRgbPreservesAlphaOnOpaque()
    {
        Span<byte> tile = stackalloc byte[4];
        tile[0] = 0; tile[1] = 0; tile[2] = 0; tile[3] = 200;
        Span<byte> mask = stackalloc byte[1] { 255 };

        fixed (byte* dst = tile)
        fixed (byte* maskPtr = mask)
        {
            SimdPixelOps.StampSrcOverMaskedRow(dst, maskPtr, 1, srcB: 0, srcG: 0, srcR: 255, opacity: 128, alphaLocked: true);
        }

        Assert.Equal(200, tile[3]);
        Assert.True(tile[2] > 0);
    }

    [Fact]
    public void StampSrcOver_AlphaLocked_SkipsTransparentPixel()
    {
        Span<byte> pixel = stackalloc byte[4];
        fixed (byte* p = pixel)
        {
            SimdPixelOps.StampSrcOver(p, 10, 20, 30, stampA: 255, alphaLocked: true);
        }

        Assert.Equal(0, pixel[0]);
        Assert.Equal(0, pixel[1]);
        Assert.Equal(0, pixel[2]);
        Assert.Equal(0, pixel[3]);
    }
}
