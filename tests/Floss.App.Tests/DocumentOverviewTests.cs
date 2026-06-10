using Avalonia;
using Avalonia.Headless;
using Floss.App.Canvas;
using Floss.App.Features.Overview;

namespace Floss.App.Tests;

public class DocumentOverviewTests
{
    private static void EnsureAvalonia() => AvaloniaTestBootstrap.EnsureInitialized();

    [Theory]
    [InlineData(1920, 1080, 200, 150, 200, 112)]
    [InlineData(500, 500, 200, 200, 200, 200)]
    [InlineData(100, 200, 400, 400, 100, 200)]
    [InlineData(4000, 2000, 256, 256, 256, 128)]
    public void ComputeFit_PreservesAspectWithoutUpscaling(
        int docW, int docH, int maxW, int maxH, int expectedW, int expectedH)
    {
        var (w, h) = DocumentOverviewCompositor.ComputeFit(docW, docH, maxW, maxH);
        Assert.Equal(expectedW, w);
        Assert.Equal(expectedH, h);
        Assert.True(w <= maxW);
        Assert.True(h <= maxH);
        Assert.True(w <= docW);
        Assert.True(h <= docH);
    }

    [Fact]
    public void CacheLongEdge_IsSmallEnoughForNavigator()
    {
        Assert.True(DocumentOverviewCompositor.CacheLongEdge <= 512);
    }

    [Fact]
    public void RequestSnapshot_AfterCancelPending_DoesNotThrow()
    {
        EnsureAvalonia();

        using var canvas = new DrawingCanvas();
        canvas.AddLayer();

        using var cache = new DocumentOverviewCache(canvas);
        using var overview = new DocumentOverviewSource(cache);
        overview.RequestSnapshot(64, 64);
        overview.CancelPending();

        var ex = Record.Exception(() => overview.RequestSnapshot(64, 64));
        Assert.Null(ex);
    }

    [Fact]
    public void BindCanvas_SwitchesActiveCanvas()
    {
        EnsureAvalonia();

        using var canvasA = new DrawingCanvas();
        using var canvasB = new DrawingCanvas();
        canvasB.AddLayer();

        using var cache = new DocumentOverviewCache(canvasA);
        using var overview = new DocumentOverviewSource(cache);
        overview.BindCanvas(canvasB);
        var ex = Record.Exception(() => overview.RequestSnapshot(96, 96));
        Assert.Null(ex);

        overview.BindCanvas(canvasB);
        Assert.Null(Record.Exception(() => overview.RequestSnapshot(96, 96)));
    }

    [Fact]
    public void BindCanvas_ClearsSnapshotBeforeRebuild()
    {
        EnsureAvalonia();

        using var canvasA = new DrawingCanvas();
        canvasA.AddLayer();
        using var canvasB = new DrawingCanvas();
        canvasB.AddLayer();

        using var cache = new DocumentOverviewCache(canvasA);
        using var overview = new DocumentOverviewSource(cache);
        DocumentOverviewSnapshot? cleared = null;
        overview.SnapshotReady += s => cleared = s;

        overview.BindCanvas(canvasB);
        Assert.Null(cleared);
    }

    [Fact]
    public void BindCanvas_ToEmptyCanvas_ClearsOverview()
    {
        EnsureAvalonia();

        using var canvasA = new DrawingCanvas();
        canvasA.AddLayer();
        using var canvasB = new DrawingCanvas();

        using var cache = new DocumentOverviewCache(canvasA);
        using var overview = new DocumentOverviewSource(cache);
        DocumentOverviewSnapshot? last = null;
        overview.SnapshotReady += s => last = s;

        overview.BindCanvas(canvasB);
        Assert.Null(last);
    }
}

