using Floss.App.SmartShape;

namespace Floss.App.Tests.SmartShape;

public sealed class SmartShapeFitterTests
{
    [Fact]
    public void Fit_ForcedCircle_FromRoughLoop()
    {
        var pts = EllipseStroke(80, 60, 35);
        var shape = SmartShapeFitter.Fit(pts, SmartShapeFitKind.Circle);
        Assert.IsType<CircleShape>(shape);
    }

    [Fact]
    public void Fit_ForcedSquare_FromRoughRect()
    {
        var pts = RectangleStroke(100, 50, 120, 80);
        var shape = SmartShapeFitter.Fit(pts, SmartShapeFitKind.Square);
        var rect = Assert.IsType<RectangleShape>(shape);
        Assert.Equal(rect.Width, rect.Height, 1);
    }

    [Fact]
    public void Fit_ForcedPolyline_ReturnsSimplifiedPoints()
    {
        var pts = new List<Vec2>();
        for (var i = 0; i <= 50; i++)
            pts.Add(new Vec2(i * 3, Math.Sin(i * 0.3) * 10));

        var shape = SmartShapeFitter.Fit(pts, SmartShapeFitKind.Polyline);
        var poly = Assert.IsType<PolylineShape>(shape);
        Assert.InRange(poly.Points.Count, 2, 20);
    }

    [Fact]
    public void Fit_ContinuousCurve_AllowsMoreSegmentsThanCurve()
    {
        var pts = new List<Vec2>();
        for (var i = 0; i <= 100; i++)
        {
            var t = i / 100.0 * Math.PI * 1.5;
            pts.Add(new Vec2(20 + i * 2, 80 + Math.Sin(t) * 30));
        }

        var curve = (CurveShape)SmartShapeFitter.Fit(pts, SmartShapeFitKind.Curve)!;
        var continuous = (CurveShape)SmartShapeFitter.Fit(pts, SmartShapeFitKind.ContinuousCurve)!;
        Assert.True(continuous.Curves.Count >= curve.Curves.Count);
    }

    [Fact]
    public void Constrain_EllipseToCircle()
    {
        var e = new EllipseShape(new Vec2(0, 0), 40, 20, 15);
        var c = Assert.IsType<CircleShape>(SmartShapeRegular.Constrain(e));
        Assert.Equal(40, c.Radius, 1);
    }

    private static List<Vec2> EllipseStroke(double cx, double cy, double r)
    {
        var pts = new List<Vec2>();
        for (var i = 0; i <= 60; i++)
        {
            var t = i * (2 * Math.PI / 60);
            pts.Add(new Vec2(cx + r * Math.Cos(t), cy + r * 0.6 * Math.Sin(t)));
        }
        pts.Add(pts[0]);
        return pts;
    }

    private static List<Vec2> RectangleStroke(double x, double y, double w, double h)
    {
        var pts = new List<Vec2>();
        for (var i = 0; i <= 20; i++)
            pts.Add(new Vec2(x + i * w / 20, y));
        for (var i = 0; i <= 15; i++)
            pts.Add(new Vec2(x + w, y + i * h / 15));
        for (var i = 0; i <= 20; i++)
            pts.Add(new Vec2(x + w - i * w / 20, y + h));
        for (var i = 0; i <= 15; i++)
            pts.Add(new Vec2(x, y + h - i * h / 15));
        pts.Add(pts[0]);
        return pts;
    }
}
