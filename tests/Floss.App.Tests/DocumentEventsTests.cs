using Avalonia;
using Avalonia.Headless;
using Floss.App.Canvas;
using Floss.App.Features;

namespace Floss.App.Tests;

public class DocumentEventsTests
{
    private static void EnsureAvalonia() => AvaloniaTestBootstrap.EnsureInitialized();

    private sealed class TestViewHost : ICanvasViewHost
    {
        public bool HasDocument { get; set; }
        public int DocumentWidth { get; set; }
        public int DocumentHeight { get; set; }
        public double Zoom { get; set; }
        public double PanOffsetX { get; set; }
        public double PanOffsetY { get; set; }
        public double Rotation { get; set; }
        public int FlipX { get; set; } = 1;
        public int FlipY { get; set; } = 1;
        public double ViewportWidth { get; set; }
        public double ViewportHeight { get; set; }
        public Document.PixelRegion? VisibleDocumentRegion => null;

        public event Action? ViewTransformChanged;
        public event Action? DocumentVisualChanged;

        public void PanBy(double dx, double dy) { }
        public void ZoomBy(double factor, Point viewportCenter) { }
        public void ResetView() { }

        public void RaiseViewportChanged() => ViewTransformChanged?.Invoke();
    }

    [Fact]
    public void StructureChanged_FiresOnAddLayer()
    {
        EnsureAvalonia();

        using var canvas = new DrawingCanvas();
        var events = new DocumentEventsSource(canvas);
        var count = 0;
        events.StructureChanged += () => count++;

        canvas.AddLayer();
        Assert.Equal(1, count);
    }

    [Fact]
    public void HistoryChanged_FiresOnUndo()
    {
        EnsureAvalonia();

        using var canvas = new DrawingCanvas();
        var events = new DocumentEventsSource(canvas);
        var count = 0;
        events.HistoryChanged += () => count++;

        canvas.AddLayer();
        var afterAdd = count;
        canvas.Undo();
        Assert.True(count > afterAdd);
    }

    [Fact]
    public void SelectionChanged_FiresOnSelectAll()
    {
        EnsureAvalonia();

        using var canvas = new DrawingCanvas();
        canvas.AddLayer();
        var events = new DocumentEventsSource(canvas);
        var count = 0;
        events.SelectionChanged += () => count++;

        canvas.SelectAll();
        Assert.Equal(1, count);
    }

    [Fact]
    public void ViewportChanged_FiresFromViewHost()
    {
        EnsureAvalonia();

        using var canvas = new DrawingCanvas();
        var view = new TestViewHost();
        var events = new DocumentEventsSource(canvas, view);
        var count = 0;
        events.ViewportChanged += () => count++;

        view.RaiseViewportChanged();
        Assert.Equal(1, count);
    }

    [Fact]
    public void BindCanvas_SwitchesEventSource()
    {
        EnsureAvalonia();

        using var canvasA = new DrawingCanvas();
        using var canvasB = new DrawingCanvas();
        var events = new DocumentEventsSource(canvasA);
        var count = 0;
        events.StructureChanged += () => count++;

        events.BindCanvas(canvasB);
        canvasA.AddLayer();
        Assert.Equal(0, count);

        canvasB.AddLayer();
        Assert.Equal(1, count);
    }

    [Fact]
    public void Bootstrap_RegistersDocumentEvents()
    {
        EnsureAvalonia();

        using var canvas = new DrawingCanvas();
        var services = FeatureSessionBootstrap.Create(canvas);
        Assert.NotNull(services.TryGet<IDocumentEvents>());
    }
}
