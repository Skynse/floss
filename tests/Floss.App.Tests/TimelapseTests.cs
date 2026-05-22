namespace Floss.App.Tests;

public class TimelapseTests
{
    [Fact]
    public void SelectFrames_SamplesRequestedDuration()
    {
        var frames = Enumerable.Range(0, 500)
            .Select(i => $"frame_{i:D6}.png")
            .ToArray();

        var selected = TimelapseSession.SelectFrames(frames, TimelapseLength.Seconds15);

        TestAssertions.Equal(180, selected.Count);
        TestAssertions.Equal("frame_000000.png", selected[0]);
        TestAssertions.Equal("frame_000499.png", selected[^1]);
    }

    [Fact]
    public void ComposeFrame_RespectsAspectModes()
    {
        using var source = new SKBitmap(new SKImageInfo(200, 100, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        source.Erase(SKColors.Red);

        using var landscape = TimelapseSession.ComposeFrame(source, new TimelapseExportSettings
        {
            Aspect = TimelapseAspect.Landscape,
            LongestSidePixels = 160
        });
        using var portrait = TimelapseSession.ComposeFrame(source, new TimelapseExportSettings
        {
            Aspect = TimelapseAspect.Portrait,
            LongestSidePixels = 160
        });

        TestAssertions.Equal(160, landscape.Width);
        TestAssertions.Equal(90, landscape.Height);
        TestAssertions.Equal(90, portrait.Width);
        TestAssertions.Equal(160, portrait.Height);
    }

    [Fact]
    public void ComputeCaptureDimensions_DownscalesLargeDocuments()
    {
        var (width, height) = TimelapseSession.ComputeCaptureDimensions(8000, 6000);
        TestAssertions.Equal(4096, width);
        TestAssertions.Equal(3072, height);

        var (smallW, smallH) = TimelapseSession.ComputeCaptureDimensions(1920, 1080);
        TestAssertions.Equal(1920, smallW);
        TestAssertions.Equal(1080, smallH);
    }

    [Fact]
    public void ChooseAssembleLod_StaysWithinPixelBudget()
    {
        TestAssertions.Equal(0, TimelapseSession.ChooseAssembleLod(4096, 4096));
        TestAssertions.Equal(1, TimelapseSession.ChooseAssembleLod(12000, 9000));
    }
}

