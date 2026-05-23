using System.Text.Json;

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
    public void FindForDocument_PrefersDocumentPathMatch()
    {
        var document = new DrawingDocument(800, 600);
        var session = TimelapseSession.StartNew("Sketch", document);
        session.SetRecording(false);
        var documentPath = Path.Combine(Path.GetTempPath(), $"sketch-{Guid.NewGuid():N}.floss");
        session.BindDocumentPath(documentPath);

        var otherDocument = new DrawingDocument(800, 600);
        var otherSession = TimelapseSession.StartNew("Sketch", otherDocument);
        otherSession.SetRecording(false);

        try
        {
            var restored = TimelapseSession.FindForDocument(
                documentPath,
                "Sketch",
                800,
                600);

            TestAssertions.Equal(session.SessionId, restored?.SessionId);
        }
        finally
        {
            try { Directory.Delete(session.DirectoryPath, recursive: true); } catch { }
            try { Directory.Delete(otherSession.DirectoryPath, recursive: true); } catch { }
        }
    }

    [Fact]
    public void TryLoad_PreservesExistingFramesForResume()
    {
        var document = new DrawingDocument(640, 480);
        document.AddLayer();
        var session = TimelapseSession.StartNew("ResumeTest", document);
        session.SetRecording(false);

        try
        {
            var directory = session.DirectoryPath;
            var framePath = Path.Combine(directory, "frame_000000.jpg");
            File.WriteAllBytes(framePath, CreateSolidJpegBytes(640, 480));

            var manifestPath = Path.Combine(directory, "manifest.json");
            var manifest = JsonSerializer.Deserialize<TimelapseManifest>(File.ReadAllText(manifestPath))!;
            manifest.Frames =
            [
                new TimelapseFrame
                {
                    Index = 0,
                    CreatedUtc = DateTimeOffset.UtcNow,
                    FileName = "frame_000000.jpg"
                }
            ];
            File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

            var restored = TimelapseSession.TryLoad(directory);
            TestAssertions.Equal(1, restored?.FrameCount);
            TestAssertions.Equal("frame_000000.jpg", restored!.Manifest.Frames[0].FileName);
        }
        finally
        {
            try { Directory.Delete(session.DirectoryPath, recursive: true); } catch { }
        }
    }

    private static byte[] CreateSolidJpegBytes(int width, int height)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.White);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 92)
            ?? throw new InvalidOperationException("Failed to encode JPEG.");
        return data.ToArray();
    }
}

