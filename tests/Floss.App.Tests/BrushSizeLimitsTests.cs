using Floss.App.Brushes;

namespace Floss.App.Tests;

public class BrushSizeLimitsTests
{
    [Fact]
    public void EffectiveMaximum_ScalesWithCanvasShortSide()
    {
        TestAssertions.Near(1024, BrushSizeLimits.EffectiveMaximum(2048, 4096));
        TestAssertions.Near(2048, BrushSizeLimits.EffectiveMaximum(4096, 8192));
        TestAssertions.Near(400, BrushSizeLimits.EffectiveMaximum(800, 1200));
    }

    [Fact]
    public void EffectiveMaximum_RespectsStudioOverride()
    {
        TestAssertions.Near(4096, BrushSizeLimits.EffectiveMaximum(2048, 2048, 400));
        TestAssertions.Near(1024, BrushSizeLimits.EffectiveMaximum(2048, 2048, 100));
    }

    [Fact]
    public void EffectiveMaximum_ClampedToHardCap()
    {
        TestAssertions.Equal(8192, BrushSizeLimits.EffectiveMaximum(32000, 32000, 400));
    }
}
