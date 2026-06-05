namespace Floss.App.Tests;

public class BrushSpacingTests
{
    [Fact]
    public void AutoOn_PercentMatchesDiameterAtBrushSize()
    {
        var brush = new BrushPreset("Auto", 400, 1, 1, 0.05, Colors.Black, 0)
        {
            AutoSpacingActive = true
        };

        var spacing = BrushSpacing.EffectiveDistance(brush, 400, 1, 0);
        TestAssertions.Near(20, spacing, 0.5);
    }

    [Fact]
    public void AutoOn_1024pxAt2Percent_IsNotSubPixel()
    {
        var brush = new BrushPreset("Huge", 1024, 1, 1, 0.02, Colors.Black, 0)
        {
            AutoSpacingActive = true
        };

        var spacing = BrushSpacing.EffectiveDistance(brush, 1024, 1, 0);
        TestAssertions.Near(20.48, spacing, 0.5);
        TestAssertions.True(spacing > 10,
            $"2% at 1024px should be ~20px between dabs, got {spacing}px (sub-pixel spacing stamp-storms the UI).");
    }

    [Fact]
    public void AutoOff_UsesDiameterFraction()
    {
        var brush = new BrushPreset("Manual", 400, 1, 1, 0.25, Colors.Black, 0)
        {
            AutoSpacingActive = false
        };

        var spacing = BrushSpacing.EffectiveDistance(brush, 400, 1, 0);
        TestAssertions.Near(100, spacing, 0.5);
    }

    [Fact]
    public void ManualSpacing_ChangesEffectiveDistance()
    {
        var low = new BrushPreset("Low", 200, 1, 1, 0.05, Colors.Black, 0) { AutoSpacingActive = false };
        var high = new BrushPreset("High", 200, 1, 1, 0.40, Colors.Black, 0) { AutoSpacingActive = false };

        var lowSpacing = BrushSpacing.EffectiveDistance(low, 200, 1, 0);
        var highSpacing = BrushSpacing.EffectiveDistance(high, 200, 1, 0);
        TestAssertions.True(lowSpacing < highSpacing,
            $"Expected lower spacing fraction to produce smaller gap, low={lowSpacing} high={highSpacing}.");
    }

    [Fact]
    public void BrushEngine_LowManualSpacingUsesMoreStampsThanHigh()
    {
        using var engine = new BrushEngine();
        using var layer = new DrawingLayer("Layer", 2048, 512);
        var from = Sample(40, 256, 0);
        var to = Sample(1800, 256, 16_000);

        var lowBrush = new BrushPreset("Low spacing", 48, 1, 0.75, 0.02, Colors.Black, 0)
        {
            AutoSpacingActive = false,
            Tip = new ProceduralBrushTip(BrushTipShape.Circle),
            Dynamics = new BrushDynamics()
        };
        var highBrush = lowBrush with { Name = "High spacing", Spacing = 0.35 };

        engine.BeginStroke(lowBrush, from);
        engine.RasterizeSegment(layer, lowBrush, from, to);
        var lowCount = engine.LastStats.StampCount;

        engine.BeginStroke(highBrush, from);
        engine.RasterizeSegment(layer, highBrush, from, to);
        var highCount = engine.LastStats.StampCount;

        TestAssertions.True(lowCount > highCount,
            $"Expected low spacing ({lowCount} stamps) to exceed high spacing ({highCount} stamps).");
    }

    [Fact]
    public void NormalizeSpacingAtLoad_MapsLegacyGapModeToSpacing()
    {
        var spacing = BrushSpacing.NormalizeSpacingAtLoad(0.10, 1.0, autoSpacingActive: false, BrushGapMode.Normal);
        TestAssertions.Near(0.25, spacing, 0.001);
    }

    private static CanvasInputSample Sample(double x, double y, long timeMicros)
        => new(x, y, 1, 0, 0, 0, timeMicros, 1, CanvasInputSource.Mouse, CanvasInputPhase.Move);
}
