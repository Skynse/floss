using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Document;
using SkiaSharp;

namespace Floss.App.Tools;

public enum SelectOp { Replace, Add, Subtract }

// Global selection state — null mask means "everything selected".
// Stored in ToolContext and inspected by brush, fill, gradient, etc.
public sealed class SelectionMask
{
    private byte[]? _mask;       // null = nothing restricted (all pixels writable)
    private int _docW, _docH;

    // The geometry used to make the last selection (for rendering the outline).
    private SKRectI _geoRect;
    private List<SKPoint> _geoPoly = [];
    private SelectionGeometry _geoType = SelectionGeometry.None;
    private StreamGeometry? _cachedMaskGeo;

    private enum SelectionGeometry { None, Rect, Polygon, Mask }

    public bool HasSelection => _mask != null;

    public void Resize(int w, int h)
    {
        if (_docW == w && _docH == h) return;
        _docW = w; _docH = h;
        Clear();
    }

    public void Clear()
    {
        _mask = null;
        _geoType = SelectionGeometry.None;
        _geoPoly.Clear();
        _cachedMaskGeo = null;
    }

    public bool IsSelected(int x, int y)
    {
        if (_mask == null) return true;
        if ((uint)x >= (uint)_docW || (uint)y >= (uint)_docH) return false;
        return _mask[y * _docW + x] > 0;
    }

    public SKRectI? GetMaskBounds()
    {
        if (_mask == null) return null;
        int minX = _docW, minY = _docH, maxX = -1, maxY = -1;
        for (int y = 0; y < _docH; y++)
            for (int x = 0; x < _docW; x++)
                if (_mask[y * _docW + x] > 0)
                {
                    if (x < minX) minX = x;
                    if (y < minY) minY = y;
                    if (x > maxX) maxX = x;
                    if (y > maxY) maxY = y;
                }
        return maxX >= minX ? new SKRectI(minX, minY, maxX + 1, maxY + 1) : null;
    }

    public void Invert()
    {
        if (_mask == null)
        {
            _mask = new byte[_docW * _docH]; // all zeros = nothing selected
        }
        else
        {
            for (int i = 0; i < _mask.Length; i++)
                _mask[i] = _mask[i] > 0 ? (byte)0 : (byte)255;
        }
        _geoType = SelectionGeometry.None;
        _cachedMaskGeo = null;
    }

    public void SetFromRect(int x, int y, int w, int h, SelectOp op = SelectOp.Replace)
    {
        EnsureMaskExists();
        var next = CreateBaseMask(op);

        int x1 = Math.Clamp(Math.Min(x, x + w), 0, _docW);
        int y1 = Math.Clamp(Math.Min(y, y + h), 0, _docH);
        int x2 = Math.Clamp(Math.Max(x, x + w), 0, _docW);
        int y2 = Math.Clamp(Math.Max(y, y + h), 0, _docH);

        for (int py = y1; py < y2; py++)
            for (int px = x1; px < x2; px++)
                Apply(next, px, py, op, true);

        CommitMask(next);
        _geoType = SelectionGeometry.Rect;
        _geoRect = new SKRectI(x1, y1, x2, y2);
        _cachedMaskGeo = null;
    }

    public void SetFromPolygon(IReadOnlyList<SKPoint> points, SelectOp op = SelectOp.Replace)
    {
        if (points.Count < 3) return;
        EnsureMaskExists();

        using var path = new SKPath();
        path.MoveTo(points[0]);
        for (int i = 1; i < points.Count; i++) path.LineTo(points[i]);
        path.Close();

        using var region = new SKRegion();
        region.SetPath(path, new SKRegion(new SKRectI(0, 0, _docW, _docH)));

        var bounds = path.Bounds;
        int x1 = Math.Clamp((int)bounds.Left,         0, _docW - 1);
        int y1 = Math.Clamp((int)bounds.Top,          0, _docH - 1);
        int x2 = Math.Clamp((int)Math.Ceiling(bounds.Right),  0, _docW - 1);
        int y2 = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, _docH - 1);

        var next = CreateBaseMask(op);
        for (int py = y1; py <= y2; py++)
            for (int px = x1; px <= x2; px++)
                Apply(next, px, py, op, region.Contains(px, py));

        CommitMask(next);
        _geoType = SelectionGeometry.Polygon;
        _geoPoly = new List<SKPoint>(points);
        _cachedMaskGeo = null;
    }

    public void SetFromFloodFill(TiledPixelBuffer pixels, int srcX, int srcY,
        double tolerance, SelectOp op = SelectOp.Replace)
        => SetFromFloodFill(pixels, srcX, srcY, 0, 0, tolerance, op);

    public void SetFromFloodFill(
        TiledPixelBuffer pixels,
        int srcX,
        int srcY,
        int offsetX,
        int offsetY,
        double tolerance,
        SelectOp op = SelectOp.Replace)
    {
        EnsureMaskExists();

        pixels.GetPixel(srcX, srcY, out byte refB, out byte refG, out byte refR, out byte refA);
        int tolInt = (int)(tolerance * 255 * 4);

        var next = CreateBaseMask(op);
        var visited = new HashSet<(int, int)>();
        var queue = new Queue<(int x, int y)>();
        queue.Enqueue((srcX, srcY));
        visited.Add((srcX, srcY));

        while (queue.Count > 0)
        {
            var (cx, cy) = queue.Dequeue();

            pixels.GetPixel(cx, cy, out byte b, out byte g, out byte r, out byte a);
            if (Math.Abs(b - refB) + Math.Abs(g - refG) + Math.Abs(r - refR) + Math.Abs(a - refA) > tolInt) continue;

            var docX = cx + offsetX;
            var docY = cy + offsetY;
            if ((uint)docX < (uint)_docW && (uint)docY < (uint)_docH)
                Apply(next, docX, docY, op, true);

            if (visited.Add((cx + 1, cy))) queue.Enqueue((cx + 1, cy));
            if (visited.Add((cx - 1, cy))) queue.Enqueue((cx - 1, cy));
            if (visited.Add((cx, cy + 1))) queue.Enqueue((cx, cy + 1));
            if (visited.Add((cx, cy - 1))) queue.Enqueue((cx, cy - 1));
        }

        CommitMask(next);
        _geoType = SelectionGeometry.Mask;
        _cachedMaskGeo = null;
        _geoPoly.Clear();
    }

    // Render the committed selection outline as marching-ants double-dash.
    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (!HasSelection || _geoType == SelectionGeometry.None) return;

        var t = Math.Max(0.5, 1.0 / zoom);
        var dash1 = new DashStyle([4, 4], 0);
        var dash2 = new DashStyle([4, 4], 4);
        var penW = new Pen(Avalonia.Media.Brushes.White, t, dash1);
        var penK = new Pen(Avalonia.Media.Brushes.Black, t, dash2);

        if (_geoType == SelectionGeometry.Rect)
        {
            var r = new Avalonia.Rect(_geoRect.Left, _geoRect.Top, _geoRect.Width, _geoRect.Height);
            dc.DrawRectangle(null, penW, r);
            dc.DrawRectangle(null, penK, r);
        }
        else if (_geoType == SelectionGeometry.Polygon && _geoPoly.Count >= 2)
        {
            var geo = new StreamGeometry();
            using (var c = geo.Open())
            {
                c.BeginFigure(new Avalonia.Point(_geoPoly[0].X, _geoPoly[0].Y), true);
                for (int i = 1; i < _geoPoly.Count; i++)
                    c.LineTo(new Avalonia.Point(_geoPoly[i].X, _geoPoly[i].Y));
                c.EndFigure(true);
            }
            dc.DrawGeometry(null, penW, geo);
            dc.DrawGeometry(null, penK, geo);
        }
        else if (_geoType == SelectionGeometry.Mask)
        {
            var geo = _cachedMaskGeo ??= BuildMaskOutline();
            if (geo != null)
            {
                dc.DrawGeometry(null, penW, geo);
                dc.DrawGeometry(null, penK, geo);
            }
        }
    }

    // Build a StreamGeometry tracing every boundary edge between selected and unselected pixels.
    // Edges are 1-pixel-wide segments; horizontal edges run along top/bottom of a row,
    // vertical edges along left/right of a column. Consecutive collinear edges are merged.
    private StreamGeometry? BuildMaskOutline()
    {
        if (_mask == null) return null;
        var bounds = GetMaskBounds();
        if (bounds == null) return null;
        var b = bounds.Value;

        // Collect all boundary segments as axis-aligned lines, then merge collinear runs.
        // Key: (isHorizontal, fixedCoord, minVar, maxVar)
        // For horizontal edges: fixedCoord = y row edge (pixel y or y+1), x range [x, x+1]
        // For vertical edges:   fixedCoord = x col edge (pixel x or x+1), y range [y, y+1]

        bool Selected(int x, int y) =>
            (uint)x < (uint)_docW && (uint)y < (uint)_docH && _mask[y * _docW + x] > 0;

        // Horizontal edge segments grouped by y-edge position, then by x
        var hEdges = new Dictionary<int, List<int>>(); // y-edge → list of x starts
        // Vertical edge segments grouped by x-edge position, then by y
        var vEdges = new Dictionary<int, List<int>>(); // x-edge → list of y starts

        for (int y = b.Top; y < b.Bottom; y++)
        {
            for (int x = b.Left; x < b.Right; x++)
            {
                if (!Selected(x, y)) continue;

                // Top edge: between (x,y-1) and (x,y)
                if (!Selected(x, y - 1))
                {
                    if (!hEdges.TryGetValue(y, out var list)) hEdges[y] = list = [];
                    list.Add(x);
                }
                // Bottom edge: between (x,y) and (x,y+1)
                if (!Selected(x, y + 1))
                {
                    if (!hEdges.TryGetValue(y + 1, out var list)) hEdges[y + 1] = list = [];
                    list.Add(x);
                }
                // Left edge: between (x-1,y) and (x,y)
                if (!Selected(x - 1, y))
                {
                    if (!vEdges.TryGetValue(x, out var list)) vEdges[x] = list = [];
                    list.Add(y);
                }
                // Right edge: between (x,y) and (x+1,y)
                if (!Selected(x + 1, y))
                {
                    if (!vEdges.TryGetValue(x + 1, out var list)) vEdges[x + 1] = list = [];
                    list.Add(y);
                }
            }
        }

        var geo = new StreamGeometry();
        using var ctx = geo.Open();

        // Merge horizontal runs and emit
        foreach (var (fy, xs) in hEdges)
        {
            xs.Sort();
            int start = xs[0], prev = xs[0];
            for (int i = 1; i < xs.Count; i++)
            {
                if (xs[i] == prev + 1) { prev = xs[i]; continue; }
                ctx.BeginFigure(new Avalonia.Point(start, fy), false);
                ctx.LineTo(new Avalonia.Point(prev + 1, fy));
                ctx.EndFigure(false);
                start = xs[i]; prev = xs[i];
            }
            ctx.BeginFigure(new Avalonia.Point(start, fy), false);
            ctx.LineTo(new Avalonia.Point(prev + 1, fy));
            ctx.EndFigure(false);
        }

        // Merge vertical runs and emit
        foreach (var (fx, ys) in vEdges)
        {
            ys.Sort();
            int start = ys[0], prev = ys[0];
            for (int i = 1; i < ys.Count; i++)
            {
                if (ys[i] == prev + 1) { prev = ys[i]; continue; }
                ctx.BeginFigure(new Avalonia.Point(fx, start), false);
                ctx.LineTo(new Avalonia.Point(fx, prev + 1));
                ctx.EndFigure(false);
                start = ys[i]; prev = ys[i];
            }
            ctx.BeginFigure(new Avalonia.Point(fx, start), false);
            ctx.LineTo(new Avalonia.Point(fx, prev + 1));
            ctx.EndFigure(false);
        }

        return geo;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private void EnsureMaskExists()
    {
        if (_docW == 0 || _docH == 0)
            throw new InvalidOperationException("Call Resize() before selection operations.");
    }

    private byte[] CreateBaseMask(SelectOp op)
    {
        if (op != SelectOp.Replace && _mask != null)
            return (byte[])_mask.Clone();

        return new byte[_docW * _docH];
    }

    private void CommitMask(byte[] mask)
    {
        for (var i = 0; i < mask.Length; i++)
        {
            if (mask[i] == 0) continue;
            _mask = mask;
            return;
        }

        Clear();
    }

    private void Apply(byte[] mask, int x, int y, SelectOp op, bool inside)
    {
        int idx = y * _docW + x;
        bool cur = mask[idx] > 0;
        mask[idx] = (op switch
        {
            SelectOp.Add      => cur || inside,
            SelectOp.Subtract => cur && !inside,
            _                 => inside,
        }) ? (byte)255 : (byte)0;
    }
}
