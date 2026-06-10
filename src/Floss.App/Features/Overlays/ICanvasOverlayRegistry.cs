using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Canvas;
using Floss.App.Tools;

namespace Floss.App.Features.Overlays;

public interface ICanvasOverlayRegistry
{
    void Register(ICanvasOverlay overlay);

    void Unregister(ICanvasOverlay overlay);

    void Render(DrawingContext context, DrawingCanvas canvas, double zoom);

    bool TryHandlePointer(
        ToolInputEventKind kind,
        PointerPoint canvasPoint,
        KeyModifiers modifiers,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight);
}
