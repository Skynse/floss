using Floss.App.Features;

namespace Floss.App.Tests;

public class CanvasViewTransformMathTests
{
    static CanvasViewTransformMathTests() => AvaloniaTestBootstrap.EnsureInitialized();

    [Fact]
    public void PanToCenterDocumentPoint_AtDocumentCenter_IsZeroPan()
    {
        var (panX, panY) = CanvasViewTransformMath.PanToCenterDocumentPoint(
            100, 50, 200, 100, zoom: 2, rotationDeg: 0, flipX: 1, flipY: 1);

        Assert.Equal(0, panX, precision: 3);
        Assert.Equal(0, panY, precision: 3);
    }

    [Fact]
    public void PanToCenterDocumentPoint_Rotated90_DiffersFromUnrotated()
    {
        var unrotated = CanvasViewTransformMath.PanToCenterDocumentPoint(
            20, 30, 200, 100, zoom: 1, rotationDeg: 0, flipX: 1, flipY: 1);
        var rotated = CanvasViewTransformMath.PanToCenterDocumentPoint(
            20, 30, 200, 100, zoom: 1, rotationDeg: 90, flipX: 1, flipY: 1);

        Assert.NotEqual(unrotated.PanX, rotated.PanX, precision: 3);
        Assert.NotEqual(unrotated.PanY, rotated.PanY, precision: 3);
    }

    [Fact]
    public void DocumentDeltaToViewportPan_RotationZero_ScalesByZoomAndFlip()
    {
        var (panDx, panDy) = CanvasViewTransformMath.DocumentDeltaToViewportPan(
            docDx: 10, docDy: 5, zoom: 2, rotationDeg: 0, flipX: 1, flipY: 1);

        Assert.Equal(-20, panDx, precision: 3);
        Assert.Equal(-10, panDy, precision: 3);
    }

    [Fact]
    public void DocumentDeltaToViewportPan_FlipX_NegatesHorizontalPan()
    {
        var normal = CanvasViewTransformMath.DocumentDeltaToViewportPan(
            10, 0, zoom: 1, rotationDeg: 0, flipX: 1, flipY: 1);
        var flipped = CanvasViewTransformMath.DocumentDeltaToViewportPan(
            10, 0, zoom: 1, rotationDeg: 0, flipX: -1, flipY: 1);

        Assert.Equal(-normal.PanDx, flipped.PanDx, precision: 3);
    }
}
