using System;
using Avalonia;
using Avalonia.Media;

namespace Floss.App.Processes.Input;

// Draws the shift-line preview: a capsule (pill shape) whose diameter
// matches the brush size, connecting the anchor point to the cursor.
internal static class StraightLineOverlay
{
    public static void Draw(DrawingContext dc, double zoom, Point from, Point to, double brushSize)
    {
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);

        // Radius in canvas space. Clamp to at least 1 px on screen.
        var r = Math.Max(brushSize * 0.5, 1.0 / zoom);

        // Outline pen — 1 px on screen regardless of zoom.
        var penW = Math.Max(0.5, 1.0 / zoom);
        var outlinePen = new Pen(new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)), penW);
        var innerPen   = new Pen(new SolidColorBrush(Color.FromArgb(100, 255, 255, 255)), penW);

        if (len < 1.0 / zoom)
        {
            // Just draw the circle at the anchor when cursor hasn't moved.
            dc.DrawEllipse(null, outlinePen, from, r, r);
            return;
        }

        // Unit perpendicular vector.
        var nx = -dy / len;
        var ny =  dx / len;

        // The four corners of the rectangular body.
        var p0 = new Point(from.X + nx * r, from.Y + ny * r);
        var p1 = new Point(from.X - nx * r, from.Y - ny * r);
        var p2 = new Point(to.X   - nx * r, to.Y   - ny * r);
        var p3 = new Point(to.X   + nx * r, to.Y   + ny * r);

        // Fill: semi-transparent dark tint so it reads over any background.
        var fillBrush = new SolidColorBrush(Color.FromArgb(55, 0, 0, 0));

        // Build the capsule geometry using StreamGeometry.
        // Arc sweep direction: the perpendicular turns into a semi-circle.
        var geo = BuildCapsule(from, to, r, nx, ny);

        dc.DrawGeometry(fillBrush, outlinePen, geo);

        // Inner highlight line along the centerline for the "tube" feel.
        var hiPen = new Pen(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), penW);
        dc.DrawLine(hiPen, from, to);

        // Endpoint circles with outline to mark the two endpoints clearly.
        dc.DrawEllipse(null, outlinePen, from, r, r);
        dc.DrawEllipse(null, outlinePen, to,   r, r);
    }

    private static StreamGeometry BuildCapsule(Point from, Point to, double r, double nx, double ny)
    {
        var geo = new StreamGeometry();
        using var ctx = geo.Open();

        // Start at the top of the "from" circle.
        var startPt = new Point(from.X + nx * r, from.Y + ny * r);
        ctx.BeginFigure(startPt, isFilled: true);

        // Line to top of the "to" circle.
        ctx.LineTo(new Point(to.X + nx * r, to.Y + ny * r));

        // Arc around the "to" end — sweep 180° CW from top to bottom.
        ctx.ArcTo(
            new Point(to.X - nx * r, to.Y - ny * r),
            new Size(r, r),
            rotationAngle: 0,
            isLargeArc: false,
            sweepDirection: SweepDirection.Clockwise);

        // Line back to bottom of the "from" circle.
        ctx.LineTo(new Point(from.X - nx * r, from.Y - ny * r));

        // Arc around the "from" end — sweep 180° CCW back to start.
        ctx.ArcTo(
            startPt,
            new Size(r, r),
            rotationAngle: 0,
            isLargeArc: false,
            sweepDirection: SweepDirection.CounterClockwise);

        ctx.EndFigure(isClosed: true);
        return geo;
    }
}
