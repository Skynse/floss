using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;

namespace Floss.App.Document.Assistants;

internal static class PerspectiveAssistantGeometry
{
    private const double AxisAlignThreshold = 0.92;

    public static double ParallelLineSpacing(PaintingAssistant assistant)
        => PerspectiveGridGeometry.ParallelLineSpacing(assistant);

    public static bool TrySnapAlongCanonicalAxes(
        Point strokeBegin,
        Point point,
        Point strokeDir,
        PaintingAssistant assistant,
        out Point snapped,
        out double alignment)
    {
        snapped = point;
        alignment = 0;

        if (assistant.TypeId != PaintingAssistant.PerspectiveType)
            return false;

        var best = point;
        var bestAlign = AxisAlignThreshold;
        var found = false;

        switch (assistant.PerspectiveMode)
        {
            case PerspectiveAssistantMode.OnePoint:
                TryAlignRadial(strokeBegin, point, strokeDir, assistant.HandleA, ref best, ref bestAlign, ref found);
                TryAlignParallel(strokeBegin, point, strokeDir, assistant.HandleA, assistant.HandleB, ref best, ref bestAlign, ref found);
                break;

            case PerspectiveAssistantMode.TwoPoint:
                TryAlignRadial(strokeBegin, point, strokeDir, assistant.HandleA, ref best, ref bestAlign, ref found);
                TryAlignRadial(strokeBegin, point, strokeDir, assistant.HandleB, ref best, ref bestAlign, ref found);
                TryAlignParallel(strokeBegin, point, strokeDir, assistant.HandleA, assistant.HandleB, ref best, ref bestAlign, ref found);
                break;

            case PerspectiveAssistantMode.ThreePoint:
                TryAlignRadial(strokeBegin, point, strokeDir, assistant.HandleA, ref best, ref bestAlign, ref found);
                TryAlignRadial(strokeBegin, point, strokeDir, assistant.HandleB, ref best, ref bestAlign, ref found);
                TryAlignRadial(strokeBegin, point, strokeDir, assistant.HandleC, ref best, ref bestAlign, ref found);
                TryAlignParallel(strokeBegin, point, strokeDir, assistant.HandleA, assistant.HandleB, ref best, ref bestAlign, ref found);
                break;

            default:
                return false;
        }

        if (!found)
            return false;

        snapped = best;
        alignment = bestAlign;
        return true;
    }

    public static IEnumerable<(Point A, Point B)> SnapSegments(PaintingAssistant assistant)
    {
        if (assistant.TypeId != PaintingAssistant.PerspectiveType)
            yield break;

        foreach (var seg in PerspectiveGridGeometry.EnumerateSegments(assistant))
            yield return seg;
    }

    private static void TryAlignRadial(
        Point strokeBegin,
        Point point,
        Point strokeDir,
        Point vanishingPoint,
        ref Point best,
        ref double bestAlign,
        ref bool found)
    {
        var refPoint = DistanceSquared(strokeBegin, vanishingPoint) < 1 ? point : strokeBegin;
        var dx = refPoint.X - vanishingPoint.X;
        var dy = refPoint.Y - vanishingPoint.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6)
            return;

        var radialDir = new Point(dx / len, dy / len);
        var dot = Math.Abs(radialDir.X * strokeDir.X + radialDir.Y * strokeDir.Y);
        if (dot < bestAlign)
            return;

        best = ProjectOntoInfiniteLine(point, strokeBegin, radialDir);
        bestAlign = dot;
        found = true;
    }

    private static void TryAlignParallel(
        Point strokeBegin,
        Point point,
        Point strokeDir,
        Point a,
        Point b,
        ref Point best,
        ref double bestAlign,
        ref bool found)
    {
        var dx = b.X - a.X;
        var dy = b.Y - a.Y;
        var len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-6)
            return;

        var parallelDir = new Point(dx / len, dy / len);
        var dot = Math.Abs(parallelDir.X * strokeDir.X + parallelDir.Y * strokeDir.Y);
        if (dot < bestAlign)
            return;

        best = ProjectOntoInfiniteLine(point, strokeBegin, parallelDir);
        bestAlign = dot;
        found = true;
    }

    private static Point ProjectOntoInfiniteLine(Point p, Point lineOrigin, Point unitDir)
        => new(
            lineOrigin.X + unitDir.X * ((p.X - lineOrigin.X) * unitDir.X + (p.Y - lineOrigin.Y) * unitDir.Y),
            lineOrigin.Y + unitDir.Y * ((p.X - lineOrigin.X) * unitDir.X + (p.Y - lineOrigin.Y) * unitDir.Y));

    private static double DistanceSquared(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

}
