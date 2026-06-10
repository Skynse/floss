using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Processes.Input;

namespace Floss.App.Tests;

public sealed class BrushStrokeInputProcessTests
{
    private static CanvasInputSample Sample(double x, double y, long timeMicros, double pressure = 1.0)
        => new(x, y, pressure, 0, 0, 0, timeMicros, 1, CanvasInputSource.Mouse, CanvasInputPhase.Move);

    [Fact]
    public void BrushStrokeInputProcess_StabilizationLagsBehindPointer()
    {
        var proc = new BrushStrokeInputProcess { Stabilization = 0.8 };
        proc.PointerDown(Sample(0, 0, 0));

        for (var i = 1; i <= 30; i++)
            proc.PointerMove(Sample(i * 10.0, 0, i * 5_000));

        proc.PointerUp(Sample(300, 0, 155_000));
        var stroke = proc.GetResult() as StrokeInput;
        TestAssertions.True(stroke != null);
        TestAssertions.True(stroke!.SmoothedSamples.Count > 5);

        var last = stroke.SmoothedSamples[^1];
        TestAssertions.True(last.X < 300, $"Stabilized X should lag raw pointer, got {last.X}");
        TestAssertions.True(last.X > 20, $"Stabilized stroke should follow pointer, got {last.X}");
    }

    [Fact]
    public void BrushStrokeInputProcess_ZeroStabilization_FollowsRaw()
    {
        var proc = new BrushStrokeInputProcess { Stabilization = 0 };
        proc.PointerDown(Sample(0, 0, 0));
        proc.PointerMove(Sample(100, 50, 10_000));
        proc.PointerUp(Sample(100, 50, 20_000));

        var stroke = proc.GetResult() as StrokeInput;
        TestAssertions.True(stroke != null);
        var last = stroke!.SmoothedSamples[^1];
        TestAssertions.Near(100, last.X);
        TestAssertions.Near(50, last.Y);
    }

    [Fact]
    public void GetStabilizedPaintInfo_MatchesIncrementalAverage()
    {
        var queue = new List<CanvasInputSample>
        {
            Sample(0, 0, 0),
            Sample(10, 0, 1),
            Sample(20, 0, 2),
        };
        var latest = Sample(30, 0, 3);
        var stabilized = BrushStrokeInputProcess.GetStabilizedPaintInfo(queue, latest);
        TestAssertions.Near(20, stabilized.X, tolerance: 0.01);
    }

    [Fact]
    public void BrushStrokeInputProcess_ShiftClickLine_ReturnsImmediateResult()
    {
        var proc = new BrushStrokeInputProcess { Stabilization = 0 };
        proc.PointerDown(Sample(0, 0, 0));
        proc.PointerUp(Sample(0, 0, 1_000));

        proc.ToolAuxMode = ToolAuxOperationType.StraightLine;
        proc.PointerDown(Sample(100, 0, 2_000));
        var line = proc.GetImmediateResult() as StrokeInput;
        TestAssertions.True(line != null);
        TestAssertions.Equal(2, line!.SmoothedSamples.Count);
        TestAssertions.Near(100, line.SmoothedSamples[1].X);

        proc.PointerUp(Sample(100, 0, 2_500));
        TestAssertions.True(proc.GetResult() == null, "Shift-click should not leave a dab stroke");
    }

    [Fact]
    public void SmartShapeBrushInputProcess_ForwardsShiftClickImmediateResult()
    {
        var proc = new SmartShapeBrushInputProcess { Stabilization = 0 };
        proc.PointerDown(Sample(0, 0, 0));
        proc.PointerUp(Sample(0, 0, 1_000));

        proc.ToolAuxMode = ToolAuxOperationType.StraightLine;
        proc.PointerDown(Sample(50, 50, 2_000));
        var line = proc.GetImmediateResult() as StrokeInput;
        TestAssertions.True(line != null);
        TestAssertions.Near(50, line!.SmoothedSamples[1].X);
    }
}
