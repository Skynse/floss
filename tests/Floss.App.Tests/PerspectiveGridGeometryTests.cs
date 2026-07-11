using System.Linq;
using Avalonia;
using Floss.App.Document.Assistants;
using Xunit;

namespace Floss.App.Tests;

public class PerspectiveGridGeometryTests
{
    [Fact]
    public void ResolveClipBounds_UsesVisibleDocumentRectWhenProvided()
    {
        var assistant = PaintingAssistant.FromDrag(
            PaintingAssistant.PerspectiveType, new Point(100, 100), new Point(200, 120));
        assistant.PerspectiveMode = PerspectiveAssistantMode.OnePoint;
        var visible = new Rect(-50, -50, 800, 600);

        var clip = PerspectiveGridGeometry.ResolveClipBounds(assistant, visible);

        Assert.Equal(visible, clip);
    }

    [Fact]
    public void EnumerateCurves_WithVisibleRect_ExtendsPastHandleBox()
    {
        var assistant = PaintingAssistant.FromDrag(
            PaintingAssistant.PerspectiveType, new Point(100, 100), new Point(200, 100));
        assistant.PerspectiveMode = PerspectiveAssistantMode.OnePoint;
        assistant.GridSubdivisions = 4;
        var visible = new Rect(-200, -200, 1000, 800);

        var handleBox = PerspectiveGridGeometry.GetHandleBounds(assistant);
        var curves = PerspectiveGridGeometry.EnumerateCurves(assistant, visible, zoom: 1.0).ToList();
        Assert.NotEmpty(curves);

        var anyOutsideHandles = curves.SelectMany(c => c.Points)
            .Any(p => p.X < handleBox.X - 1 || p.X > handleBox.Right + 1
                      || p.Y < handleBox.Y - 1 || p.Y > handleBox.Bottom + 1);
        Assert.True(anyOutsideHandles, "viewport clip should produce rays beyond the handle box");
    }

    [Fact]
    public void ParallelLineSpacing_IsStableAcrossZoom_AndDenserWithSubdivisions()
    {
        var assistant = PaintingAssistant.FromDrag(
            PaintingAssistant.PerspectiveType, new Point(0, 0), new Point(100, 0));
        assistant.GridSubdivisions = 4;
        var bounds = new Rect(0, 0, 2000, 2000);

        var at1x = PerspectiveGridGeometry.ParallelLineSpacing(assistant, bounds, zoom: 1.0);
        var at2x = PerspectiveGridGeometry.ParallelLineSpacing(assistant, bounds, zoom: 2.0);
        var at005x = PerspectiveGridGeometry.ParallelLineSpacing(assistant, bounds, zoom: 0.05);
        Assert.Equal(at1x, at2x);
        Assert.Equal(at1x, at005x);

        assistant.GridSubdivisions = 8;
        var denser = PerspectiveGridGeometry.ParallelLineSpacing(assistant, bounds, zoom: 1.0);
        Assert.True(denser < at1x);
    }

    [Fact]
    public void EnumerateCurves_LatticePhaseStableAcrossZoom()
    {
        var assistant = PaintingAssistant.FromDrag(
            PaintingAssistant.PerspectiveType, new Point(400, 200), new Point(700, 200));
        assistant.PerspectiveMode = PerspectiveAssistantMode.OnePoint;
        assistant.GridSubdivisions = 6;
        var visible = new Rect(0, 0, 800, 600);

        var at1x = DepthSortedTransversalDistances(assistant, visible, zoom: 1.0);
        var at4x = DepthSortedTransversalDistances(assistant, visible, zoom: 4.0);
        var at025x = DepthSortedTransversalDistances(assistant, visible, zoom: 0.25);

        Assert.Equal(at1x.Count, at4x.Count);
        Assert.Equal(at1x.Count, at025x.Count);
        for (var i = 0; i < at1x.Count; i++)
        {
            Assert.True(Math.Abs(at1x[i] - at4x[i]) < 0.5, $"zoom-in shifted transversal {i}");
            Assert.True(Math.Abs(at1x[i] - at025x[i]) < 0.5, $"zoom-out shifted transversal {i}");
        }
    }

    [Fact]
    public void EnumerateCurves_CapsLineCountAtHighZoomOut()
    {
        var assistant = PaintingAssistant.FromDrag(
            PaintingAssistant.PerspectiveType, new Point(400, 300), new Point(500, 300));
        assistant.PerspectiveMode = PerspectiveAssistantMode.OnePoint;
        assistant.GridSubdivisions = 12;
        var visible = new Rect(-5000, -5000, 12000, 10000);

        var curves = PerspectiveGridGeometry.EnumerateCurves(assistant, visible, zoom: 0.05).ToList();
        Assert.True(curves.Count < 200, $"expected capped density, got {curves.Count}");
    }

    [Fact]
    public void FisheyeLens_IsStableAcrossVisibleViewport()
    {
        var assistant = PaintingAssistant.FromDrag(
            PaintingAssistant.FisheyeType, new Point(100, 100), new Point(140, 110));
        assistant.FisheyeEnabled = true;
        assistant.PerspectiveMode = PerspectiveAssistantMode.OnePoint;

        var tiny = new Rect(100, 100, 40, 20);
        var smallLens = FisheyeAssistantGeometry.GetLensFrame(assistant, tiny);
        var visible = new Rect(-100, -100, 1200, 900);
        var viewportLens = FisheyeAssistantGeometry.GetLensFrame(assistant, visible);

        Assert.Equal(smallLens.Center, viewportLens.Center);
        Assert.Equal(smallLens.Radius, viewportLens.Radius);
    }

    [Fact]
    public void FisheyeWarpedCurves_CoverVisibleArea()
    {
        var assistant = PaintingAssistant.FromDrag(
            PaintingAssistant.FisheyeType, new Point(400, 300), new Point(500, 300));
        assistant.FisheyeEnabled = true;
        assistant.PerspectiveMode = PerspectiveAssistantMode.OnePoint;
        assistant.GridSubdivisions = 4;
        var visible = new Rect(0, 0, 800, 600);

        var curves = FisheyeAssistantGeometry.EnumerateWarpedGridCurves(assistant, visible, zoom: 1.0).ToList();
        Assert.NotEmpty(curves);
        var frame = FisheyeAssistantGeometry.GetLensFrame(assistant, visible);
        Assert.True(frame.Radius >= 400);
    }

    [Fact]
    public void OnePointGrid_TransversalsForeshortenTowardVanishingPoint()
    {
        var vp = new Point(400, 200);
        var horizon = new Point(700, 200);
        var assistant = PaintingAssistant.FromDrag(PaintingAssistant.PerspectiveType, vp, horizon);
        assistant.PerspectiveMode = PerspectiveAssistantMode.OnePoint;
        assistant.GridSubdivisions = 6;
        var visible = new Rect(0, 0, 800, 600);

        var transversals = DepthSortedTransversalDistances(assistant, visible, zoom: 1.0);

        Assert.True(transversals.Count >= 4, $"expected several transversals, got {transversals.Count}");

        var nearGap = transversals[0] - transversals[1];
        var farGap = transversals[^2] - transversals[^1];
        Assert.True(nearGap > farGap * 1.15,
            $"expected foreshortening: nearGap={nearGap:0.#} farGap={farGap:0.#}");
    }

    private static List<double> DepthSortedTransversalDistances(
        PaintingAssistant assistant,
        Rect visible,
        double zoom)
    {
        var vp = assistant.HandleA;
        return PerspectiveGridGeometry.EnumerateCurves(assistant, visible, zoom)
            .Where(c => c.Plane == 0 && !c.IsPrimary)
            .Select(c =>
            {
                var mid = new Point(
                    (c.Points[0].X + c.Points[^1].X) * 0.5,
                    (c.Points[0].Y + c.Points[^1].Y) * 0.5);
                return Math.Sqrt((mid.X - vp.X) * (mid.X - vp.X) + (mid.Y - vp.Y) * (mid.Y - vp.Y));
            })
            .OrderByDescending(d => d)
            .ToList();
    }
}
