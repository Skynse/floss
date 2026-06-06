using Floss.App.Input;
using Floss.App.SmartShape;

namespace Floss.App.Tests.SmartShape;

public sealed class SmartShapeSampleRemapTests
{
    [Fact]
    public void RemapOntoShape_PreservesPressureAlongOpenLine()
    {
        var layer = new Document.DrawingLayer("test", 512, 512);
        var raw = new List<CanvasInputSample>();
        for (var i = 0; i <= 20; i++)
        {
            var t = i / 20.0;
            raw.Add(Sample(100 + t * 200, 100, 0.2 + t * 0.8, i));
        }

        var shapePath = new List<Vec2> { new(100, 100), new(300, 100) };
        var mapped = SmartShapeSampleRemap.RemapOntoShape(shapePath, raw, layer, strokeClosed: false);

        Assert.Equal(2, mapped.Count);
        Assert.InRange(mapped[0].Pressure, 0.15, 0.35);
        Assert.InRange(mapped[1].Pressure, 0.85, 1.05);
    }

    [Fact]
    public void RemapOntoShape_PreservesTiltAlongPath()
    {
        var layer = new Document.DrawingLayer("test", 512, 512);
        var raw = new List<CanvasInputSample>
        {
            Sample(0, 0, 1, 0, tiltX: 0.1, tiltY: 0.2),
            Sample(100, 0, 1, 1, tiltX: 0.9, tiltY: 0.8)
        };

        var shapePath = new List<Vec2> { new(0, 0), new(50, 0), new(100, 0) };
        var mapped = SmartShapeSampleRemap.RemapOntoShape(shapePath, raw, layer, strokeClosed: false);

        Assert.InRange(mapped[0].TiltX, 0.05, 0.15);
        Assert.InRange(mapped[^1].TiltX, 0.85, 0.95);
    }

    private static CanvasInputSample Sample(
        double x,
        double y,
        double pressure,
        long index,
        double tiltX = 0,
        double tiltY = 0)
        => new(x, y, pressure, tiltX, tiltY, 0, index, 0, CanvasInputSource.Pen, CanvasInputPhase.Move);
}
