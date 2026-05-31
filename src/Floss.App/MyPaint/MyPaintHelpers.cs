using System;

namespace Floss.App.MyPaint;

/// <summary>
/// Port of helpers.c: color space conversions, spectral upsampling, RNG helpers.
/// </summary>
public static class MyPaintHelpers
{
    private const float WgmEpsilon = 0.001f;
    private const float LumaRedCoeff = 0.2126f * 32768;
    private const float LumaGreenCoeff = 0.7152f * 32768;
    private const float LumaBlueCoeff = 0.0722f * 32768;

    private static readonly float[,] TMatrixSmall = new float[,]
    {
        {0.026595621243689f,0.049779426257903f,0.022449850859496f,-0.218453689278271f,-0.256894883201278f,0.445881722194840f,0.772365886289756f,0.194498761382537f,0.014038157587820f,0.007687264480513f},
        {-0.032601672674412f,-0.061021043498478f,-0.052490001018404f,0.206659098273522f,0.572496335158169f,0.317837248815438f,-0.021216624031211f,-0.019387668756117f,-0.001521339050858f,-0.000835181622534f},
        {0.339475473216284f,0.635401374177222f,0.771520797089589f,0.113222640692379f,-0.055251113343776f,-0.048222578468680f,-0.012966666339586f,-0.001523814504223f,-0.000094718948810f,-0.000051604594741f}
    };

    private static readonly float[] SpectralRSmall = {0.009281362787953f,0.009732627042016f,0.011254252737167f,0.015105578649573f,0.024797924177217f,0.083622585502406f,0.977865045723212f,1.000000000000000f,0.999961046144372f,0.999999992756822f};
    private static readonly float[] SpectralGSmall = {0.002854127435775f,0.003917589679914f,0.012132151699187f,0.748259205918013f,1.000000000000000f,0.865695937531795f,0.037477469241101f,0.022816789725717f,0.021747419446456f,0.021384940572308f};
    private static readonly float[] SpectralBSmall = {0.537052150373386f,0.546646402401469f,0.575501819073983f,0.258778829633924f,0.041709923751716f,0.012662638828324f,0.007485593127390f,0.006766900622462f,0.006699764779016f,0.006676219883241f};

    public static float RandGauss(MyPaintRng rng)
    {
        double sum = rng.Next() + rng.Next() + rng.Next() + rng.Next();
        return (float)(sum * 1.73205080757 - 3.46410161514);
    }

    public static float ModArith(float a, float n)
        => a - n * MathF.Floor(a / n);

    public static float SmallestAngularDifference(float angleA, float angleB)
    {
        float a = angleB - angleA;
        a = ModArith(a + 180, 360) - 180;
        if (a > 180) a -= 360;
        else if (a < -180) a += 360;
        return a;
    }

    public static void RgbToHsvFloat(ref float r, ref float g, ref float b)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float delta = max - min;
        float v = max;
        float h = 0, s = 0;
        if (delta > 0.0001f)
        {
            s = delta / max;
            if (r == max)
            {
                h = (g - b) / delta;
                if (h < 0) h += 6.0f;
            }
            else if (g == max)
                h = 2.0f + (b - r) / delta;
            else if (b == max)
                h = 4.0f + (r - g) / delta;
            h /= 6.0f;
        }
        else
        {
            s = 0;
            h = 0;
        }
        r = h; g = s; b = v;
    }

    public static void HsvToRgbFloat(ref float h, ref float s, ref float v)
    {
        float hh = h - MathF.Floor(h);
        s = Clamp(s, 0, 1);
        v = Clamp(v, 0, 1);
        float r = 0, g = 0, b = 0;
        if (s == 0)
        {
            r = g = b = v;
        }
        else
        {
            float hue = hh;
            if (hue == 1.0f) hue = 0;
            hue *= 6.0f;
            int i = (int)hue;
            float f = hue - i;
            float w = v * (1.0f - s);
            float q = v * (1.0f - (s * f));
            float t = v * (1.0f - (s * (1.0f - f)));
            switch (i)
            {
                case 0: r = v; g = t; b = w; break;
                case 1: r = q; g = v; b = w; break;
                case 2: r = w; g = v; b = t; break;
                case 3: r = w; g = q; b = v; break;
                case 4: r = t; g = w; b = v; break;
                case 5: r = v; g = w; b = q; break;
            }
        }
        h = r; s = g; v = b;
    }

    public static void RgbToHslFloat(ref float r, ref float g, ref float b)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float l = (max + min) / 2.0f;
        float h = 0, s = 0;
        if (max != min)
        {
            float delta = max - min;
            s = l <= 0.5f ? delta / (max + min) : delta / (2.0f - max - min);
            if (r == max) h = (g - b) / delta;
            else if (g == max) h = 2.0f + (b - r) / delta;
            else if (b == max) h = 4.0f + (r - g) / delta;
            h /= 6.0f;
            if (h < 0) h += 1.0f;
        }
        r = h; g = s; b = l;
    }

    public static void HslToRgbFloat(ref float h, ref float s, ref float l)
    {
        float hh = h - MathF.Floor(h);
        s = Clamp(s, 0, 1);
        l = Clamp(l, 0, 1);
        float r, g, b;
        if (s == 0)
        {
            r = g = b = l;
        }
        else
        {
            float m2 = l <= 0.5f ? l * (1.0f + s) : l + s - l * s;
            float m1 = 2.0f * l - m2;
            r = HslValue(m1, m2, hh * 6.0f + 2.0f);
            g = HslValue(m1, m2, hh * 6.0f);
            b = HslValue(m1, m2, hh * 6.0f - 2.0f);
        }
        h = r; s = g; l = b;
    }

    private static float HslValue(float n1, float n2, float hue)
    {
        if (hue > 6.0f) hue -= 6.0f;
        else if (hue < 0.0f) hue += 6.0f;
        if (hue < 1.0f) return n1 + (n2 - n1) * hue;
        if (hue < 3.0f) return n2;
        if (hue < 4.0f) return n1 + (n2 - n1) * (4.0f - hue);
        return n1;
    }

    public static void RgbToSpectral(float r, float g, float b, Span<float> spectral)
    {
        float offset = 1.0f - WgmEpsilon;
        r = r * offset + WgmEpsilon;
        g = g * offset + WgmEpsilon;
        b = b * offset + WgmEpsilon;
        for (int i = 0; i < 10; i++)
            spectral[i] += SpectralRSmall[i] * r + SpectralGSmall[i] * g + SpectralBSmall[i] * b;
    }

    public static void SpectralToRgb(ReadOnlySpan<float> spectral, Span<float> rgb)
    {
        float offset = 1.0f - WgmEpsilon;
        float tmp0 = 0, tmp1 = 0, tmp2 = 0;
        for (int i = 0; i < 10; i++)
        {
            tmp0 += TMatrixSmall[0, i] * spectral[i];
            tmp1 += TMatrixSmall[1, i] * spectral[i];
            tmp2 += TMatrixSmall[2, i] * spectral[i];
        }
        rgb[0] = Clamp((tmp0 - WgmEpsilon) / offset, 0, 1);
        rgb[1] = Clamp((tmp1 - WgmEpsilon) / offset, 0, 1);
        rgb[2] = Clamp((tmp2 - WgmEpsilon) / offset, 0, 1);
    }

    public static float[] MixColors(float[] a, float[] b, float fac, float paintMode)
    {
        float[] result = new float[4];
        float opaA = fac;
        float opaB = 1.0f - opaA;
        result[3] = Clamp(opaA * a[3] + opaB * b[3], 0, 1);
        float sfacA = a[3] == 0 ? 0.0f : opaA * a[3] / (a[3] + b[3] * opaB);
        float sfacB = 1 - sfacA;

        if (paintMode > 0.0f)
        {
            float[] specA = new float[10];
            float[] specB = new float[10];
            float[] spectralMix = new float[10];
            RgbToSpectral(a[0], a[1], a[2], specA);
            RgbToSpectral(b[0], b[1], b[2], specB);
            for (int i = 0; i < 10; i++)
                spectralMix[i] = MathF.Pow(specA[i], sfacA) * MathF.Pow(specB[i], sfacB);
            float[] rgbResult = new float[3];
            SpectralToRgb(spectralMix, rgbResult);
            for (int i = 0; i < 3; i++) result[i] = rgbResult[i];
        }

        if (paintMode < 1.0f)
        {
            for (int i = 0; i < 3; i++)
                result[i] = result[i] * paintMode + (1 - paintMode) * (a[i] * opaA + b[i] * opaB);
        }

        return result;
    }

    private static float Luma(float r, float g, float b)
        => r * LumaRedCoeff + g * LumaGreenCoeff + b * LumaBlueCoeff;

    public static void SetRgb16LumFromRgb16(ushort topr, ushort topg, ushort topb, ref ushort botr, ref ushort botg, ref ushort botb)
    {
        float botlum = Luma(botr, botg, botb) / 32768.0f;
        float toplum = Luma(topr, topg, topb) / 32768.0f;
        float diff = botlum - toplum;
        int r = (int)(topr + diff);
        int g = (int)(topg + diff);
        int b = (int)(topb + diff);
        int lum = (int)(Luma(r, g, b) / 32768.0f);
        int cmin = Math.Min(r, Math.Min(g, b));
        int cmax = Math.Max(r, Math.Max(g, b));
        if (cmin < 0)
        {
            int denom = lum - cmin;
            if (denom != 0)
            {
                r = lum + ((r - lum) * lum) / denom;
                g = lum + ((g - lum) * lum) / denom;
                b = lum + ((b - lum) * lum) / denom;
            }
        }
        if (cmax > 32768)
        {
            int denom = cmax - lum;
            if (denom != 0)
            {
                r = lum + ((r - lum) * (32768 - lum)) / denom;
                g = lum + ((g - lum) * (32768 - lum)) / denom;
                b = lum + ((b - lum) * (32768 - lum)) / denom;
            }
        }
        botr = (ushort)Clamp(r, 0, 32768);
        botg = (ushort)Clamp(g, 0, 32768);
        botb = (ushort)Clamp(b, 0, 32768);
    }

    public static float Clamp(float x, float low, float high)
        => x > high ? high : (x < low ? low : x);
    public static int Clamp(int x, int low, int high)
        => x > high ? high : (x < low ? low : x);
    public static float Max3(float a, float b, float c) => Math.Max(a, Math.Max(b, c));
    public static float Min3(float a, float b, float c) => Math.Min(a, Math.Min(b, c));
}
