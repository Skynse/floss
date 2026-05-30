using System;
using System.Runtime.CompilerServices;

namespace Floss.App.Brushes.Engine;

/// <summary>
/// Drawpile GIMP-based classic brush stamp LUTs — 8-bit masks.
/// Exact algorithm port from Drawpile's paint.c, outputting byte[] [0,255].
/// Ported features:
///   - 101 hardness LUTs (0-100%), 128×128 each
///   - Standard mask (single-sample LUT lookup)
///   - High-res mask (2×2 supersampling for radius &lt; 8)
///   - Sub-pixel offset mask (quarter-pixel bilinear interpolation)
///   - Small-brush fudge factors (matching Drawpile's empirical values)
/// </summary>
public static class ClassicBrushLut
{
    // ── Constants matching Drawpile exactly ────────────────────────────────
    private const int LUT_RADIUS = 128;
    private const int LUT_SIZE = LUT_RADIUS * LUT_RADIUS; // 16384
    private const int LUT_MIN_HARDNESS = 0;
    private const int LUT_MAX_HARDNESS = 100;
    private const int LUT_COUNT = LUT_MAX_HARDNESS - LUT_MIN_HARDNESS + 1; // 101

    private static readonly byte[][] _luts = new byte[LUT_COUNT][];
    private static readonly object _lutLock = new();

    private static byte[] GenerateLut(int index)
    {
        var lut = new byte[LUT_SIZE];
        double h = 1.0 - (index / 100.0);
        double exponent = h < 0.0000004 ? 1000000.0 : 0.4 / h;
        double radius = LUT_RADIUS;
        for (int i = 0; i < LUT_SIZE; i++)
        {
            double d = 1.0 - Math.Pow(Math.Pow(Math.Sqrt(i) / radius, exponent), 2.0);
            lut[i] = (byte)Math.Clamp((int)(d * 255.0 + 0.5), 0, 255);
        }
        return lut;
    }

    private static byte[] GetLut(int hardness)
    {
        int idx = Math.Clamp(hardness, LUT_MIN_HARDNESS, LUT_MAX_HARDNESS);
        var lut = _luts[idx];
        if (lut != null) return lut;
        lock (_lutLock)
        {
            lut = _luts[idx];
            if (lut == null)
            {
                lut = GenerateLut(idx);
                _luts[idx] = lut;
            }
            return lut;
        }
    }

    // ── Stamp struct ──────────────────────────────────────────────────────

    public ref struct BrushStamp
    {
        public int Top, Left, Diameter;
        public ReadOnlySpan<byte> Data;
    }

    // ── Standard mask (radius >= 1, no supersampling) ─────────────────────

    private static BrushStamp GetMask(float radius, int hardness, byte[] buffer)
    {
        float r = radius / 2.0f;
        if (r < 1.0)
        {
            // Single-pixel brush: 3×3, center = 255
            return new BrushStamp { Top = -1, Left = -1, Diameter = 3, Data = new byte[] { 0, 0, 0, 0, 255, 0, 0, 0, 0 } };
        }

        int diameter;
        float offset;
        int rawDiameter = (int)Math.Ceiling(radius) + 2;
        bool rawEven = rawDiameter % 2 == 0;
        if (rawEven) { diameter = rawDiameter + 1; offset = -1.0f; }
        else { diameter = rawDiameter; offset = -0.5f; }

        // Empirically determined fudge factors (exact Drawpile match)
        float fudge = r < 4.0 ? 0.8f
                    : rawEven && r < 8.0 ? 0.9f
                    : 1.0f;

        var lut = GetLut(hardness);
        float lutScale = (LUT_RADIUS - 1.0f) * (LUT_RADIUS - 1.0f) / (r * r);

        for (int y = 0; y < diameter; y++)
        {
            float yy = (y - r + offset) * (y - r + offset);
            for (int x = 0; x < diameter; x++)
            {
                float dist = ((x - r + offset) * (x - r + offset) + yy) * fudge * lutScale;
                int i = (int)dist;
                buffer[y * diameter + x] = i < LUT_SIZE ? lut[i] : (byte)0;
            }
        }

        return new BrushStamp { Top = -(diameter / 2), Left = -(diameter / 2), Diameter = diameter, Data = buffer.AsSpan(0, diameter * diameter) };
    }

    // ── High-res mask (2×2 supersampling, for radius < 8) ─────────────────

    private static BrushStamp GetHighResMask(float radius, int hardness, byte[] buffer)
    {
        int rawDiameter = (int)Math.Ceiling(radius) + 2;
        float rawOffset = ((float)Math.Ceiling(radius) - radius) / -2.0f;
        int diameter;
        float offset;
        if (rawDiameter % 2 == 0) { diameter = rawDiameter + 1; offset = rawOffset - 2.5f; }
        else { diameter = rawDiameter; offset = rawOffset - 1.5f; }

        var lut = GetLut(hardness);
        float lutScale = (LUT_RADIUS - 1.0f) * (LUT_RADIUS - 1.0f) / (radius * radius);

        for (int y = 0; y < diameter; y++)
        {
            float y2 = y * 2;
            float yy0 = (y2 - radius + offset) * (y2 - radius + offset);
            float yy1 = (y2 + 1.0f - radius + offset) * (y2 + 1.0f - radius + offset);

            for (int x = 0; x < diameter; x++)
            {
                float x2 = x * 2;
                float xx0 = (x2 - radius + offset) * (x2 - radius + offset);
                float xx1 = (x2 + 1.0f - radius + offset) * (x2 + 1.0f - radius + offset);

                int dist00 = (int)((xx0 + yy0) * lutScale);
                int dist01 = (int)((xx0 + yy1) * lutScale);
                int dist10 = (int)((xx1 + yy0) * lutScale);
                int dist11 = (int)((xx1 + yy1) * lutScale);

                uint acc = (uint)((dist00 < LUT_SIZE ? lut[dist00] : 0)
                    + (dist01 < LUT_SIZE ? lut[dist01] : 0)
                    + (dist10 < LUT_SIZE ? lut[dist10] : 0)
                    + (dist11 < LUT_SIZE ? lut[dist11] : 0));
                buffer[y * diameter + x] = (byte)(acc / 4);
            }
        }

        return new BrushStamp { Top = -(diameter / 2), Left = -(diameter / 2), Diameter = diameter, Data = buffer.AsSpan(0, diameter * diameter) };
    }

    // ── Sub-pixel offset (quarter-pixel bilinear interpolation) ───────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void GenerateOffsetMask(byte[] dst, ReadOnlySpan<byte> src, int diameter, int xfrac, int yfrac)
    {
        int k0 = xfrac * yfrac;
        int k1 = (4 - xfrac) * yfrac;
        int k2 = xfrac * (4 - yfrac);
        int k3 = (4 - xfrac) * (4 - yfrac);

        dst[0] = (byte)((src[0] * k3) / 16);
        for (int x = 0; x < diameter - 1; x++)
            dst[x + 1] = (byte)((src[x] * k2 + src[x + 1] * k3) / 16);

        for (int y = 0; y < diameter - 1; y++)
        {
            int yd = y * diameter;
            int yd1 = (y + 1) * diameter;
            int dstRow = yd1;

            // First pixel of row
            dst[dstRow] = (byte)((src[yd] * k1 + src[yd1] * k3) / 16);

            for (int x = 0; x < diameter - 1; x++)
            {
                dst[dstRow + x + 1] = (byte)((src[yd + x] * k0 + src[yd + x + 1] * k1
                    + src[yd1 + x] * k2 + src[yd1 + x + 1] * k3) / 16);
            }
        }
    }

    private static BrushStamp GetOffsetStamp(BrushStamp maskStamp, byte[] offsetBuffer, int x, int y, int hardness)
    {
        int left = maskStamp.Left + x / 4;
        int top = maskStamp.Top + y / 4;
        int diameter = maskStamp.Diameter;

        if (hardness == 100 || diameter < 48)
        {
            int xfrac = x & 3;
            int yfrac = y & 3;
            if (xfrac < 2) { xfrac += 2; left--; }
            else { xfrac -= 2; }
            if (yfrac < 2) { yfrac += 2; top--; }
            else { yfrac -= 2; }

            GenerateOffsetMask(offsetBuffer, maskStamp.Data, diameter, xfrac, yfrac);
            return new BrushStamp { Top = top, Left = left, Diameter = diameter, Data = offsetBuffer.AsSpan(0, diameter * diameter) };
        }

        return new BrushStamp { Top = top, Left = left, Diameter = diameter, Data = maskStamp.Data };
    }

    // ── Public API ────────────────────────────────────────────────────────

    /// <summary>Get the max buffer size needed for any stamp.</summary>
    public static int MaxBufferSize => LUT_SIZE; // 128×128 = 16384 — worst case diameter

    /// <summary>
    /// Generate a classic brush stamp mask in 8-bit.
    /// Main entry point — matches Drawpile's get_classic_mask_stamp.
    /// </summary>
    /// <param name="radius">Brush radius in pixels.</param>
    /// <param name="hardness">Hardness 0-100 (Drawpile: scaled_hardness).</param>
    /// <param name="buffer">Pre-allocated buffer, at least MaxBufferSize bytes.</param>
    /// <param name="offsetBuffer">Pre-allocated buffer for sub-pixel offset, at least MaxBufferSize bytes.</param>
    /// <param name="subPixelX">Sub-pixel X position (×4, e.g. 0-3). Use 0 for pixel-aligned.</param>
    /// <param name="subPixelY">Sub-pixel Y position (×4, e.g. 0-3).</param>
    public static BrushStamp GetStamp(float radius, int hardness, byte[] buffer, byte[] offsetBuffer, int subPixelX = 0, int subPixelY = 0)
    {
        BrushStamp stamp;
        if (radius < 8.0f && radius >= 1.0f)
            stamp = GetHighResMask(radius, hardness, buffer);
        else
            stamp = GetMask(radius, hardness, buffer);

        if (subPixelX != 0 || subPixelY != 0)
            stamp = GetOffsetStamp(stamp, offsetBuffer, subPixelX, subPixelY, hardness);

        return stamp;
    }
}
