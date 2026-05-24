using Floss.App.Document;
using SkiaSharp;

namespace Floss.App.Tests;

public class AlphaLockPixelOpsTests
{
    [Fact]
    public void CompositeBrushPixel_SrcOver_AlphaLocked_PreservesAlphaAndSkipsTransparent()
    {
        byte b = 100, g = 50, r = 200, a = 128;
        AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a, 255, 0, 0, 200, alphaLocked: true, SKBlendMode.SrcOver);
        Assert.Equal((byte)128, a);
        Assert.NotEqual((byte)100, b);
    }

    [Fact]
    public void CompositeBrushPixel_SrcOver_AlphaLocked_DoesNotPaintOnClearPixels()
    {
        byte b = 0, g = 0, r = 0, a = 0;
        AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a, 255, 0, 0, 255, alphaLocked: true, SKBlendMode.SrcOver);
        Assert.Equal((byte)0, a);
        Assert.Equal((byte)0, b);
    }

    [Fact]
    public void CompositeBrushPixel_SrcOver_Unlocked_ExpandsAlpha()
    {
        byte b = 0, g = 0, r = 0, a = 0;
        AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a, 255, 0, 0, 200, alphaLocked: false, SKBlendMode.SrcOver);
        Assert.True(a > 0);
    }

    [Fact]
    public void CompositeBrushPixel_DstOut_AlphaLocked_ReducesAlphaOnExistingPixels()
    {
        byte b = 100, g = 50, r = 200, a = 200;
        AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a, 255, 255, 255, 128, alphaLocked: true, SKBlendMode.DstOut);
        Assert.True(a < 200);
        Assert.True(a > 0);
    }

    [Fact]
    public void CompositeBrushPixel_DstOut_AlphaLocked_SkipsClearPixels()
    {
        byte b = 0, g = 0, r = 0, a = 0;
        AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a, 255, 255, 255, 255, alphaLocked: true, SKBlendMode.DstOut);
        Assert.Equal((byte)0, a);
    }

    [Fact]
    public void CompositeBrushPixel_Multiply_AlphaLocked_PreservesAlpha()
    {
        byte b = 100, g = 100, r = 100, a = 180;
        AlphaLockPixelOps.CompositeBrushPixel(ref b, ref g, ref r, ref a, 128, 128, 128, 255, alphaLocked: true, SKBlendMode.Multiply);
        Assert.Equal((byte)180, a);
        Assert.True(b < 100);
    }

    [Fact]
    public void BlendSrcOverColor_AlphaLocked_KeepsAlpha()
    {
        byte b = 10, g = 20, r = 30, a = 200;
        AlphaLockPixelOps.BlendSrcOverColor(ref b, ref g, ref r, ref a, 255, 128, 64, 128, alphaLocked: true);
        Assert.Equal((byte)200, a);
    }
}
