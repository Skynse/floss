using Floss.App.Features.Overview.Histogram;
using SkiaSharp;

namespace Floss.App.Tests;

public class DocumentHistogramComputerTests
{
    static DocumentHistogramComputerTests() => AvaloniaTestBootstrap.EnsureInitialized();

    [Fact]
    public void Compute_SolidRed_PeaksAtBin255()
    {
        using var bitmap = CreateSolid(4, 4, SKColors.Red);
        var hist = DocumentHistogramComputer.Compute(bitmap, 4, 4);

        Assert.Equal(16, hist.TotalSamples);
        Assert.Equal(16, hist.Red.Span[255]);
        Assert.Equal(0, hist.Red.Span[0]);
        Assert.Equal(16, hist.Green.Span[0]);
        Assert.Equal(16, hist.Blue.Span[0]);
        Assert.Equal(16, hist.PeakCount);
    }

    [Fact]
    public void Compute_MidGray_PeaksAtBin128()
    {
        using var bitmap = CreateSolid(2, 2, new SKColor(128, 128, 128, 255));
        var hist = DocumentHistogramComputer.Compute(bitmap, 2, 2);

        Assert.Equal(4, hist.TotalSamples);
        Assert.Equal(4, hist.Red.Span[128]);
        Assert.Equal(4, hist.Green.Span[128]);
        Assert.Equal(4, hist.Blue.Span[128]);
        Assert.Equal(4, hist.Luminance.Span[128]);
    }

    [Fact]
    public void Compute_TransparentPixels_AreSkipped()
    {
        using var bitmap = new SKBitmap(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.Transparent);

        var hist = DocumentHistogramComputer.Compute(bitmap, 2, 2);
        Assert.Equal(0, hist.TotalSamples);
        Assert.Equal(0, hist.PeakCount);
    }

    private static SKBitmap CreateSolid(int width, int height, SKColor color)
    {
        var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(color);
        return bitmap;
    }
}
