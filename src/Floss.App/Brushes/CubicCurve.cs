using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Floss.App.Brushes;

// A user-editable spline curve mapping [0,1] → [0,1].
// Evaluated via a precomputed 256-entry LUT. Thread-safe reads; call RebuildLut() after edits.
public sealed class CubicCurve
{
    public const int LutSize = 256;

    private readonly List<CurvePoint> _points = [];
    private float[] _lut = new float[LutSize];

    public static CubicCurve Identity() { var c = new CubicCurve(); c.Reset(); return c; }
    public static CubicCurve Linear(float x0, float y0, float x1, float y1)
    {
        var c = new CubicCurve();
        c._points.Add(new(x0, y0));
        c._points.Add(new(x1, y1));
        c.RebuildLut();
        return c;
    }

    public IReadOnlyList<CurvePoint> Points => _points;

    public void Reset()
    {
        _points.Clear();
        _points.Add(new(0f, 0f));
        _points.Add(new(1f, 1f));
        RebuildLut();
    }

    public void SetPoints(IEnumerable<CurvePoint> pts)
    {
        _points.Clear();
        _points.AddRange(pts);
        SortAndClamp();
        RebuildLut();
    }

    public void AddPoint(float x, float y)
    {
        _points.Add(new(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
        SortAndClamp();
        RebuildLut();
    }

    public void MovePoint(int index, float x, float y)
    {
        if (index < 0 || index >= _points.Count) return;
        _points[index] = new(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1));
        SortAndClamp();
        RebuildLut();
    }

    public void RemovePoint(int index)
    {
        if (_points.Count <= 2 || index < 0 || index >= _points.Count) return;
        _points.RemoveAt(index);
        RebuildLut();
    }

    public float Evaluate(float x)
    {
        var idx = (int)(Math.Clamp(x, 0, 1) * (LutSize - 1));
        return _lut[Math.Clamp(idx, 0, LutSize - 1)];
    }

    public float[] GetLut() => _lut;

    public void RebuildLut()
    {
        var pts = _points.OrderBy(p => p.X).ToList();
        if (pts.Count == 0) { Array.Fill(_lut, 0.5f); return; }
        if (pts[0].X > 0) pts.Insert(0, new(0f, pts[0].Y));
        if (pts[^1].X < 1f) pts.Add(new(1f, pts[^1].Y));
        int n = pts.Count;

        if (n == 2)
        {
            float dy = pts[1].Y - pts[0].Y;
            float dx = pts[1].X - pts[0].X;
            for (int i = 0; i < LutSize; i++)
            {
                float t = i / (float)(LutSize - 1);
                _lut[i] = Math.Clamp(dx < 1e-6f ? pts[0].Y : pts[0].Y + dy * (t - pts[0].X) / dx, 0, 1);
            }
            return;
        }

        // Natural cubic spline
        var h = new float[n - 1];
        for (int i = 0; i < n - 1; i++) h[i] = pts[i + 1].X - pts[i].X;

        // Set up tridiagonal system for second derivatives M[i]
        var bd = new float[n]; // main diag
        var c = new float[n]; // upper
        var d = new float[n]; // rhs
        bd[0] = 1f; c[0] = 0f; d[0] = 0f;               // natural: M[0] = 0
        bd[n - 1] = 1f; d[n - 1] = 0f;                        // natural: M[n-1] = 0
        for (int i = 1; i < n - 1; i++)
        {
            float hi = h[i], him1 = h[i - 1];
            bd[i] = 2f * (him1 + hi);
            c[i] = hi;
            d[i] = 6f * ((pts[i + 1].Y - pts[i].Y) / hi - (pts[i].Y - pts[i - 1].Y) / him1);
        }

        // Thomas algorithm forward
        var sub = new float[n]; // sub-diag (lower)
        for (int i = 1; i < n; i++) sub[i] = h[i - 1]; // h[i-1] for i<n-1, 0 for last
        sub[n - 1] = 0f;
        for (int i = 1; i < n; i++)
        {
            if (Math.Abs(bd[i - 1]) < 1e-10f) continue;
            float m = sub[i] / bd[i - 1];
            bd[i] -= m * c[i - 1];
            d[i] -= m * d[i - 1];
        }

        var M = new float[n];
        M[n - 1] = Math.Abs(bd[n - 1]) > 1e-10f ? d[n - 1] / bd[n - 1] : 0f;
        for (int i = n - 2; i >= 0; i--)
            M[i] = Math.Abs(bd[i]) > 1e-10f ? (d[i] - c[i] * M[i + 1]) / bd[i] : 0f;

        // Evaluate at LUT sample points
        for (int k = 0; k < LutSize; k++)
        {
            float x = k / (float)(LutSize - 1);
            int seg = 0;
            for (int i = 0; i < n - 2; i++) { if (x <= pts[i + 1].X) { seg = i; break; } seg = i + 1; }
            float hi2 = h[seg];
            if (hi2 < 1e-7f) { _lut[k] = Math.Clamp(pts[seg].Y, 0, 1); continue; }
            float dx = x - pts[seg].X;
            float y = pts[seg].Y
                    + ((pts[seg + 1].Y - pts[seg].Y) / hi2 - hi2 * (2f * M[seg] + M[seg + 1]) / 6f) * dx
                    + M[seg] / 2f * dx * dx
                    + (M[seg + 1] - M[seg]) / (6f * hi2) * dx * dx * dx;
            _lut[k] = Math.Clamp(y, 0, 1);
        }
    }

    private void SortAndClamp()
    {
        for (int i = 0; i < _points.Count; i++)
            _points[i] = new(Math.Clamp(_points[i].X, 0, 1), Math.Clamp(_points[i].Y, 0, 1));
        _points.Sort((a, b) => a.X.CompareTo(b.X));
    }

    // Serialization — compact "x,y;x,y;..." string (same idea as Krita's curve string)
    public string Serialize()
    {
        var parts = new string[_points.Count];
        for (int i = 0; i < _points.Count; i++)
            parts[i] = $"{_points[i].X:G6},{_points[i].Y:G6}";
        return string.Join(";", parts);
    }

    public static CubicCurve Deserialize(string s)
    {
        var c = new CubicCurve();
        c._points.Clear();
        if (!string.IsNullOrWhiteSpace(s))
        {
            foreach (var seg in s.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = seg.Split(',');
                if (parts.Length == 2
                    && float.TryParse(parts[0], out float x)
                    && float.TryParse(parts[1], out float y))
                    c._points.Add(new(Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1)));
            }
        }
        if (c._points.Count < 2) c.Reset();
        else c.RebuildLut();
        return c;
    }

    public CubicCurve Clone()
    {
        var c = new CubicCurve();
        c._points.AddRange(_points);
        Array.Copy(_lut, c._lut, LutSize);
        return c;
    }
}

public readonly record struct CurvePoint(float X, float Y);
