namespace Floss.App.Tests;

public class BrushSizeAdjustmentTests
{
    [Fact]
    public void ScalesSmoothlyAcrossSizes()
    {
        TestAssertions.Near(400, BrushSizeAdjustment.FromRadiusDistance(200, 1, 2000));
        TestAssertions.Near(20, BrushSizeAdjustment.FromRadiusDistance(10, 1, 2000));

        var smallNudge = BrushSizeAdjustment.Nudge(10, 1, 2, 1, 2000);
        var largeBrushNudge = BrushSizeAdjustment.Nudge(1000, 1, 2, 1, 2000);
        TestAssertions.Near(12, smallNudge);
        TestAssertions.True(largeBrushNudge - 1000 > 2, "Expected large brushes to use proportional nudge deltas.");
    }

    [Fact]
    public void ClampsBoundaries()
    {
        TestAssertions.Equal(1.0, BrushSizeAdjustment.FromRadiusDistance(0, 1, 2000));
        TestAssertions.Equal(2000.0, BrushSizeAdjustment.FromRadiusDistance(2000, 1, 2000));
        TestAssertions.Equal(1.0, BrushSizeAdjustment.Nudge(1, -1, 10, 1, 2000));
        TestAssertions.Equal(2000.0, BrushSizeAdjustment.Nudge(2000, 1, 10, 1, 2000));
    }
}

