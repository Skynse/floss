using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Floss.App.Canvas;

internal sealed class CheckerboardOverlay : Control
{
    internal DrawingCanvas Canvas { get; set; }

    internal static readonly ISolidColorBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#2a2a2e"));

    public CheckerboardOverlay(DrawingCanvas canvas)
    {
        Canvas = canvas;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.FillRectangle(BackgroundBrush, new Rect(0, 0, Bounds.Width, Bounds.Height));
    }
}
