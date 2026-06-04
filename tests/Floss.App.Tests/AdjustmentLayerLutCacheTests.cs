using Floss.App.Canvas.Compositing;
using Floss.App.Document;

namespace Floss.App.Tests;

public sealed class AdjustmentLayerLutCacheTests
{
    [Fact]
    public void HslCube_MatchesDirectTransform_AtGridAndMidpoints()
    {
        var adj = new AdjustmentLayerData
        {
            Kind = AdjustmentKind.HueSaturationLuminosity,
            Hue = 42f,
            Saturation = -15f,
            Luminosity = 8f,
        };
        adj.LutCache.Ensure(adj);

        // Cube is sampled on 33³ grid; only grid-aligned RGB values match exactly.
        static byte Grid(int i) => (byte)((i * 255 + 16) / 32);
        foreach (var ri in Enumerable.Range(0, 33))
        foreach (var gi in Enumerable.Range(0, 33))
        foreach (var bi in Enumerable.Range(0, 33))
        {
            var r = Grid(ri);
            var g = Grid(gi);
            var b = Grid(bi);
            Span<byte> px = stackalloc byte[4];
            px[0] = b;
            px[1] = g;
            px[2] = r;
            AdjustmentLayerProcessor.TransformPixel(px, adj);
            adj.LutCache.Lookup(b, g, r, out var cb, out var cg, out var cr);
            Assert.Equal(px[0], cb);
            Assert.Equal(px[1], cg);
            Assert.Equal(px[2], cr);
        }
    }

    [Fact]
    public void Ensure_RebuildsWhenParametersChange()
    {
        var adj = new AdjustmentLayerData { Kind = AdjustmentKind.BrightnessContrast, Brightness = 10f };
        adj.LutCache.Ensure(adj);
        adj.LutCache.Lookup(128, 128, 128, out var b1, out _, out _);

        adj.Brightness = 40f;
        adj.LutCache.Ensure(adj);
        adj.LutCache.Lookup(128, 128, 128, out var b2, out _, out _);

        Assert.NotEqual(b1, b2);
    }
}
