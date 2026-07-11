using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Media;

namespace Floss.App.Document.Assistants;

/// <summary>Draws object-layer rulers below the active tool overlay.</summary>
public static class AssistantsRenderer
{
    // Soft CSP-like plane colors (lower saturation / alpha applied via pens).
    private static readonly Color PlaneXColor = Color.Parse("#c45c5c");
    private static readonly Color PlaneYColor = Color.Parse("#4fa86a");
    private static readonly Color PlaneZColor = Color.Parse("#5a7ec4");

    public static void Render(
        DrawingContext dc,
        DocumentAssistants assistants,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight,
        double zoom,
        PaintingAssistant? preview = null)
    {
        foreach (var (layer, assistant) in assistants.EnumerateForRender())
        {
            DrawAssistant(dc, assistant, documentWidth, documentHeight, canvasWidth, canvasHeight, zoom,
                assistant.Id == assistants.SelectedId, layerOpacity: layer.Opacity, gridsOnCanvas: false);
        }

        if (preview != null)
        {
            DrawAssistant(dc, preview, documentWidth, documentHeight, canvasWidth, canvasHeight, zoom,
                selected: true, preview: true, layerOpacity: 1, gridsOnCanvas: false);
        }
    }

    /// <summary>
    /// Draws perspective/fisheye grids in viewport (screen) space so lines extend past the canvas.
    /// </summary>
    public static void RenderPerspectiveGridsInViewport(
        DrawingContext dc,
        DocumentAssistants assistants,
        Rect visibleDocumentRect,
        double zoom,
        Func<Point, Point?> documentToViewport,
        PaintingAssistant? preview = null)
    {
        foreach (var (layer, assistant) in assistants.EnumerateForRender())
        {
            if (assistant.TypeId is not (PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType))
                continue;

            DrawPerspectiveGridViewport(
                dc, assistant, visibleDocumentRect, zoom, documentToViewport,
                assistant.Id == assistants.SelectedId, layer.Opacity);
        }

        if (preview != null && preview.TypeId is PaintingAssistant.PerspectiveType or PaintingAssistant.FisheyeType)
        {
            DrawPerspectiveGridViewport(
                dc, preview, visibleDocumentRect, zoom, documentToViewport,
                selected: true, layerOpacity: 1);
        }
    }

    private static void DrawAssistant(
        DrawingContext dc,
        PaintingAssistant assistant,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight,
        double zoom,
        bool selected,
        bool preview = false,
        double layerOpacity = 1,
        bool gridsOnCanvas = true)
    {
        switch (assistant.TypeId)
        {
            case PaintingAssistant.PerspectiveType:
            case PaintingAssistant.FisheyeType:
                // Grids are drawn on the viewport overlay; canvas only shows handles.
                if (gridsOnCanvas)
                {
                    if (assistant.UsesFisheyeGrid)
                        DrawFisheye(dc, assistant, documentWidth, documentHeight, canvasWidth, canvasHeight, zoom, selected, preview, layerOpacity);
                    else
                        DrawPerspective(dc, assistant, documentWidth, documentHeight, canvasWidth, canvasHeight, zoom, selected, preview, layerOpacity);
                }
                else
                {
                    Point ToCanvas(Point doc) =>
                        AssistantSnap.DocumentToCanvas(doc, documentWidth, documentHeight, canvasWidth, canvasHeight);
                    var pen = MakePen(selected, preview, zoom, layerOpacity);
                    DrawPerspectiveHandles(dc, assistant, pen, selected, zoom, ToCanvas);
                }
                break;
            default:
                DrawRuler(dc, assistant.HandleA, assistant.HandleB, documentWidth, documentHeight,
                    canvasWidth, canvasHeight, zoom, selected, preview, layerOpacity);
                break;
        }
    }

    private static void DrawPerspectiveGridViewport(
        DrawingContext dc,
        PaintingAssistant assistant,
        Rect visibleDocumentRect,
        double zoom,
        Func<Point, Point?> documentToViewport,
        bool selected,
        double layerOpacity)
    {
        _ = selected;
        IEnumerable<PerspectiveGridGeometry.GridCurve> curves = assistant.UsesFisheyeGrid
            ? FisheyeAssistantGeometry.EnumerateWarpedGridCurves(assistant, visibleDocumentRect, zoom)
            : PerspectiveGridGeometry.EnumerateCurves(assistant, visibleDocumentRect, zoom);

        foreach (var curve in curves)
        {
            var pen = curve.IsPrimary
                ? MakePrimaryPlanePen(PlaneColor(curve.Plane), zoom, layerOpacity)
                : PlanePen(curve.Plane, zoom, layerOpacity, primary: false);
            DrawPolylineViewport(dc, pen, curve.Points, documentToViewport);
        }

        if (assistant.UsesFisheyeGrid)
        {
            var lensPen = new Pen(
                new SolidColorBrush(ApplyOpacity(Color.Parse("#6688cc"), layerOpacity * 0.35)),
                Math.Max(0.75, 1.0));
            foreach (var (a, b) in FisheyeAssistantGeometry.BoundarySegments(assistant, visibleDocumentRect))
            {
                var sa = documentToViewport(a);
                var sb = documentToViewport(b);
                if (sa is { } p0 && sb is { } p1)
                    dc.DrawLine(lensPen, p0, p1);
            }

            var frame = FisheyeAssistantGeometry.GetLensFrame(assistant, visibleDocumentRect);
            if (documentToViewport(frame.Center) is { } center)
                DrawVanishingPointMarkerScreen(dc, center, ApplyOpacity(Color.Parse("#a888d4"), layerOpacity * 0.7));
        }

        DrawVanishingPointMarkersViewport(dc, assistant, documentToViewport, layerOpacity);
    }

    private static void DrawPerspective(
        DrawingContext dc,
        PaintingAssistant assistant,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight,
        double zoom,
        bool selected,
        bool preview,
        double layerOpacity)
    {
        var pen = MakePen(selected, preview, zoom, layerOpacity);
        Point ToCanvas(Point doc) =>
            AssistantSnap.DocumentToCanvas(doc, documentWidth, documentHeight, canvasWidth, canvasHeight);

        foreach (var curve in PerspectiveGridGeometry.EnumerateCurves(assistant))
        {
            var planePen = curve.IsPrimary
                ? MakePrimaryPlanePen(PlaneColor(curve.Plane), zoom, layerOpacity)
                : PlanePen(curve.Plane, zoom, layerOpacity, primary: false);
            DrawPolyline(dc, planePen, curve.Points, ToCanvas);
        }

        DrawVanishingPointMarkers(dc, assistant, zoom, ToCanvas, layerOpacity);
        DrawPerspectiveHandles(dc, assistant, pen, selected, zoom, ToCanvas);
    }

    private static void DrawFisheye(
        DrawingContext dc,
        PaintingAssistant assistant,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight,
        double zoom,
        bool selected,
        bool preview,
        double layerOpacity)
    {
        var pen = MakePen(selected, preview, zoom, layerOpacity);
        var lensPen = new Pen(
            new SolidColorBrush(ApplyOpacity(Color.Parse("#6688cc"), layerOpacity * 0.4)),
            Math.Max(0.75, 1.25 / Math.Max(zoom, 0.001)));

        Point ToCanvas(Point doc) =>
            AssistantSnap.DocumentToCanvas(doc, documentWidth, documentHeight, canvasWidth, canvasHeight);

        foreach (var curve in FisheyeAssistantGeometry.EnumerateWarpedGridCurves(assistant))
        {
            var planePen = curve.IsPrimary
                ? MakePrimaryPlanePen(PlaneColor(curve.Plane), zoom, layerOpacity)
                : PlanePen(curve.Plane, zoom, layerOpacity, primary: false);
            DrawPolyline(dc, planePen, curve.Points, ToCanvas);
        }

        foreach (var (a, b) in FisheyeAssistantGeometry.BoundarySegments(assistant))
            dc.DrawLine(lensPen, ToCanvas(a), ToCanvas(b));

        var frame = FisheyeAssistantGeometry.GetLensFrame(assistant);
        DrawVanishingPointMarker(dc, ToCanvas(frame.Center), zoom, ApplyOpacity(Color.Parse("#a888d4"), layerOpacity * 0.7));
        DrawVanishingPointMarkers(dc, assistant, zoom, ToCanvas, layerOpacity);
        DrawPerspectiveHandles(dc, assistant, pen, selected, zoom, ToCanvas);
    }

    private static void DrawVanishingPointMarkers(
        DrawingContext dc,
        PaintingAssistant assistant,
        double zoom,
        Func<Point, Point> toCanvas,
        double layerOpacity)
    {
        DrawVanishingPointMarker(dc, toCanvas(assistant.HandleA), zoom, ApplyOpacity(PlaneColor(1), layerOpacity * 0.85));
        DrawVanishingPointMarker(dc, toCanvas(assistant.HandleB), zoom, ApplyOpacity(PlaneColor(2), layerOpacity * 0.85));
        if (assistant.HandleCount > 2)
            DrawVanishingPointMarker(dc, toCanvas(assistant.HandleC), zoom, ApplyOpacity(PlaneColor(0), layerOpacity * 0.85));
    }

    private static void DrawVanishingPointMarkersViewport(
        DrawingContext dc,
        PaintingAssistant assistant,
        Func<Point, Point?> documentToViewport,
        double layerOpacity)
    {
        void Draw(Point doc, Color color)
        {
            if (documentToViewport(doc) is { } p)
                DrawVanishingPointMarkerScreen(dc, p, color);
        }

        Draw(assistant.HandleA, ApplyOpacity(PlaneColor(1), layerOpacity * 0.85));
        Draw(assistant.HandleB, ApplyOpacity(PlaneColor(2), layerOpacity * 0.85));
        if (assistant.HandleCount > 2)
            Draw(assistant.HandleC, ApplyOpacity(PlaneColor(0), layerOpacity * 0.85));
    }

    private static void DrawVanishingPointMarker(DrawingContext dc, Point p, double zoom, Color color)
    {
        var arm = 5 / Math.Max(zoom, 0.001);
        var pen = new Pen(new SolidColorBrush(color), Math.Max(0.75, 1.0 / Math.Max(zoom, 0.001)));
        dc.DrawLine(pen, new Point(p.X - arm, p.Y), new Point(p.X + arm, p.Y));
        dc.DrawLine(pen, new Point(p.X, p.Y - arm), new Point(p.X, p.Y + arm));
        dc.DrawEllipse(new SolidColorBrush(color), null, new Rect(p.X - arm * 0.35, p.Y - arm * 0.35, arm * 0.7, arm * 0.7));
    }

    private static void DrawVanishingPointMarkerScreen(DrawingContext dc, Point p, Color color)
    {
        const double arm = 5;
        var pen = new Pen(new SolidColorBrush(color), 1.0);
        dc.DrawLine(pen, new Point(p.X - arm, p.Y), new Point(p.X + arm, p.Y));
        dc.DrawLine(pen, new Point(p.X, p.Y - arm), new Point(p.X, p.Y + arm));
        dc.DrawEllipse(new SolidColorBrush(color), null, new Rect(p.X - 1.5, p.Y - 1.5, 3, 3));
    }

    private static Color PlaneColor(int plane) => plane switch
    {
        1 => PlaneYColor,
        2 => PlaneZColor,
        _ => PlaneXColor,
    };

    private static void DrawPerspectiveHandles(
        DrawingContext dc,
        PaintingAssistant assistant,
        Pen pen,
        bool selected,
        double zoom,
        Func<Point, Point> toCanvas)
    {
        DrawHandle(dc, toCanvas(assistant.HandleA), pen, selected, zoom);
        DrawHandle(dc, toCanvas(assistant.HandleB), pen, selected, zoom);
        if (assistant.HandleCount > 2)
            DrawHandle(dc, toCanvas(assistant.HandleC), pen, selected, zoom);
        if (assistant.HandleCount > 3)
            DrawHandle(dc, toCanvas(assistant.HandleD), pen, selected, zoom);
    }

    private static Pen PlanePen(int plane, double zoom, double layerOpacity, bool primary)
        => primary
            ? MakePrimaryPlanePen(PlaneColor(plane), zoom, layerOpacity)
            : MakePlanePen(PlaneColor(plane), zoom, layerOpacity);

    private static void DrawRuler(
        DrawingContext dc,
        Point docA,
        Point docB,
        int documentWidth,
        int documentHeight,
        double canvasWidth,
        double canvasHeight,
        double zoom,
        bool selected,
        bool preview,
        double layerOpacity)
    {
        var a = AssistantSnap.DocumentToCanvas(docA, documentWidth, documentHeight, canvasWidth, canvasHeight);
        var b = AssistantSnap.DocumentToCanvas(docB, documentWidth, documentHeight, canvasWidth, canvasHeight);
        var pen = MakePen(selected, preview, zoom, layerOpacity);
        var dash = new Pen(new SolidColorBrush(ApplyOpacity(Color.Parse("#303848"), layerOpacity)), pen.Thickness)
        {
            DashStyle = new DashStyle([4 / Math.Max(zoom, 0.001), 4 / Math.Max(zoom, 0.001)], 0),
        };

        dc.DrawLine(dash, a, b);
        dc.DrawLine(pen, a, b);
        DrawHandle(dc, a, pen, selected, zoom);
        DrawHandle(dc, b, pen, selected, zoom);

        var dx = docB.X - docA.X;
        var dy = docB.Y - docA.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 1)
            return;

        DrawLabel(dc, $"{dist:0.##} px", new Point((a.X + b.X) * 0.5, (a.Y + b.Y) * 0.5), zoom, layerOpacity);
    }

    private static Pen MakePen(bool selected, bool preview, double zoom, double layerOpacity)
    {
        var stroke = Math.Max(1.0, 1.5 / Math.Max(zoom, 0.001));
        var color = preview ? "#88ccff" : selected ? "#ffcc44" : "#c8d0e0";
        return new Pen(new SolidColorBrush(ApplyOpacity(Color.Parse(color), layerOpacity)), stroke);
    }

    private static Pen MakePlanePen(Color color, double zoom, double layerOpacity)
    {
        // Screen-constant thin strokes with soft opacity.
        var stroke = Math.Max(0.75, 0.9);
        return new Pen(new SolidColorBrush(ApplyOpacity(color, layerOpacity * 0.42)), stroke);
    }

    private static Pen MakePrimaryPlanePen(Color color, double zoom, double layerOpacity)
    {
        var stroke = Math.Max(1.0, 1.15);
        return new Pen(new SolidColorBrush(ApplyOpacity(color, layerOpacity * 0.72)), stroke);
    }

    private static Color ApplyOpacity(Color color, double layerOpacity)
    {
        var scale = Math.Clamp(layerOpacity, 0, 1);
        return Color.FromArgb((byte)Math.Round(color.A * scale), color.R, color.G, color.B);
    }

    private static void DrawPolyline(
        DrawingContext dc,
        Pen pen,
        IReadOnlyList<Point> documentPoints,
        Func<Point, Point> toCanvas)
    {
        if (documentPoints.Count < 2)
            return;

        for (var i = 1; i < documentPoints.Count; i++)
            dc.DrawLine(pen, toCanvas(documentPoints[i - 1]), toCanvas(documentPoints[i]));
    }

    private static void DrawPolylineViewport(
        DrawingContext dc,
        Pen pen,
        IReadOnlyList<Point> documentPoints,
        Func<Point, Point?> documentToViewport)
    {
        if (documentPoints.Count < 2)
            return;

        Point? prev = null;
        for (var i = 0; i < documentPoints.Count; i++)
        {
            var cur = documentToViewport(documentPoints[i]);
            if (prev is { } a && cur is { } b)
                dc.DrawLine(pen, a, b);
            prev = cur;
        }
    }

    private static void DrawHandle(DrawingContext dc, Point p, Pen pen, bool selected, double zoom)
    {
        var radius = 5 / Math.Max(zoom, 0.001);
        var handlePen = new Pen(Avalonia.Media.Brushes.White, pen.Thickness);
        dc.DrawEllipse(selected ? pen.Brush : Avalonia.Media.Brushes.White, handlePen,
            new Rect(p.X - radius, p.Y - radius, radius * 2, radius * 2));
    }

    private static void DrawLabel(DrawingContext dc, string label, Point mid, double zoom, double layerOpacity)
    {
        var fontSize = 11 / Math.Max(zoom, 0.001);
        var fg = new SolidColorBrush(ApplyOpacity(Colors.White, layerOpacity));
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            Typeface.Default, fontSize, fg);
        var pad = 3 / Math.Max(zoom, 0.001);
        var bg = new Rect(mid.X - ft.Width * 0.5 - pad, mid.Y - ft.Height - pad * 2, ft.Width + pad * 2, ft.Height + pad * 2);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb((byte)Math.Round(180 * layerOpacity), 16, 20, 28)), null, bg);
        dc.DrawText(ft, new Point(bg.X + pad, bg.Y + pad));
    }
}
