namespace Floss.App.Tests;

public class LayerCompositorTests
{
    [Fact]
    public void MonochromeExpression_ThresholdsCoverageBeforePaperComposite()
    {
        using var layer = new DrawingLayer("Ink", 4, 1)
        {
            ExpressionColor = ExpressionColorMode.Monochrome
        };

        layer.Pixels.SetPixel(0, 0, 0, 0, 0, 127);
        layer.Pixels.SetPixel(1, 0, 0, 0, 0, 128);
        layer.Pixels.SetPixel(2, 0, 200, 200, 200, 255);

        using var compositor = new LayerCompositor();
        var pixels = compositor.CompositeToBgra([layer], 4, 1, 0xFFFFFFFF);

        AssertPixel(pixels, 0, 255, 255, 255, 255);
        AssertPixel(pixels, 1, 0, 0, 0, 255);
        AssertPixel(pixels, 2, 255, 255, 255, 255);
    }

    [Fact]
    public void SampleCompositePixel_UsesFinalCompositorResult()
    {
        using var layer = new DrawingLayer("Multiply red", 1, 1)
        {
            BlendMode = "Multiply"
        };
        layer.Pixels.SetPixel(0, 0, b: 0, g: 0, r: 255, a: 255);

        using var compositor = new LayerCompositor();
        var sampled = compositor.SampleCompositePixel([layer], 1, 1, 0, 0, paperColor: 0xFF0000FF);

        TestAssertions.True(sampled.HasValue, "Sampling image mode should return the final composited pixel.");
        TestAssertions.Equal((byte)0, sampled!.Value.R);
        TestAssertions.Equal((byte)0, sampled.Value.G);
        TestAssertions.Equal((byte)0, sampled.Value.B);
        TestAssertions.Equal((byte)255, sampled.Value.A);
    }

    [Fact]
    public void Composite_BudgetsDirtyTiles()
    {
        var dirtyTileCount = LayerCompositor.CountTilesForRegion(new PixelRegion(0, 0, 4096, 4096), lod: 0);

        TestAssertions.True(dirtyTileCount > LayerCompositor.DirtyTileBudget);
        TestAssertions.Equal(32, LayerCompositor.DirtyTileBudget);
    }

    [Fact]
    public void Composite_SelectsLodForHugeLowZoomCanvas()
    {
        using var compositor = new LayerCompositor();
        TestAssertions.Equal(2, compositor.SelectLod(6000, 4080, 0.1));
        TestAssertions.Equal(1, compositor.SelectLod(6000, 4080, 0.3));
        TestAssertions.Equal(0, compositor.SelectLod(6000, 4080, 1.0));
    }

    [Fact]
    public void Composite_InvalidatesAllLodsOnPartialDirty()
    {
        using var compositor = new LayerCompositor();
        var region = new PixelRegion(512, 512, 128, 128);
        compositor.Invalidate(region);
        var lod0 = LayerCompositor.CountTilesForRegion(region, lod: 0);
        var lod1 = LayerCompositor.CountTilesForRegion(region, lod: 1);
        var lod2 = LayerCompositor.CountTilesForRegion(region, lod: 2);
        TestAssertions.True(lod0 > 0);
        TestAssertions.True(lod1 > 0);
        TestAssertions.True(lod2 > 0);
        TestAssertions.True(compositor.PendingDirtyTileCount >= lod0 + lod1 + lod2);
    }

    private static void AssertPixel(byte[] pixels, int x, byte b, byte g, byte r, byte a)
    {
        var offset = x * 4;
        TestAssertions.SequenceEqual(new[] { b, g, r, a },
            new[] { pixels[offset], pixels[offset + 1], pixels[offset + 2], pixels[offset + 3] });
    }
}

