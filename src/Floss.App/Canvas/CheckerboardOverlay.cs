using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Floss.App.Canvas;

/// <summary>Solid workspace backdrop behind the scaled canvas frame (margins / pan area).</summary>
internal sealed class CheckerboardOverlay : Control
{
    internal DrawingCanvas Canvas { get; set; }

    internal static readonly ISolidColorBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#323232"));

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
