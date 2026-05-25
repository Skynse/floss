using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Floss.App.Input;

public sealed class PenPressureSettings
{
    public bool Enabled { get; set; }
    public float[] CurvePoints { get; set; } = [0f, 0f, 1f, 1f];

    public PenPressureSettings Clone()
        => new() { Enabled = Enabled, CurvePoints = (float[])CurvePoints.Clone() };

    public double Evaluate(double pressure)
    {
        if (!Enabled) return pressure;
        var v = (float)Math.Clamp(pressure, 0, 1);
        if (CurvePoints.Length < 4) return v;
        return EvalCurve(v);
    }

    public byte[] ComputeLut()
    {
        var lut = new byte[256];
        for (var i = 0; i < 256; i++)
            lut[i] = (byte)Math.Clamp((int)(Evaluate(i / 255.0) * 255.0 + 0.5), 0, 255);
        return lut;
    }

    private float EvalCurve(float x)
    {
        var pts = CurvePoints;
        if (pts.Length < 4) return x;

        // Build point list from flat array
        int n = pts.Length / 2;
        if (x <= pts[0]) return pts[1];
        if (x >= pts[(n - 1) * 2]) return pts[(n - 1) * 2 + 1];

        for (var i = 0; i < n - 1; i++)
        {
            float x0 = pts[i * 2], y0 = pts[i * 2 + 1];
            float x1 = pts[(i + 1) * 2], y1 = pts[(i + 1) * 2 + 1];
            if (x < x0 || x > x1) continue;

            var t = (x - x0) / (x1 - x0);
            var t2 = t * t;
            var t3 = t2 * t;
            var h00 = 2 * t3 - 3 * t2 + 1;
            var h10 = t3 - 2 * t2 + t;
            var h01 = -2 * t3 + 3 * t2;
            var h11 = t3 - t2;

            var m0 = Tangent(n, pts, i);
            var m1 = Tangent(n, pts, i + 1);
            var dx = x1 - x0;

            return h00 * y0 + h10 * dx * m0 + h01 * y1 + h11 * dx * m1;
        }
        return x;
    }

    private static float Tangent(int n, float[] pts, int i)
    {
        if (i <= 0)
            return (pts[3] - pts[1]) / (pts[2] - pts[0]);
        if (i >= n - 1)
        {
            int last = (n - 1) * 2;
            int prev = (n - 2) * 2;
            return (pts[last + 1] - pts[prev + 1]) / (pts[last] - pts[prev]);
        }
        float xPrev = pts[(i - 1) * 2], yPrev = pts[(i - 1) * 2 + 1];
        float xNext = pts[(i + 1) * 2], yNext = pts[(i + 1) * 2 + 1];
        return (yNext - yPrev) / (xNext - xPrev);
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static PenPressureSettings Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<PenPressureSettings>(json, JsonOpts) ?? new();
            }
        }
        catch { }
        return new();
    }

    public void Save(string path)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { }
    }
}
