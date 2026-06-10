using System;
using Avalonia;

namespace Floss.App.Document.Assistants;

internal static class AssistantSnap
{
    private const double SnapRadians = Math.PI / 4;
    private const double SnapThresholdDoc = 12;
    private const double AxisAlignThreshold = 0.92;
    private const double MinStrokeLengthDoc = 2;

    public static Point ConstrainTo45Degrees(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 1e-6)
            return end;

        var angle = Math.Atan2(dy, dx);
        var snapped = Math.Round(angle / SnapRadians) * SnapRadians;
        return new Point(
            start.X + Math.Cos(snapped) * dist,
            start.Y + Math.Sin(snapped) * dist);
    }

    public static Point AdjustPosition(DocumentAssistants assistants, Point point, Point strokeBegin)
    {
        Point? proximityBest = null;
        var bestDist2 = SnapThresholdDoc * SnapThresholdDoc;

        Point? axisBest = null;
        var bestAxisAlign = AxisAlignThreshold;

        foreach (var assistant in assistants.All)
        {
            if (!assistant.IsVisible || !assistant.SnapEnabled)
                continue;

            var proximityCandidate = assistant.TypeId switch
            {
                PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType
                    => assistant.UsesFisheyeGrid
                        ? SnapToFisheye(assistant, point)
                        : SnapToPerspective(assistant, point),
                _ => SnapToSegment(point, assistant.HandleA, assistant.HandleB),
            };

            var d2 = DistanceSquared(point, proximityCandidate);
            if (d2 <= bestDist2)
            {
                bestDist2 = d2;
                proximityBest = proximityCandidate;
            }

            if (!TrySnapAlongAxis(strokeBegin, point, assistant, out var axisCandidate, out var axisAlign))
                continue;

            if (axisAlign > bestAxisAlign)
            {
                bestAxisAlign = axisAlign;
                axisBest = axisCandidate;
            }
        }

        return axisBest ?? proximityBest ?? point;
    }

    private static Point SnapToSegment(Point p, Point a, Point b)
        => ProjectOntoSegment(p, a, b);

    private static bool TrySnapAlongAxis(
        Point strokeBegin,
        Point point,
        PaintingAssistant assistant,
        out Point snapped,
        out double alignment)
    {
        snapped = point;
        alignment = 0;

        if (!TryGetUnitStrokeDirection(strokeBegin, point, out var strokeDir))
            return false;

        return assistant.TypeId switch
        {
            PaintingAssistant.RulerType => TrySnapRulerAxis(strokeBegin, point, strokeDir, assistant.HandleA, assistant.HandleB, out snapped, out alignment),
            PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType
                => TrySnapPerspectiveAxis(strokeBegin, point, strokeDir, assistant, out snapped, out alignment),
            _ => false,
        };
    }

    private static bool TrySnapRulerAxis(
        Point strokeBegin,
        Point point,
        Point strokeDir,
        Point a,
        Point b,
        out Point snapped,
        out double alignment)
    {
        snapped = point;
        alignment = 0;

        if (!TryGetUnitDirection(b, a, out var axisDir))
            return false;

        alignment = Math.Abs(axisDir.X * strokeDir.X + axisDir.Y * strokeDir.Y);
        if (alignment < AxisAlignThreshold)
            return false;

        snapped = ProjectOntoInfiniteLine(point, strokeBegin, axisDir);
        return true;
    }

    private static bool TrySnapPerspectiveAxis(
        Point strokeBegin,
        Point point,
        Point strokeDir,
        PaintingAssistant assistant,
        out Point snapped,
        out double alignment)
    {
        snapped = point;
        alignment = 0;

        if (!assistant.UsesFisheyeGrid
            && PerspectiveAssistantGeometry.TrySnapAlongCanonicalAxes(
                strokeBegin, point, strokeDir, assistant, out snapped, out alignment))
            return true;

        var segments = assistant.UsesFisheyeGrid
            ? FisheyeAssistantGeometry.SnapSegments(assistant)
            : PerspectiveAssistantGeometry.SnapSegments(assistant);

        Point best = point;
        var bestAlign = AxisAlignThreshold;
        var found = false;

        foreach (var (a, b) in segments)
        {
            if (!TryGetUnitDirection(b, a, out var axisDir))
                continue;

            var dot = Math.Abs(axisDir.X * strokeDir.X + axisDir.Y * strokeDir.Y);
            if (dot < bestAlign)
                continue;

            best = ProjectOntoInfiniteLine(point, strokeBegin, axisDir);
            bestAlign = dot;
            found = true;
        }

        if (!found)
            return false;

        snapped = best;
        alignment = bestAlign;
        return true;
    }

    private static Point SnapToPerspective(PaintingAssistant assistant, Point p)
    {
        Point best = p;
        var bestDist2 = double.MaxValue;
        foreach (var (a, b) in PerspectiveAssistantGeometry.SnapSegments(assistant))
        {
            var proj = ProjectOntoSegment(p, a, b);
            var d2 = DistanceSquared(p, proj);
            if (d2 < bestDist2)
            {
                bestDist2 = d2;
                best = proj;
            }
        }

        return best;
    }

    private static Point SnapToFisheye(PaintingAssistant assistant, Point p)
    {
        Point best = p;
        var bestDist2 = double.MaxValue;
        foreach (var (a, b) in FisheyeAssistantGeometry.SnapSegments(assistant))
        {
            var proj = ProjectOntoSegment(p, a, b);
            var d2 = DistanceSquared(p, proj);
            if (d2 < bestDist2)
            {
                bestDist2 = d2;
                best = proj;
            }
        }

        return best;
    }

    private static bool TryGetUnitStrokeDirection(Point strokeBegin, Point point, out Point strokeDir)
    {
        strokeDir = default;
        var dx = point.X - strokeBegin.X;
        var dy = point.Y - strokeBegin.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < MinStrokeLengthDoc)
            return false;

        strokeDir = new Point(dx / len, dy / len);
        return true;
    }

    private static bool TryGetUnitDirection(Point from, Point to, out Point dir)
    {
        dir = default;
        var dx = to.X - from.X;
        var dy = to.Y - from.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6)
            return false;

        dir = new Point(dx / len, dy / len);
        return true;
    }

    private static Point ProjectOntoInfiniteLine(Point p, Point lineOrigin, Point unitDir)
        => new(
            lineOrigin.X + unitDir.X * ((p.X - lineOrigin.X) * unitDir.X + (p.Y - lineOrigin.Y) * unitDir.Y),
            lineOrigin.Y + unitDir.Y * ((p.X - lineOrigin.X) * unitDir.X + (p.Y - lineOrigin.Y) * unitDir.Y));

    private static Point ProjectOntoSegment(Point p, Point a, Point b)
    {
        var abx = b.X - a.X;
        var aby = b.Y - a.Y;
        var len2 = abx * abx + aby * aby;
        if (len2 < 1e-6)
            return a;

        var t = Math.Clamp(((p.X - a.X) * abx + (p.Y - a.Y) * aby) / len2, 0, 1);
        return new Point(a.X + abx * t, a.Y + aby * t);
    }

    private static double DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    public static Point DocumentToCanvas(
        Point document,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight)
    {
        var w = Math.Max(1, canvasWidth);
        var h = Math.Max(1, canvasHeight);
        return new Point(
            document.X / Math.Max(1, documentWidth) * w,
            document.Y / Math.Max(1, documentHeight) * h);
    }

    public static Point CanvasToDocument(
        Point canvas,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight)
    {
        var w = Math.Max(1, canvasWidth);
        var h = Math.Max(1, canvasHeight);
        return new Point(
            canvas.X / w * documentWidth,
            canvas.Y / h * documentHeight);
    }
}
