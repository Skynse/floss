namespace Floss.App.Tests;

public class BrushSpacingTests
{
    [Fact]
    public void NormalGap_UsesQuarterDiameter()
    {
        var brush = new BrushPreset("Normal", 400, 1, 1, 0.10, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Normal
        };

        var spacing = BrushSpacing.EffectiveDistance(brush, 400, 1, 0);
        TestAssertions.Near(100, spacing, 0.5);
    }

    [Fact]
    public void NormalGap_IsWiderThanFixedNarrow()
    {
        var normal = new BrushPreset("Normal", 500, 1, 1, 0.25, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Normal
        };
        var fixedNarrow = new BrushPreset("Fixed", 500, 1, 1, 0.10, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Fixed
        };

        var normalSpacing = BrushSpacing.EffectiveDistance(normal, 500, 1, 0);
        var fixedSpacing = BrushSpacing.EffectiveDistance(fixedNarrow, 500, 1, 0);
        TestAssertions.True(normalSpacing > fixedSpacing);
    }

    [Fact]
    public void BrushEngine_NormalGapUsesFewerStampsThanFixedNarrow()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 2048, 512);
        var from = Sample(40, 256, 0);
        var to = Sample(1800, 256, 16_000);

        var normalBrush = new BrushPreset("Normal", 320, 1, 0.75, 0.25, Colors.Black, 0)
        {
            GapMode = BrushGapMode.Normal,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Dynamics = new BrushDynamics()
        };
        var fixedBrush = normalBrush with
        {
            Name = "Fixed",
            GapMode = BrushGapMode.Fixed,
            Spacing = 0.08
        };

        engine.BeginStroke(normalBrush, from);
        engine.RasterizeSegment(layer, normalBrush, from, to);
        var normalCount = engine.LastStats.StampCount;

        engine.BeginStroke(fixedBrush, from);
        engine.RasterizeSegment(layer, fixedBrush, from, to);
        var fixedCount = engine.LastStats.StampCount;

        TestAssertions.True(normalCount < fixedCount,
            $"Expected Normal gap to use fewer stamps ({normalCount}) than Fixed narrow ({fixedCount}).");
    }

    private static CanvasInputSample Sample(double x, double y, long timeMicros)
        => new(x, y, 1, 0, 0, 0, timeMicros, 1, CanvasInputSource.Mouse, CanvasInputPhase.Move);
}
