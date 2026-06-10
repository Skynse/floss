using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Floss.App.Canvas;
using Floss.App.Features;
using Floss.App.Features.Overlays;
using Xunit;

namespace Floss.App.Tests;

public class CanvasOverlayRegistryTests
{
    private static void EnsureAvalonia() => AvaloniaTestBootstrap.EnsureInitialized();

    [Fact]
    public void Register_RenderInvokesOverlayInOrder()
    {
        EnsureAvalonia();
        var canvas = new DrawingCanvas();
        canvas.AddLayer();
        canvas.Measure(new Size(100, 100));
        canvas.Arrange(new Rect(0, 0, 100, 100));
        var registry = new CanvasOverlayRegistry();
        registry.BindCanvas(canvas);

        var order = new List<int>();
        registry.Register(new TestOverlay(100, () => order.Add(100)));
        registry.Register(new TestOverlay(10, () => order.Add(10)));

        var recorder = new DrawingGroup();
        using (var dc = recorder.Open())
            registry.Render(dc, canvas, 1.0);

        Assert.Equal([10, 100], order);
    }

    [Fact]
    public void Overlay_TryHandlePointer_CanConsumeDown()
    {
        var overlay = new CapturingOverlay();
        var handled = overlay.TryHandlePointer(new CanvasOverlayPointerEvent
        {
            Kind = CanvasOverlayPointerKind.Down,
            CanvasPosition = new Point(10, 10),
            DocumentPosition = new Point(100, 100),
            PointerId = 1,
            Pressure = 0.5f,
        });

        Assert.True(handled);
        Assert.Equal(1, overlay.DownCount);
    }

    [Fact]
    public void Bootstrap_RegistersOverlayRegistry()
    {
        EnsureAvalonia();
        var canvas = new DrawingCanvas();
        var services = FeatureSessionBootstrap.Create(canvas);
        Assert.NotNull(services.TryGet<ICanvasOverlayRegistry>());
    }

    private sealed class TestOverlay(int order, Action onRender) : ICanvasOverlay
    {
        public int Order => order;

        public void Render(CanvasOverlayContext context) => onRender();

        public bool TryHandlePointer(CanvasOverlayPointerEvent pointerEvent) => false;
    }

    private sealed class CapturingOverlay : ICanvasOverlay
    {
        public int DownCount { get; private set; }

        public int Order => 0;

        public void Render(CanvasOverlayContext context) { }

        public bool TryHandlePointer(CanvasOverlayPointerEvent pointerEvent)
        {
            if (pointerEvent.Kind == CanvasOverlayPointerKind.Down)
                DownCount++;
            return pointerEvent.Kind == CanvasOverlayPointerKind.Down;
        }
    }
}
