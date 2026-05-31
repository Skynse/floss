using System;
using System.Runtime.CompilerServices;

namespace Floss.App.MyPaint;

/// <summary>
/// Port of libmypaint's fast dab mask generation and direct pixel compositing.
/// Adapts libmypaint's 16-bit premultiplied RGBA kernels to Floss's 8-bit BGRA tiles.
/// </summary>
public static class MyPaintDabRenderer
{
    private const int One15 = 1 << 15; // 32768, libmypaint's fixed 1.0

    /// <summary>
    /// Generates a dab mask for a circular/elliptical brush with hardness falloff.
    /// Port of libmypaint's render_dab_mask, but outputs a flat buffer with
    /// a tight bounding box instead of RLE-encoded tile-relative coordinates.
    /// </summary>
    public static unsafe void RenderDabMask(
        float cx, float cy, float radius, float hardness, float softness,
        float aspectRatio, float angleDeg,
        Span<ushort> mask, out int maskW, out int maskH, out int maskX, out int maskY)
    {
        hardness = Math.Clamp(hardness, 0.0f, 1.0f);
        if (aspectRatio < 1.0f) aspectRatio = 1.0f;

        // Pre-calculate hardness segments
        float seg1Off = 1.0f * (1.0f - softness);
        float seg1Slope = -(1.0f / hardness - 1.0f) * (1.0f - softness);
        float seg2Off = hardness / (1.0f - hardness) * (1.0f - softness);
        float seg2Slope = -hardness / (1.0f - hardness) * (1.0f - softness);

        float angleRad = angleDeg / 360.0f * 2.0f * MathF.PI;
        float cs = MathF.Cos(angleRad);
        float sn = MathF.Sin(angleRad);

        float rFringe = radius + 1.0f;
        int x0 = (int)MathF.Floor(cx - rFringe);
        int y0 = (int)MathF.Floor(cy - rFringe);
        int x1 = (int)MathF.Floor(cx + rFringe);
        int y1 = (int)MathF.Floor(cy + rFringe);

        maskX = x0;
        maskY = y0;
        maskW = x1 - x0 + 1;
        maskH = y1 - y0 + 1;

        float oneOverRadius2 = 1.0f / (radius * radius);

        fixed (ushort* maskP = mask)
        {
            if (radius < 3.0f)
            {
                float aaBorder = 1.0f;
                float rAaStart = radius > aaBorder ? (radius - aaBorder) : 0.0f;
                rAaStart = rAaStart * rAaStart / aspectRatio;

                for (int yp = y0; yp <= y1; yp++)
                {
                    for (int xp = x0; xp <= x1; xp++)
                    {
                        float rr = CalculateRrAntialiased(xp, yp, cx, cy, aspectRatio, sn, cs, oneOverRadius2, rAaStart);
                        maskP[(yp - y0) * maskW + (xp - x0)] = (ushort)(CalculateOpa(rr, hardness, seg1Off, seg1Slope, seg2Off, seg2Slope) * One15);
                    }
                }
            }
            else
            {
                for (int yp = y0; yp <= y1; yp++)
                {
                    for (int xp = x0; xp <= x1; xp++)
                    {
                        float rr = CalculateRr(xp, yp, cx, cy, aspectRatio, sn, cs, oneOverRadius2);
                        maskP[(yp - y0) * maskW + (xp - x0)] = (ushort)(CalculateOpa(rr, hardness, seg1Off, seg1Slope, seg2Off, seg2Slope) * One15);
                    }
                }
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CalculateRr(int xp, int yp, float x, float y, float aspectRatio, float sn, float cs, float oneOverRadius2)
    {
        float yy = (yp + 0.5f - y);
        float xx = (xp + 0.5f - x);
        float yyr = (yy * cs - xx * sn) * aspectRatio;
        float xxr = yy * sn + xx * cs;
        return (yyr * yyr + xxr * xxr) * oneOverRadius2;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CalculateRrAntialiased(int xp, int yp, float x, float y, float aspectRatio,
        float sn, float cs, float oneOverRadius2, float rAaStart)
    {
        float pixelRight = x - xp;
        float pixelBottom = y - yp;
        float pixelCenterX = pixelRight - 0.5f;
        float pixelCenterY = pixelBottom - 0.5f;
        float pixelLeft = pixelRight - 1.0f;
        float pixelTop = pixelBottom - 1.0f;

        float nearestX, nearestY;
        float rNear, rrNear;
        if (pixelLeft < 0 && pixelRight > 0 && pixelTop < 0 && pixelBottom > 0)
        {
            nearestX = 0; nearestY = 0;
            rNear = 0; rrNear = 0;
        }
        else
        {
            ClosestPointToLine(cs, sn, pixelCenterX, pixelCenterY, out nearestX, out nearestY);
            nearestX = Math.Clamp(nearestX, pixelLeft, pixelRight);
            nearestY = Math.Clamp(nearestY, pixelTop, pixelBottom);
            rNear = CalculateRSample(nearestX, nearestY, aspectRatio, sn, cs);
            rrNear = rNear * oneOverRadius2;
        }

        if (rrNear > 1.0f) return rrNear;

        float centerSign = SignPointInLine(pixelCenterX, pixelCenterY, cs, -sn);
        float radArea1 = MathF.Sqrt(1.0f / MathF.PI);

        float farthestX, farthestY;
        if (centerSign < 0)
        {
            farthestX = nearestX - sn * radArea1;
            farthestY = nearestY + cs * radArea1;
        }
        else
        {
            farthestX = nearestX + sn * radArea1;
            farthestY = nearestY - cs * radArea1;
        }

        float rFar = CalculateRSample(farthestX, farthestY, aspectRatio, sn, cs);
        float rrFar = rFar * oneOverRadius2;

        if (rFar < rAaStart) return (rrFar + rrNear) * 0.5f;

        float visibilityNear = 1.0f - rrNear;
        float delta = rrFar - rrNear;
        float delta2 = 1.0f + delta;
        visibilityNear /= delta2;
        return 1.0f - visibilityNear;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CalculateRSample(float x, float y, float aspectRatio, float sn, float cs)
    {
        float yyr = (y * cs - x * sn) * aspectRatio;
        float xxr = y * sn + x * cs;
        return yyr * yyr + xxr * xxr;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SignPointInLine(float px, float py, float vx, float vy)
        => (px - vx) * (-vy) - vx * (py - vy);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ClosestPointToLine(float lx, float ly, float px, float py, out float ox, out float oy)
    {
        float l2 = lx * lx + ly * ly;
        float dot = px * lx + py * ly;
        float t = dot / l2;
        ox = lx * t;
        oy = ly * t;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float CalculateOpa(float rr, float hardness,
        float seg1Off, float seg1Slope, float seg2Off, float seg2Slope)
    {
        float fac = rr <= hardness ? seg1Slope : seg2Slope;
        float opa = rr <= hardness ? seg1Off : seg2Off;
        opa += rr * fac;
        if (rr > 1.0f) opa = 0.0f;
        return Math.Clamp(opa, 0.0f, 1.0f);
    }

    /// <summary>
    /// Generates a dab mask as byte (0..255) for direct use with Floss's AVX2 compositing.
    /// </summary>
    public static unsafe void RenderDabMaskByte(
        float cx, float cy, float radius, float hardness, float softness,
        float aspectRatio, float angleDeg,
        Span<byte> mask, out int maskW, out int maskH, out int maskX, out int maskY)
    {
        if (hardness < 0.001f) hardness = 0.001f;
        if (hardness > 1.0f) hardness = 1.0f;
        if (aspectRatio < 1.0f) aspectRatio = 1.0f;

        float seg1Off = 1.0f * (1.0f - softness);
        float seg1Slope = -(1.0f / hardness - 1.0f) * (1.0f - softness);
        float seg2Off = hardness / (1.0f - hardness) * (1.0f - softness);
        float seg2Slope = -hardness / (1.0f - hardness) * (1.0f - softness);

        float angleRad = angleDeg / 360.0f * 2.0f * MathF.PI;
        float cs = MathF.Cos(angleRad);
        float sn = MathF.Sin(angleRad);

        float rFringe = radius + 1.0f;
        int x0 = (int)MathF.Floor(cx - rFringe);
        int y0 = (int)MathF.Floor(cy - rFringe);
        int x1 = (int)MathF.Floor(cx + rFringe);
        int y1 = (int)MathF.Floor(cy + rFringe);

        maskX = x0;
        maskY = y0;
        maskW = x1 - x0 + 1;
        maskH = y1 - y0 + 1;

        float oneOverRadius2 = 1.0f / (radius * radius);

        fixed (byte* maskP = mask)
        {
            if (radius < 3.0f)
            {
                float aaBorder = 1.0f;
                float rAaStart = radius > aaBorder ? (radius - aaBorder) : 0.0f;
                rAaStart = rAaStart * rAaStart / aspectRatio;

                for (int yp = y0; yp <= y1; yp++)
                {
                    for (int xp = x0; xp <= x1; xp++)
                    {
                        float rr = CalculateRrAntialiased(xp, yp, cx, cy, aspectRatio, sn, cs, oneOverRadius2, rAaStart);
                        maskP[(yp - y0) * maskW + (xp - x0)] = (byte)(CalculateOpa(rr, hardness, seg1Off, seg1Slope, seg2Off, seg2Slope) * 255.0f + 0.5f);
                    }
                }
            }
            else
            {
                for (int yp = y0; yp <= y1; yp++)
                {
                    for (int xp = x0; xp <= x1; xp++)
                    {
                        float rr = CalculateRr(xp, yp, cx, cy, aspectRatio, sn, cs, oneOverRadius2);
                        maskP[(yp - y0) * maskW + (xp - x0)] = (byte)(CalculateOpa(rr, hardness, seg1Off, seg1Slope, seg2Off, seg2Slope) * 255.0f + 0.5f);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Composites a single dab directly into a BGRA8 tile buffer.
    /// Port of draw_dab_pixels_BlendMode_Normal adapted for 8-bit BGRA.
    /// </summary>
    public static unsafe void CompositeDabNormalBgra8(
        byte* tile, int tileW, int tileH, int tileStride,
        ReadOnlySpan<ushort> mask, int maskW, int maskH, int maskX, int maskY,
        byte b, byte g, byte r, ushort opacity)
    {
        // Clip mask to tile bounds
        int startX = Math.Max(0, maskX);
        int startY = Math.Max(0, maskY);
        int endX = Math.Min(tileW, maskX + maskW);
        int endY = Math.Min(tileH, maskY + maskH);

        if (startX >= endX || startY >= endY) return;

        fixed (ushort* maskP = mask)
        {
            for (int y = startY; y < endY; y++)
            {
                byte* row = tile + y * tileStride;
                int maskRowOffset = (y - maskY) * maskW;
                for (int x = startX; x < endX; x++)
                {
                    ushort opa = (ushort)((uint)(maskP[maskRowOffset + (x - maskX)] * opacity) >> 15);
                    if (opa == 0) continue;

                    int off = x * 4;

                    // Convert 15-bit mask opacity to 8-bit for BGRA compositing.
                    byte opa8 = (byte)((opa * 255 + (One15 >> 1)) / One15);
                    if (opa8 == 0) continue;

                    int invOpa = 255 - opa8;
                    int dstA2 = row[off + 3];
                    int outA8 = opa8 + (dstA2 * invOpa + 127) / 255;
                    if (outA8 == 0) continue;

                    // Straight-alpha SrcOver (matches Floss tile format)
                    row[off + 0] = (byte)((b * opa8 + row[off + 0] * invOpa + 127) / 255);
                    row[off + 1] = (byte)((g * opa8 + row[off + 1] * invOpa + 127) / 255);
                    row[off + 2] = (byte)((r * opa8 + row[off + 2] * invOpa + 127) / 255);
                    row[off + 3] = (byte)outA8;
                }
            }
        }
    }

    /// <summary>
    /// Sample color from BGRA8 tiles for smudge/blend brushes.
    /// Port of get_color_pixels_legacy adapted for BGRA8.
    /// </summary>
    public static unsafe void GetColorBgra8(
        byte* tile, int tileW, int tileH, int tileStride,
        ReadOnlySpan<ushort> mask, int maskW, int maskH, int maskX, int maskY,
        out float r, out float g, out float b, out float a)
    {
        uint weight = 0, sumR = 0, sumG = 0, sumB = 0, sumA = 0;

        int startX = Math.Max(0, maskX);
        int startY = Math.Max(0, maskY);
        int endX = Math.Min(tileW, maskX + maskW);
        int endY = Math.Min(tileH, maskY + maskH);

        fixed (ushort* maskP = mask)
        {
            for (int y = startY; y < endY; y++)
            {
                byte* row = tile + y * tileStride;
                int maskRowOffset = (y - maskY) * maskW;
                for (int x = startX; x < endX; x++)
                {
                    ushort opa = maskP[maskRowOffset + (x - maskX)];
                    if (opa == 0) continue;
                    int off = x * 4;
                    weight += opa;
                    sumB += (uint)(opa * row[off + 0]) >> 15;
                    sumG += (uint)(opa * row[off + 1]) >> 15;
                    sumR += (uint)(opa * row[off + 2]) >> 15;
                    sumA += (uint)(opa * row[off + 3]) >> 15;
                }
            }
        }

        if (weight > 0)
        {
            r = (float)sumR / weight;
            g = (float)sumG / weight;
            b = (float)sumB / weight;
            a = (float)sumA / weight;
        }
        else
        {
            r = g = b = a = 0;
        }
    }
}
