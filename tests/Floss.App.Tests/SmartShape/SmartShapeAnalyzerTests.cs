using Floss.App.SmartShape;

namespace Floss.App.Tests.SmartShape;

public sealed class SmartShapeAnalyzerTests
{
    [Fact]
    public void AnalyzeStroke_DetectsLine()
    {
        var pts = new List<Vec2>();
        for (var i = 0; i <= 40; i++)
            pts.Add(new Vec2(i * 4.0, i * 2.0 + Math.Sin(i * 0.2) * 0.5));

        var shape = SmartShapeAnalyzer.AnalyzeStroke(pts);
        Assert.NotNull(shape);
        Assert.IsType<LineShape>(shape);
    }

    [Fact]
    public void AnalyzeStroke_DetectsRectangle()
    {
        var pts = new List<Vec2>();
        for (var i = 0; i <= 30; i++)
            pts.Add(new Vec2(100 + i, 50 + Math.Sin(i * 0.3)));
        for (var i = 0; i <= 20; i++)
            pts.Add(new Vec2(220 + Math.Sin(i * 0.3), 50 + i));
        for (var i = 0; i <= 30; i++)
            pts.Add(new Vec2(220 - i, 70 + Math.Sin(i * 0.3)));
        for (var i = 0; i <= 20; i++)
            pts.Add(new Vec2(100 + Math.Sin(i * 0.3), 70 - i));
        pts.Add(pts[0]);

        var shape = SmartShapeAnalyzer.AnalyzeStroke(pts);
        Assert.NotNull(shape);
        Assert.True(shape is RectangleShape or PolygonShape);
    }

    [Fact]
    public void AnalyzeStroke_DetectsEllipse()
    {
        var pts = new List<Vec2>();
        for (var i = 0; i <= 80; i++)
        {
            var t = i * (2 * Math.PI / 80);
            pts.Add(new Vec2(200 + 60 * Math.Cos(t) + Math.Sin(i) * 0.8,
                150 + 35 * Math.Sin(t) + Math.Cos(i) * 0.8));
        }
        pts.Add(pts[0]);

        var shape = SmartShapeAnalyzer.AnalyzeStroke(pts);
        Assert.NotNull(shape);
        Assert.True(shape is EllipseShape or CircleShape);
    }

    [Fact]
    public void AnalyzeStroke_TooFewPoints_ReturnsNull()
    {
        var shape = SmartShapeAnalyzer.AnalyzeStroke(
        [
            new Vec2(0, 0),
            new Vec2(1, 1),
            new Vec2(2, 2)
        ]);
        Assert.Null(shape);
    }

    [Fact]
    public void AnalyzeStroke_OpenCurve_CapsSegmentCount()
    {
        var pts = new List<Vec2>();
        for (var i = 0; i <= 120; i++)
        {
            var t = i / 120.0 * Math.PI;
            pts.Add(new Vec2(50 + 200 * t / Math.PI, 100 + 40 * Math.Sin(t) + Math.Sin(i * 0.4) * 2));
        }

        var shape = SmartShapeAnalyzer.AnalyzeStroke(pts);
        var curve = Assert.IsType<CurveShape>(shape);
        Assert.InRange(curve.Curves.Count, 1, 4);
    }

    [Fact]
    public void TransformShape_ScalesLine()
    {
        var line = new LineShape(new Vec2(0, 0), new Vec2(10, 0));
        var scaled = SmartShapeTransforms.Transform(line, 5, 0, 2, 0);
        var result = Assert.IsType<LineShape>(scaled);
        Assert.Equal(-5, result.Start.X, 1);
        Assert.Equal(15, result.End.X, 1);
    }
}
