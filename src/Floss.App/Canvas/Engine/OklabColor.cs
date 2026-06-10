using System;
using System.Runtime.CompilerServices;

namespace Floss.App.Canvas.Engine;

/// <summary>
/// Oklab color space conversions and perceptual blending.
/// Modeled on Oklab pigment mixing ().
/// Oklab provides perceptually uniform blending — colors mix naturally
/// like real pigments instead of washing out or producing muddy grays.
/// </summary>
public static class OklabColor
{
    // ── sRGB linear ←→ Oklab ──────────────────────────────────────────────────

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float SrgbToLinear(float c)
    {
        if (c <= 0.04045f)
            return c / 12.92f;
        return MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float LinearToSrgb(float c)
    {
        if (c <= 0.0031308f)
            return c * 12.92f;
        return 1.055f * MathF.Pow(c, 1.0f / 2.4f) - 0.055f;
    }

    /// <summary>Convert sRGB (0..1 per channel) to Oklab.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (float L, float a, float b) SrgbToOklab(float r, float g, float b)
    {
        r = SrgbToLinear(r);
        g = SrgbToLinear(g);
        b = SrgbToLinear(b);

        // LMS matrix
        float l = 0.4122214708f * r + 0.5363325363f * g + 0.0514459929f * b;
        float m = 0.2119034982f * r + 0.6806995451f * g + 0.1073969566f * b;
        float s = 0.0883024619f * r + 0.2817188376f * g + 0.6299787005f * b;

        // Non-linearity: cube root approximation
        l = MathF.Cbrt(l);
        m = MathF.Cbrt(m);
        s = MathF.Cbrt(s);

        // From LMS to Oklab
        float oL = 0.2104542553f * l + 0.7936177850f * m - 0.0040720468f * s;
        float oa = 1.9779984951f * l - 2.4285922050f * m + 0.4505937099f * s;
        float ob = 0.0259040371f * l + 0.7827717662f * m - 0.8086757660f * s;

        return (oL, oa, ob);
    }

    /// <summary>Convert Oklab back to sRGB (0..1 per channel).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (float r, float g, float b) OklabToSrgb(float L, float A, float B)
    {
        float l = L + 0.3963377774f * A + 0.2158037573f * B;
        float m = L - 0.1055613458f * A - 0.0638541728f * B;
        float s = L - 0.0894841775f * A - 1.2914855480f * B;

        l = l * l * l;
        m = m * m * m;
        s = s * s * s;

        float r = +4.0767416621f * l - 3.3077115913f * m + 0.2309699292f * s;
        float g = -1.2684380046f * l + 2.6097574011f * m - 0.3413193965f * s;
        float b_out = -0.0041960863f * l - 0.7034186147f * m + 1.7076147010f * s;

        return (LinearToSrgb(r), LinearToSrgb(g), LinearToSrgb(b_out));
    }

    // ── Pigment mixing ────────────────────────────────────────────────────────

    /// <summary>
    /// Mix two colors in Oklab space using geometric pigment-like blending.
    /// srcWeight controls how much of the source color is mixed in (0..1).
    /// The blend preserves lightness for natural pigment behavior, matching
    /// .
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static (float r, float g, float b) PigmentMix(
        float sr, float sg, float sbl,
        float dr, float dg, float dbl,
        float srcWeight)
    {
        if (srcWeight <= 0f) return (dr, dg, dbl);
        if (srcWeight >= 1f) return (sr, sg, sbl);

        var (sL, sa, sab) = SrgbToOklab(sr, sg, sbl);
        var (dL, da, dab) = SrgbToOklab(dr, dg, dbl);

        var rL = dL + (sL - dL) * srcWeight;
        var ra = da + (sa - da) * srcWeight;
        var rab = dab + (sab - dab) * srcWeight;

        return OklabToSrgb(rL, ra, rab);
    }

    /// <summary>
    /// Blend byte colors using Oklab pigment mixing.
    /// srcWeight = 0..255 maps to 0.0..1.0. Result is packed BGRA uint (alpha=255).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static uint PigmentMixBytes(
        byte sb, byte sg, byte sr,
        byte db, byte dg, byte dr,
        int weight) // 0..255
    {
        if (weight <= 0) return (uint)(db | (dg << 8) | (dr << 16) | (255 << 24));
        if (weight >= 255) return (uint)(sb | (sg << 8) | (sr << 16) | (255 << 24));

        float w = weight / 255f;
        var (r, g, b) = PigmentMix(
            sr / 255f, sg / 255f, sb / 255f,
            dr / 255f, dg / 255f, db / 255f,
            w);

        byte or = (byte)(Math.Clamp(r * 255f, 0, 255));
        byte og = (byte)(Math.Clamp(g * 255f, 0, 255));
        byte ob = (byte)(Math.Clamp(b * 255f, 0, 255));

        return (uint)(ob | (og << 8) | (or << 16) | (255 << 24));
    }
}
