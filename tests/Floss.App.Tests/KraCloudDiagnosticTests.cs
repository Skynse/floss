using System.IO;
using System.Linq;
using Floss.App.Canvas.Compositing;
using Floss.App.Document;
using Floss.App.Kra;

namespace Floss.App.Tests;

public class KraCloudDiagnosticTests
{
    private static readonly string KraPath = TestPaths.KraTestFile;

    [Fact]
    public void Load_RealKraArchive_SkyCloudLayersHavePixels()
    {
        if (!File.Exists(KraPath))
            return;

        using var stream = File.OpenRead(KraPath);
        var document = KraImporter.Load(stream);

        foreach (var fragment in new[] { "Cloud Front", "Cloud Back", "Cloud Back Duplicate" })
        {
            var layer = FindLayerByNameContains(document.Layers, fragment);
            Assert.NotNull(layer);
            var region = new PixelRegion(0, 0, document.Width, document.Height);
            Assert.True(layer!.Pixels.HasContentTiles(region), $"{fragment} should have tiles");
        }

        var gradient = FindLayerByNameContains(document.Layers, "Sky Gradient");
        Assert.NotNull(gradient);
        Assert.True(gradient!.IsClipping);

        var skyGroup = document.Layers.First(l => l.IsGroup && l.Name.Contains("Sky |", StringComparison.Ordinal));
        Assert.True(skyGroup.Children.Count >= 7);
        Assert.Contains(skyGroup.Children, c => c.Name.Contains("Cloud Front", StringComparison.Ordinal));
    }

    [Fact]
    public void Load_RealKraArchive_CloudPixelsAppearInComposite()
    {
        if (!File.Exists(KraPath))
            return;

        using var stream = File.OpenRead(KraPath);
        var document = KraImporter.Load(stream);
        var cloud = FindLayerByNameContains(document.Layers, "Cloud Front");
        Assert.NotNull(cloud);

        var skyBand = new PixelRegion(0, 0, document.Width, document.Height / 3);
        Assert.True(TryFindOpaquePixel(cloud!, skyBand, out var sampleX, out var sampleY, out var cb, out var cg, out var cr),
            "cloud layer should contain opaque pixels in the upper sky band");

        using var compositor = new LayerCompositor();
        var skyGroup = document.Layers.First(l => l.IsGroup && l.Name.Contains("Sky |", StringComparison.Ordinal));

        var cloudOnly = compositor.SampleCompositePixel([cloud], document.Width, document.Height, sampleX, sampleY);
        Assert.NotNull(cloudOnly);
        Assert.True(ColorDistance(cloudOnly!.Value, cr, cg, cb) < 16,
            $"cloud-only RGB({cloudOnly.Value.R},{cloudOnly.Value.G},{cloudOnly.Value.B}) should match layer RGB({cr},{cg},{cb})");

        var skyOnly = compositor.SampleCompositePixel([skyGroup], document.Width, document.Height, sampleX, sampleY);
        Assert.NotNull(skyOnly);
        Assert.True(skyOnly!.Value.A > 16);

        var composite = compositor.SampleCompositePixel(document.Layers, document.Width, document.Height, sampleX, sampleY);
        Assert.NotNull(composite);
        Assert.True(composite!.Value.A > 16);
        Assert.True(composite.Value.R + composite.Value.G + composite.Value.B >
                    skyOnly.Value.R + skyOnly.Value.G + skyOnly.Value.B - 60,
            "full document composite should not flatten the sky to a single dark ocean tone");
    }

    private static int ColorDistance(Avalonia.Media.Color a, byte r, byte g, byte b)
        => Math.Abs(a.R - r) + Math.Abs(a.G - g) + Math.Abs(a.B - b);

    private static bool TryFindOpaquePixel(
        DrawingLayer layer,
        PixelRegion documentRegion,
        out int x,
        out int y,
        out byte b,
        out byte g,
        out byte r)
    {
        var bounds = layer.DocumentContentBounds.Intersect(documentRegion);
        for (y = bounds.Y; y < bounds.Bottom; y++)
        {
            for (x = bounds.X; x < bounds.Right; x++)
            {
                layer.Pixels.GetPixel(x, y, out b, out g, out r, out var a);
                if (a > 16)
                    return true;
            }
        }

        x = y = 0;
        b = g = r = 0;
        return false;
    }

    [Fact]
    public void Load_RealKraArchive_CompositeIncludesCloudPixels()
    {
        if (!File.Exists(KraPath))
            return;

        using var stream = File.OpenRead(KraPath);
        var document = KraImporter.Load(stream);

        using var compositor = new LayerCompositor();
        var roots = LayerStackComposition.GetRootLayers(document.Layers);
        var stack = LayerProjectionPlane.BuildSiblingStack(roots);

        var sampleX = document.Width / 2;
        var sampleY = document.Height / 4;
        var color = compositor.SampleCompositePixel(document.Layers, document.Width, document.Height, sampleX, sampleY);
        Assert.NotNull(color);
        Assert.True(color!.Value.A > 0);
    }

    private static DrawingLayer? FindLayerByNameContains(IReadOnlyList<DrawingLayer> layers, string fragment)
    {
        foreach (var layer in layers)
        {
            if (layer.Name.Contains(fragment, StringComparison.Ordinal))
                return layer;
        }

        return null;
    }
}
