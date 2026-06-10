using Floss.App.Canvas;

namespace Floss.App.Features;

/// <summary>Service that must follow the active tab's <see cref="DrawingCanvas"/>.</summary>
internal interface ICanvasBoundService
{
    void BindCanvas(DrawingCanvas canvas);
}
