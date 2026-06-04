using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Floss.App.Canvas;

internal sealed class CheckerboardOverlay : Control
{
    internal DrawingCanvas Canvas { get; set; }

    internal static readonly ISolidColorBrush BackgroundBrush = new SolidColorBrush(Color.Parse("#252525"));
    private static readonly IBrush DotBrush = new SolidColorBrush(Color.FromArgb(34, 255, 255, 255));

    public CheckerboardOverlay(DrawingCanvas canvas)
    {
        Canvas = canvas;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext ctx)
    {
        base.Render(ctx);
        ctx.FillRectangle(BackgroundBrush, new Rect(0, 0, Bounds.Width, Bounds.Height));

        const double spacing = 24;
        const double radius = 1.05;
        for (var y = spacing * 0.5; y < Bounds.Height; y += spacing)
        {
            for (var x = spacing * 0.5; x < Bounds.Width; x += spacing)
                ctx.DrawEllipse(DotBrush, null, new Point(x, y), radius, radius);
        }
    }
}
