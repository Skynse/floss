using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using Floss.App.Canvas.FloodFill;
using Floss.App.Document;
using SkiaSharp;

namespace Floss.App.Tools;

public enum SelectOp { Replace, Add, Subtract, Intersect }

// Global selection state — null mask means "everything selected".
// Stored in ToolContext and inspected by brush, fill, gradient, etc.
public sealed class SelectionMask
{
    public sealed record Snapshot(
        int Width,
        int Height,
        byte[]? Mask,
        string GeometryType,
        SKRectI GeometryRect,
        SKPoint[] GeometryPolygon);

    private byte[]? _mask;       // null = nothing restricted (all pixels writable)
    private int _docW, _docH;
    private SKRectI? _maskBounds;
    private int _selectedCount;
    private bool _simplifyOutline;

    private readonly VisitEpoch _visit = new();

    private const int SimplifyOutlineSelectedPixels = 400_000;
    private const int MaxOutlineSegments = 12_000;

    // The geometry used to make the last selection (for rendering the outline).
    private SKRectI _geoRect;
    private List<SKPoint> _geoPoly = [];
    private SelectionGeometry _geoType = SelectionGeometry.None;
    private readonly List<object> _outlineParts = []; // SKRectI or SKPoint[]
    private Geometry? _cachedOutlineGeo;
    private bool _outlineDirty;
    private StreamGeometry? _cachedMaskGeo;

    private enum SelectionGeometry { None, Rect, Polygon, Mask }

    private void AddOutlinePart(object shape)
    {
        _outlineParts.Add(shape);
        _outlineDirty = true;
    }

    private void SetOutlinePart(object shape)
    {
        _outlineParts.Clear();
        _outlineParts.Add(shape);
        _outlineDirty = true;
    }

    private void ClearOutline()
    {
        _outlineParts.Clear();
        _cachedOutlineGeo = null;
        _outlineDirty = false;
    }

    private Geometry? GetOutlineGeometry()
    {
        if (_outlineParts.Count == 0) return null;
        if (!_outlineDirty && _cachedOutlineGeo != null) return _cachedOutlineGeo;

        Geometry? result = null;
        foreach (var part in _outlineParts)
        {
            Geometry? geo = null;
            if (part is SKRectI r && r.Width > 0)
                geo = new RectangleGeometry(new Avalonia.Rect(r.Left, r.Top, r.Width, r.Height));
            else if (part is SKPoint[] pts && pts.Length >= 3)
                geo = BuildOutlinePoly(pts);

            if (geo == null) continue;
            result = result == null ? geo : new CombinedGeometry(GeometryCombineMode.Union, result, geo);
        }

        _cachedOutlineGeo = result;
        _outlineDirty = false;
        return result;
    }

    private static StreamGeometry BuildOutlinePoly(SKPoint[] pts)
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Avalonia.Point(pts[0].X, pts[0].Y), true);
            for (int i = 1; i < pts.Length; i++)
                c.LineTo(new Avalonia.Point(pts[i].X, pts[i].Y));
            c.EndFigure(true);
        }
        return geo;
    }

    public bool HasSelection => _mask != null;

    internal string OutlineGeometryKindForTests => _geoType.ToString();

    internal SKRectI GeometryRectForTests => _geoRect;

    internal bool TryGetAlphaTexture(out SKImage? image, out SKRectI bounds, out int texScale)
    {
        image = null;
        bounds = default;
        texScale = 1;
        if (_mask == null || _maskBounds is not { } b || b.Width <= 0 || b.Height <= 0)
            return false;

        if (_alphaTexture != null && _alphaTextureBounds == b)
        {
            image = _alphaTexture;
            bounds = b;
            texScale = _alphaTextureScale;
            return true;
        }

        ReleaseAlphaTexture();
        if (!TryBuildAlphaTexture(b, out _alphaTexture, out texScale))
            return false;

        _alphaTextureBounds = b;
        _alphaTextureScale = texScale;
        image = _alphaTexture;
        bounds = b;
        return true;
    }

    private SKImage? _alphaTexture;
    private SKRectI _alphaTextureBounds;
    private int _alphaTextureScale = 1;

    private const int MaxAlphaTexturePixels = 8_000_000;

    private void ReleaseAlphaTexture()
    {
        _alphaTexture?.Dispose();
        _alphaTexture = null;
        _alphaTextureBounds = default;
        _alphaTextureScale = 1;
    }

    private bool TryBuildAlphaTexture(SKRectI bounds, out SKImage? image, out int texScale)
    {
        image = null;
        texScale = 1;
        if (_mask == null)
            return false;

        int srcW = bounds.Width;
        int srcH = bounds.Height;
        while ((long)(srcW / texScale) * (srcH / texScale) > MaxAlphaTexturePixels)
            texScale *= 2;

        int dstW = Math.Max(1, (srcW + texScale - 1) / texScale);
        int dstH = Math.Max(1, (srcH + texScale - 1) / texScale);

        using var bmp = new SKBitmap(dstW, dstH, SKColorType.Alpha8, SKAlphaType.Opaque);
        var span = bmp.GetPixelSpan();
        for (int y = 0; y < dstH; y++)
        {
            int srcY = bounds.Top + y * texScale;
            for (int x = 0; x < dstW; x++)
            {
                int srcX = bounds.Left + x * texScale;
                int idx = srcY * _docW + srcX;
                span[y * dstW + x] = _mask[idx] > 0 ? (byte)255 : (byte)0;
            }
        }

        image = SKImage.FromBitmap(bmp);
        return image != null;
    }

    private void InvalidateAlphaTexture() => ReleaseAlphaTexture();

    public Snapshot CaptureSnapshot()
        => new(
            _docW,
            _docH,
            _mask?.ToArray(),
            _geoType.ToString(),
            _geoRect,
            _geoPoly.ToArray());

    public void RestoreSnapshot(Snapshot snapshot)
    {
        _docW = snapshot.Width;
        _docH = snapshot.Height;
        _mask = snapshot.Mask?.ToArray();
        _geoRect = snapshot.GeometryRect;
        _geoPoly = snapshot.GeometryPolygon.ToList();
        _geoType = Enum.TryParse<SelectionGeometry>(snapshot.GeometryType, out var type)
            ? type
            : SelectionGeometry.None;
        _outlineParts.Clear();
        _cachedOutlineGeo = null;
        _outlineDirty = false;
        _cachedMaskGeo = null;
        _simplifyOutline = false;
        InvalidateAlphaTexture();
        RecomputeMaskMetadata();
    }

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
        ClearOutline();
        _cachedMaskGeo = null;
        _maskBounds = null;
        _selectedCount = 0;
        _simplifyOutline = false;
        InvalidateAlphaTexture();
    }

    public bool IsSelected(int x, int y)
    {
        if (_mask == null) return true;
        if ((uint)x >= (uint)_docW || (uint)y >= (uint)_docH) return false;
        return _mask[y * _docW + x] > 0;
    }

    internal bool TryGetMaskBuffer(out byte[]? mask, out int docW, out int docH)
    {
        mask = _mask;
        docW = _docW;
        docH = _docH;
        return _mask != null;
    }

    public SKRectI? GetMaskBounds()
    {
        if (_mask == null) return null;
        return _maskBounds;
    }

    public void Invert()
    {
        var hadExplicitMask = _mask != null;
        var next = new byte[_docW * _docH];
        if (_mask == null)
        {
            Array.Fill(next, (byte)255);
        }
        else
        {
            for (int i = 0; i < _mask.Length; i++)
                next[i] = _mask[i] > 0 ? (byte)0 : (byte)255;
        }

        CommitMask(next);
        ClearOutline();
        if (_mask != null)
        {
            if (hadExplicitMask && TryGetSolidRectBounds(out var rect))
            {
                _geoType = SelectionGeometry.Rect;
                _geoRect = rect;
                SetOutlinePart(rect);
            }
            else
            {
                _geoType = SelectionGeometry.Mask;
                _geoRect = default;
                _geoPoly.Clear();
            }
        }
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

        ApplyRectOp(next, x1, y1, x2, y2, op);

        CommitMask(next);
        var newRect = new SKRectI(x1, y1, x2, y2);
        if (op == SelectOp.Replace && TryGetSolidRectBounds(out var solid))
        {
            _geoType = SelectionGeometry.Rect;
            _geoRect = solid;
            SetOutlinePart(solid);
        }
        else if (op == SelectOp.Replace)
        {
            _geoType = SelectionGeometry.Mask;
            _geoPoly.Clear();
            _geoRect = newRect;
            SetOutlinePart(newRect);
        }
        else
        {
            _geoType = SelectionGeometry.Mask;
            _geoPoly.Clear();
            _geoRect = default;
            AddOutlinePart(newRect);
        }
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

        var bounds = path.Bounds;
        int x1 = Math.Clamp((int)bounds.Left, 0, _docW - 1);
        int y1 = Math.Clamp((int)bounds.Top, 0, _docH - 1);
        int x2 = Math.Clamp((int)Math.Ceiling(bounds.Right), 0, _docW - 1);
        int y2 = Math.Clamp((int)Math.Ceiling(bounds.Bottom), 0, _docH - 1);
        int bW = x2 - x1 + 1;
        int bH = y2 - y1 + 1;
        if (bW <= 0 || bH <= 0) return;

        // Krita approach: rasterize the path into an Alpha8 bitmap in one
        // native draw call instead of per-pixel region.Contains(). Skia
        // handles the scanline rasterization in C++.
        using var raster = new SKBitmap(new SKImageInfo(bW, bH, SKColorType.Alpha8, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(raster))
        using (var fill = new SKPaint { Color = SKColors.White, IsAntialias = false, BlendMode = SKBlendMode.Src })
        {
            canvas.Clear(SKColors.Transparent);
            canvas.Translate(-x1, -y1);
            canvas.DrawPath(path, fill);
        }

        var next = CreateBaseMask(op);
        unsafe
        {
            var rp = (byte*)raster.GetPixels().ToPointer();
            int stride = raster.RowBytes;
            for (int y = y1; y <= y2; y++)
            {
                int ri = (y - y1) * stride;
                int di = y * _docW + x1;
                for (int x = 0; x < bW; x++)
                {
                    if (rp[ri + x] > 0)
                        ApplyAt(next, di + x, op, true);
                }
            }
        }

        CommitMask(next);
        var polyPts = points is SKPoint[] arr ? arr : points.ToArray();
        if (op == SelectOp.Replace)
        {
            _geoType = SelectionGeometry.Polygon;
            _geoPoly = new List<SKPoint>(points);
            SetOutlinePart(polyPts);
        }
        else
        {
            _geoType = SelectionGeometry.Mask;
            _geoPoly.Clear();
            _geoRect = default;
            AddOutlinePart(polyPts);
        }
        _cachedMaskGeo = null;
    }

    public void SetFromFloodFill(TiledPixelBuffer pixels, int srcX, int srcY,
        double tolerance, SelectOp op = SelectOp.Replace)
        => SetFromFloodFill(pixels, srcX, srcY, 0, 0, tolerance, op);

    // Flood-fill selection from a flat BGRA byte[] reference composite (e.g. merged reference layers).
    public void SetFromFloodFillBuffer(byte[] refBuf, int startDocX, int startDocY,
        double tolerance, SelectOp op = SelectOp.Replace,
        bool contiguousFill = true, int areaScaling = 0)
    {
        EnsureMaskExists();
        if ((uint)startDocX >= (uint)_docW || (uint)startDocY >= (uint)_docH) return;

        int startIdx = startDocY * _docW + startDocX;
        int off = startIdx * 4;
        byte refB = refBuf[off], refG = refBuf[off + 1], refR = refBuf[off + 2], refA = refBuf[off + 3];
        int threshold = ColorDifference.Tolerance01ToThreshold(tolerance);

        var next = CreateBaseMask(op);
        var simCache = new Dictionary<uint, bool>(1024);

        bool Similar(int docX, int docY)
        {
            int idx = docY * _docW + docX;
            int pOff = idx * 4;
            uint packed = (uint)(refBuf[pOff] | (refBuf[pOff + 1] << 8) | (refBuf[pOff + 2] << 16) | (refBuf[pOff + 3] << 24));
            if (simCache.TryGetValue(packed, out var cached))
                return cached;
            var result = ColorDifference.IsSimilarBgra(refBuf.AsSpan(pOff, 4), refB, refG, refR, refA, threshold);
            simCache[packed] = result;
            return result;
        }

        RunFloodFillIntoMask(next, startDocX, startDocY, op, Similar, contiguousFill);
        ApplyAreaScalingToMask(next, areaScaling);
        CommitMask(next);
        FinalizeMaskSelection();
    }

    public void SetFromFloodFill(
        TiledPixelBuffer pixels,
        int srcX,
        int srcY,
        int offsetX,
        int offsetY,
        double tolerance,
        SelectOp op = SelectOp.Replace,
        bool contiguousFill = true,
        int areaScaling = 0)
    {
        EnsureMaskExists();

        int startDocX = srcX + offsetX;
        int startDocY = srcY + offsetY;
        if ((uint)startDocX >= (uint)_docW || (uint)startDocY >= (uint)_docH)
            return;

        pixels.GetPixel(srcX, srcY, out byte refB, out byte refG, out byte refR, out byte refA);
        int threshold = ColorDifference.Tolerance01ToThreshold(tolerance);

        var next = CreateBaseMask(op);
        var simCache = new Dictionary<int, bool>(1024);

        bool Similar(int docX, int docY)
        {
            pixels.GetPixel(docX - offsetX, docY - offsetY, out byte b, out byte g, out byte r, out byte a);
            int packed = b | (g << 8) | (r << 16) | (a << 24);
            if (simCache.TryGetValue(packed, out var cached))
                return cached;
            var result = ColorDifference.IsSimilarBgra(b, g, r, a, refB, refG, refR, refA, threshold);
            simCache[packed] = result;
            return result;
        }

        RunFloodFillIntoMask(next, startDocX, startDocY, op, Similar, contiguousFill);
        ApplyAreaScalingToMask(next, areaScaling);
        CommitMask(next);
        FinalizeMaskSelection();
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
        => RenderMarchingAnts(dc, zoom, 0);

    public void RenderMarchingAnts(DrawingContext dc, double zoom, float phase)
    {
        if (!HasSelection || _geoType == SelectionGeometry.None) return;

        // Fixed document-space dash length — the dash count per boundary
        // stays constant at any zoom, preventing infinite segment explosions
        // that would lag the renderer at extreme magnification.
        const double t = 1.5;
        const double dash = 4.0;
        var cycle = dash * 2;
        var offset = phase % (float)cycle;

        DrawOutlinePass(dc, t, dash, offset, Avalonia.Media.Brushes.White);
        DrawOutlinePass(dc, t, dash, offset + dash, Avalonia.Media.Brushes.Black);
    }

    private void DrawOutlinePass(DrawingContext dc, double thickness, double dash, double offset, IBrush brush)
    {
        var pen = new Pen(brush, thickness, new DashStyle([dash, dash], offset));

        var outline = GetOutlineGeometry();
        if (outline != null)
        {
            dc.DrawGeometry(null, pen, outline);
            return;
        }

        if (_geoType == SelectionGeometry.Rect)
        {
            var r = new Avalonia.Rect(_geoRect.Left, _geoRect.Top, _geoRect.Width, _geoRect.Height);
            dc.DrawRectangle(null, pen, r);
            return;
        }

        if (_geoPoly.Count >= 2)
        {
            dc.DrawGeometry(null, pen, BuildPolygonGeometry());
            return;
        }

        if (_geoType == SelectionGeometry.Mask)
        {
            if (_simplifyOutline || _selectedCount > SimplifyOutlineSelectedPixels)
            {
                if (_maskBounds is { } b)
                    DrawBoundsRect(dc, pen, b);
                return;
            }

            var geo = _cachedMaskGeo ??= BuildMaskOutline();
            if (geo != null)
                dc.DrawGeometry(null, pen, geo);
            else if (_maskBounds is { } b)
                DrawBoundsRect(dc, pen, b);
        }
    }

    private StreamGeometry BuildPolygonGeometry()
    {
        var geo = new StreamGeometry();
        using (var c = geo.Open())
        {
            c.BeginFigure(new Avalonia.Point(_geoPoly[0].X, _geoPoly[0].Y), true);
            for (int i = 1; i < _geoPoly.Count; i++)
                c.LineTo(new Avalonia.Point(_geoPoly[i].X, _geoPoly[i].Y));
            c.EndFigure(true);
        }
        return geo;
    }

    private static void DrawBoundsRect(DrawingContext dc, Pen pen, SKRectI b)
    {
        var r = new Avalonia.Rect(b.Left, b.Top, b.Width, b.Height);
        dc.DrawRectangle(null, pen, r);
    }

    private StreamGeometry? BuildMaskOutline()
    {
        if (_mask == null || _maskBounds is not { } b) return null;

        bool Selected(int x, int y) =>
            (uint)x < (uint)_docW && (uint)y < (uint)_docH && _mask[y * _docW + x] > 0;

        var geo = new StreamGeometry();
        using var ctx = geo.Open();
        int segments = 0;

        bool EmitHorizontal(int y, int x0, int x1)
        {
            if (++segments > MaxOutlineSegments)
            {
                _simplifyOutline = true;
                return false;
            }

            ctx.BeginFigure(new Avalonia.Point(x0, y), false);
            ctx.LineTo(new Avalonia.Point(x1, y));
            ctx.EndFigure(false);
            return true;
        }

        bool EmitVertical(int x, int y0, int y1)
        {
            if (++segments > MaxOutlineSegments)
            {
                _simplifyOutline = true;
                return false;
            }

            ctx.BeginFigure(new Avalonia.Point(x, y0), false);
            ctx.LineTo(new Avalonia.Point(x, y1));
            ctx.EndFigure(false);
            return true;
        }

        for (int y = b.Top; y < b.Bottom; y++)
        {
            int x = b.Left;
            while (x < b.Right)
            {
                while (x < b.Right && !(Selected(x, y) && !Selected(x, y - 1))) x++;
                if (x >= b.Right) break;
                int x0 = x;
                while (x < b.Right && Selected(x, y) && !Selected(x, y - 1)) x++;
                if (!EmitHorizontal(y, x0, x)) return null;
            }
        }

        for (int y = b.Top; y < b.Bottom; y++)
        {
            int x = b.Left;
            while (x < b.Right)
            {
                while (x < b.Right && !(Selected(x, y) && !Selected(x, y + 1))) x++;
                if (x >= b.Right) break;
                int x0 = x;
                while (x < b.Right && Selected(x, y) && !Selected(x, y + 1)) x++;
                if (!EmitHorizontal(y + 1, x0, x)) return null;
            }
        }

        for (int x = b.Left; x < b.Right; x++)
        {
            int y = b.Top;
            while (y < b.Bottom)
            {
                while (y < b.Bottom && !(Selected(x, y) && !Selected(x - 1, y))) y++;
                if (y >= b.Bottom) break;
                int y0 = y;
                while (y < b.Bottom && Selected(x, y) && !Selected(x - 1, y)) y++;
                if (!EmitVertical(x, y0, y)) return null;
            }
        }

        for (int x = b.Left; x < b.Right; x++)
        {
            int y = b.Top;
            while (y < b.Bottom)
            {
                while (y < b.Bottom && !(Selected(x, y) && !Selected(x + 1, y))) y++;
                if (y >= b.Bottom) break;
                int y0 = y;
                while (y < b.Bottom && Selected(x, y) && !Selected(x + 1, y)) y++;
                if (!EmitVertical(x + 1, y0, y)) return null;
            }
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
            RecomputeMaskMetadata();
            return;
        }

        Clear();
    }

    private void RecomputeMaskMetadata()
    {
        _cachedMaskGeo = null;
        _simplifyOutline = false;
        InvalidateAlphaTexture();
        if (_mask == null)
        {
            _maskBounds = null;
            _selectedCount = 0;
            return;
        }

        int minX = _docW, minY = _docH, maxX = -1, maxY = -1;
        int count = 0;
        for (int y = 0; y < _docH; y++)
        {
            int row = y * _docW;
            for (int x = 0; x < _docW; x++)
            {
                if (_mask[row + x] == 0) continue;
                count++;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        _selectedCount = count;
        _maskBounds = count > 0 ? new SKRectI(minX, minY, maxX + 1, maxY + 1) : null;
    }

    private void FinalizeMaskSelection()
    {
        _geoType = SelectionGeometry.Mask;
        _geoRect = default;
        _cachedMaskGeo = null;
        _simplifyOutline = false;
        _geoPoly.Clear();
        ClearOutline();
    }

    private void RunFloodFillIntoMask(
        byte[] next,
        int startX,
        int startY,
        SelectOp op,
        Func<int, int, bool> similar,
        bool contiguousFill)
    {
        void OnPixel(int x, int y) => Apply(next, x, y, op, true);

        if (contiguousFill)
        {
            _visit.BeginPass(_docW * _docH);
            FloodFillScanline.FillContiguous(_docW, _docH, startX, startY, similar,
                _visit.Stamp, _visit.Epoch, OnPixel);
            if (op == SelectOp.Intersect)
                ClearUnvisitedIntersect(next);
        }
        else if (op == SelectOp.Intersect)
        {
            for (int y = 0; y < _docH; y++)
            {
                for (int x = 0; x < _docW; x++)
                    Apply(next, x, y, SelectOp.Intersect, similar(x, y));
            }
        }
        else
        {
            FloodFillNonContiguous.FillInBounds(0, 0, _docW - 1, _docH - 1, similar, OnPixel);
        }
    }

    private void ApplyAreaScalingToMask(byte[] next, int areaScaling)
    {
        int delta = Math.Clamp(areaScaling, -20, 20);
        if (delta != 0)
            MaskMorphology.ApplyAreaScaling(next, _docW, _docH, delta);
    }

    private bool TryGetSolidRectBounds(out SKRectI rect)
    {
        rect = default;
        var bounds = GetMaskBounds();
        if (bounds == null || _mask == null) return false;

        var b = bounds.Value;
        for (var y = 0; y < _docH; y++)
        {
            for (var x = 0; x < _docW; x++)
            {
                var insideBounds = x >= b.Left && x < b.Right && y >= b.Top && y < b.Bottom;
                var selected = _mask[y * _docW + x] > 0;
                if (selected != insideBounds) return false;
            }
        }

        rect = b;
        return true;
    }

    private void ApplyRectOp(byte[] next, int x1, int y1, int x2, int y2, SelectOp op)
    {
        if (op == SelectOp.Intersect && _mask != null)
        {
            var shape = new SKRectI(x1, y1, x2, y2);
            var iter = IterationBoundsForIntersect(shape);
            for (int py = iter.Top; py < iter.Bottom; py++)
            {
                int row = py * _docW;
                for (int px = iter.Left; px < iter.Right; px++)
                {
                    bool inside = (uint)(px - x1) < (uint)(x2 - x1) && (uint)(py - y1) < (uint)(y2 - y1);
                    ApplyAt(next, row + px, op, inside);
                }
            }
            return;
        }

        for (int py = y1; py < y2; py++)
        {
            int row = py * _docW + x1;
            for (int px = 0; px < x2 - x1; px++)
                ApplyAt(next, row + px, op, true);
        }
    }

    private void ApplyRegionOp(byte[] next, int x1, int y1, int x2, int y2, SelectOp op,
        Func<int, int, bool> insideAt)
    {
        var shape = new SKRectI(x1, y1, x2 + 1, y2 + 1);
        if (op == SelectOp.Intersect && _mask != null)
        {
            var iter = IterationBoundsForIntersect(shape);
            for (int py = iter.Top; py < iter.Bottom; py++)
                for (int px = iter.Left; px < iter.Right; px++)
                    Apply(next, px, py, op, insideAt(px, py));
            return;
        }

        for (int py = y1; py <= y2; py++)
            for (int px = x1; px <= x2; px++)
                Apply(next, px, py, op, insideAt(px, py));
    }

    private SKRectI IterationBoundsForIntersect(SKRectI shapeBounds)
    {
        if (_maskBounds is not { } existing)
            return shapeBounds;

        return new SKRectI(
            Math.Clamp(Math.Min(existing.Left, shapeBounds.Left), 0, _docW),
            Math.Clamp(Math.Min(existing.Top, shapeBounds.Top), 0, _docH),
            Math.Clamp(Math.Max(existing.Right, shapeBounds.Right), 0, _docW),
            Math.Clamp(Math.Max(existing.Bottom, shapeBounds.Bottom), 0, _docH));
    }

    private void ClearUnvisitedIntersect(byte[] next)
    {
        if (_mask == null || _maskBounds is not { } existing) return;

        var iter = IterationBoundsForIntersect(new SKRectI(0, 0, _docW, _docH));
        for (int py = iter.Top; py < iter.Bottom; py++)
        {
            int row = py * _docW;
            for (int px = iter.Left; px < iter.Right; px++)
            {
                int idx = row + px;
                if (_visit.Stamp[idx] == _visit.Epoch) continue;
                Apply(next, px, py, SelectOp.Intersect, false);
            }
        }
    }

    private void Apply(byte[] mask, int x, int y, SelectOp op, bool inside)
        => ApplyAt(mask, y * _docW + x, op, inside);

    private static void ApplyAt(byte[] mask, int idx, SelectOp op, bool inside)
    {
        bool cur = mask[idx] > 0;
        mask[idx] = (op switch
        {
            SelectOp.Add => cur || inside,
            SelectOp.Subtract => cur && !inside,
            SelectOp.Intersect => cur && inside,
            _ => inside,
        }) ? (byte)255 : (byte)0;
    }
}
