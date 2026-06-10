using Avalonia;
using Floss.App.SmartShape;
using Xunit;

namespace Floss.App.Tests.SmartShape;

public sealed class SmartShapeGizmoTests
{
    [Fact]
    public void CurveHandles_ExposeAnchorsOnly()
    {
        var curve = new CurveShape([
            (new Vec2(0, 0), new Vec2(10, 30), new Vec2(20, -10), new Vec2(30, 0)),
            (new Vec2(30, 0), new Vec2(40, 10), new Vec2(50, -10), new Vec2(60, 0))
        ]);
        var frame = SmartShapeGizmo.GetFrameRect(curve);

        var handles = SmartShapeGizmo.ComputeHandles(curve, frame, 0);
        Assert.DoesNotContain(handles, h => h.Kind == GizmoHandleKind.CurveControl);
        Assert.Equal(3, handles.Count(h => h.Kind == GizmoHandleKind.CurveAnchor));
    }

    [Fact]
    public void DragCurveAnchor_PreservesControlOffsets()
    {
        var curve = new CurveShape([
            (new Vec2(0, 0), new Vec2(10, 20), new Vec2(20, 20), new Vec2(30, 0))
        ]);
        var handle = new GizmoHandle(GizmoHandleKind.CurveAnchor, curve.Curves[0].P0, 0);
        var moved = (CurveShape)SmartShapeGizmo.ApplyAnchorDrag(curve, handle, new Vec2(5, 5));

        Assert.Equal(new Vec2(5, 5), moved.Curves[0].P0);
        Assert.Equal(new Vec2(15, 25), moved.Curves[0].P1);
        Assert.Equal(new Vec2(30, 0), moved.Curves[0].P3);
    }

    [Fact]
    public void DragPolylinePoint_InfluencesNeighbors()
    {
        var poly = new PolylineShape([
            new Vec2(0, 0),
            new Vec2(50, 0),
            new Vec2(100, 0)
        ]);
        var handle = new GizmoHandle(GizmoHandleKind.CurveAnchor, poly.Points[1], 1);
        var moved = (PolylineShape)SmartShapeGizmo.ApplyAnchorDrag(poly, handle, new Vec2(50, 40));

        Assert.Equal(new Vec2(50, 40), moved.Points[1]);
        Assert.Equal(new Vec2(0, 15.2), moved.Points[0]);
        Assert.Equal(new Vec2(100, 15.2), moved.Points[2]);
    }

    [Fact]
    public void MoveDrag_TracksPointerDelta()
    {
        var line = new LineShape(new Vec2(10, 10), new Vec2(90, 10));
        var baseRect = SmartShapeGizmo.GetFrameRect(line);
        var (movedRect, _) = SmartShapeGizmo.UpdateFrameDrag(
            GizmoHandleKind.Move,
            baseRect,
            0,
            new Vec2(0, 0),
            new Vec2(20, 15),
            new SmartShapeGizmoModifiers());

        var moved = (LineShape)SmartShapeGizmo.ApplyFrameToShape(line, baseRect, 0, movedRect, 0);
        Assert.Equal(new Vec2(30, 25), moved.Start);
        Assert.Equal(new Vec2(110, 25), moved.End);
    }

    [Fact]
    public void RotateDrag_RotatesAroundBoundsCenter()
    {
        var line = new LineShape(new Vec2(0, 0), new Vec2(100, 0));
        var baseRect = SmartShapeGizmo.GetFrameRect(line);
        var center = new Vec2(baseRect.X + baseRect.Width * 0.5, baseRect.Y + baseRect.Height * 0.5);
        var (_, angle) = SmartShapeGizmo.UpdateFrameDrag(
            GizmoHandleKind.Rotate,
            baseRect,
            0,
            new Vec2(center.X + 40, center.Y),
            new Vec2(center.X, center.Y + 40),
            new SmartShapeGizmoModifiers());

        var rotated = (LineShape)SmartShapeGizmo.ApplyFrameToShape(line, baseRect, 0, baseRect, angle);
        Assert.InRange(rotated.End.X, rotated.Start.X - 1, rotated.Start.X + 1);
        Assert.True(rotated.Start.Y < center.Y);
        Assert.True(rotated.End.Y > center.Y);
    }

    [Fact]
    public void FrameRect_PadsCurveAnchorsAwayFromBounds()
    {
        var curve = new CurveShape([
            (new Vec2(0, 0), new Vec2(10, 30), new Vec2(20, -10), new Vec2(30, 0)),
            (new Vec2(30, 0), new Vec2(40, 10), new Vec2(50, -10), new Vec2(60, 0))
        ]);
        var frame = SmartShapeGizmo.GetFrameRect(curve);
        const double minInset = 20;

        foreach (var seg in curve.Curves)
        {
            Assert.True(seg.P0.X >= frame.X + minInset);
            Assert.True(seg.P0.Y >= frame.Y + minInset);
            Assert.True(seg.P3.X <= frame.Right - minInset);
            Assert.True(seg.P3.Y <= frame.Bottom - minInset);
        }
    }

    [Fact]
    public void HitTest_UsesFrameLocalCoordinatesWhenRotated()
    {
        var shape = new RectangleShape(new Vec2(50, 50), 40, 40, 0);
        var frame = SmartShapeGizmo.GetFrameRect(shape);
        var handles = SmartShapeGizmo.ComputeHandles(shape, frame, 45);
        var hit = SmartShapeGizmo.HitTest(shape, frame, 45, handles, new Vec2(50, 50), zoom: 1);
        Assert.NotNull(hit);
        Assert.Equal(GizmoHandleKind.Move, hit!.Value.Kind);
    }
}
