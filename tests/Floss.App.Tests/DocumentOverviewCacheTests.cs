using Floss.App.Canvas;
using Floss.App.Features.Overview;
using Floss.App.Features.Overview.Histogram;

namespace Floss.App.Tests;

public class DocumentOverviewCacheTests
{
    private static void EnsureAvalonia() => AvaloniaTestBootstrap.EnsureInitialized();

    [Fact]
    public void SharedCache_BothConsumers_RequestWithoutThrow()
    {
        EnsureAvalonia();

        using var canvas = new DrawingCanvas();
        canvas.AddLayer();

        using var cache = new DocumentOverviewCache(canvas);
        using var overview = new DocumentOverviewSource(cache);
        using var histogram = new DocumentHistogramSource(cache);

        overview.RequestSnapshot(64, 64);
        var ex = Record.Exception(() => histogram.RequestUpdate());
        Assert.Null(ex);
    }

    [Fact]
    public void SharedCache_TryGet_MissBeforeRebuild()
    {
        using var canvas = new DrawingCanvas();
        canvas.AddLayer();

        using var cache = new DocumentOverviewCache(canvas);
        Assert.False(cache.TryGet(canvas.Document, out _, out _));
    }
}
