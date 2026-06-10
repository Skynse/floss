using Avalonia;
using Avalonia.Headless;
using Floss.App.Canvas;
using Floss.App.Features;
using Floss.App.Features.Actions;

namespace Floss.App.Tests;

public class FeatureServicesTests
{
    private static void EnsureAvalonia() => AvaloniaTestBootstrap.EnsureInitialized();

    [Fact]
    public void Get_ThrowsWhenServiceMissing()
    {
        var services = new FeatureServices();
        Assert.Throws<InvalidOperationException>(() => services.Get<IDocumentHistorySource>());
    }

    [Fact]
    public void TryGet_ReturnsNullWhenServiceMissing()
    {
        var services = new FeatureServices();
        Assert.Null(services.TryGet<IDocumentHistorySource>());
    }

    [Fact]
    public void NotifyActiveCanvas_RebindsCanvasBoundServices()
    {
        EnsureAvalonia();

        using var canvasA = new DrawingCanvas();
        using var canvasB = new DrawingCanvas();
        canvasB.AddLayer();

        var services = FeatureSessionBootstrap.Create(canvasA);
        var history = services.Get<IDocumentHistorySource>();
        Assert.NotNull(services.TryGet<IActionRegistry>());
        Assert.NotNull(services.TryGet<IDocumentEvents>());
        Assert.False(history.HasDocument);

        services.NotifyActiveCanvas(canvasB);
        Assert.True(history.HasDocument);
    }

    [Fact]
    public void NotifyActiveCanvas_EmptyCanvas_ClearsHistoryBinding()
    {
        EnsureAvalonia();

        using var canvasA = new DrawingCanvas();
        canvasA.AddLayer();
        using var canvasB = new DrawingCanvas();

        var services = FeatureSessionBootstrap.Create(canvasA);
        Assert.True(services.Get<IDocumentHistorySource>().HasDocument);

        services.NotifyActiveCanvas(canvasB);
        Assert.False(services.Get<IDocumentHistorySource>().HasDocument);
    }
}
