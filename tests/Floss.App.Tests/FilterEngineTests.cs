using Floss.App.Document;
using Floss.App.Filters;

namespace Floss.App.Tests;

public class FilterEngineTests
{
    [Fact]
    public void ApplyExposureGamma_RestoreRoundTrip_PreservesPixels()
    {
        var layer = new DrawingLayer("Ink", 128, 128);
        layer.Pixels.SetPixel(40, 40, 40, 80, 120, 200);

        var region = layer.Pixels.Bounds;
        var before = layer.CapturePixels(region);

        FilterEngine.ApplyExposureGamma(layer, exposureStops: 1.5f, gamma: 1.2f);
        layer.Pixels.GetPixel(40, 40, out var previewB, out var previewG, out var previewR, out _);
        TestAssertions.True(previewB != 40 || previewG != 80 || previewR != 120, "Preview should change pixel values.");

        layer.RestorePixels(region, before);
        layer.Pixels.GetPixel(40, 40, out var b, out var g, out var r, out var a);
        TestAssertions.Equal((byte)40, b);
        TestAssertions.Equal((byte)80, g);
        TestAssertions.Equal((byte)120, r);
        TestAssertions.Equal((byte)200, a);
    }
}
