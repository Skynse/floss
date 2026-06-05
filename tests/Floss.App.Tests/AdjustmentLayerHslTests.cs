using Floss.App.Canvas.Compositing;
using Floss.App.Document;

namespace Floss.App.Tests;

public sealed class AdjustmentLayerHslTests
{
    /// <summary>
    /// Reference: FilterEngine.ApplyHueSaturationLightness (hue degrees, sat/light −1..1).
    /// Adjustment sliders: hue degrees, sat/lum −100..100 → divide by 100 for sat/light.
    /// </summary>
    private static (byte R, byte G, byte B) FilterStyleHsl(byte r, byte g, byte b, float hueDegrees, float saturation, float lightness)
    {
        static float Wrap01(float value)
        {
            value %= 1f;
            return value < 0 ? value + 1f : value;
        }

        static (float H, float S, float L) RgbToHsl(float rf, float gf, float bf)
        {
            var max = Math.Max(rf, Math.Max(gf, bf));
            var min = Math.Min(rf, Math.Min(gf, bf));
            var l = (max + min) * 0.5f;
            if (Math.Abs(max - min) < 0.0001f) return (0f, 0f, l);
            var d = max - min;
            var s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
            float h;
            if (Math.Abs(max - rf) < 0.0001f) h = (gf - bf) / d + (gf < bf ? 6f : 0f);
            else if (Math.Abs(max - gf) < 0.0001f) h = (bf - rf) / d + 2f;
            else h = (rf - gf) / d + 4f;
            h /= 6f;
            return (h, s, l);
        }

        static (float R, float G, float B) HslToRgb(float h, float s, float l)
        {
            if (s <= 0f) return (l, l, l);
            static float HueToRgb(float p, float q, float t)
            {
                if (t < 0f) t += 1f;
                if (t > 1f) t -= 1f;
                if (t < 1f / 6f) return p + (q - p) * 6f * t;
                if (t < 1f / 2f) return q;
                if (t < 2f / 3f) return p + (q - p) * (2f / 3f - t) * 6f;
                return p;
            }
            var q = l < 0.5f ? l * (1f + s) : l + s - l * s;
            var p = 2f * l - q;
            return (HueToRgb(p, q, h + 1f / 3f), HueToRgb(p, q, h), HueToRgb(p, q, h - 1f / 3f));
        }

        var (hh, ss, ll) = RgbToHsl(r / 255f, g / 255f, b / 255f);
        hh = Wrap01(hh + hueDegrees / 360f);
        ss = saturation >= 0 ? ss + (1f - ss) * saturation : ss * (1f + saturation);
        ll = lightness >= 0 ? ll + (1f - ll) * lightness : ll * (1f + lightness);
        var (rr, gg, bb) = HslToRgb(hh, Math.Clamp(ss, 0f, 1f), Math.Clamp(ll, 0f, 1f));
        return ((byte)Math.Clamp((int)MathF.Round(rr * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(gg * 255f), 0, 255),
            (byte)Math.Clamp((int)MathF.Round(bb * 255f), 0, 255));
    }

    [Fact]
    public void TransformPixel_Identity_LeavesRgbUnchanged()
    {
        var adj = new AdjustmentLayerData { Kind = AdjustmentKind.HueSaturationLuminosity };
        foreach (var r in new byte[] { 0, 1, 64, 127, 200, 255 })
        foreach (var g in new byte[] { 0, 64, 128, 255 })
        foreach (var b in new byte[] { 0, 64, 255 })
        {
            Span<byte> px = stackalloc byte[4] { b, g, r, 255 };
            AdjustmentLayerProcessor.TransformPixel(px, adj);
            Assert.Equal(b, px[0]);
            Assert.Equal(g, px[1]);
            Assert.Equal(r, px[2]);
        }
    }

    [Theory]
    [InlineData(200, 80, 40, 90f, 50f, -25f)]
    [InlineData(10, 128, 240, -45f, -100f, 100f)]
    [InlineData(128, 128, 128, 0f, 0f, 0f)]
    [InlineData(255, 0, 0, 180f, 100f, -100f)]
    public void TransformPixel_MatchesFilterHsl(byte r, byte g, byte b, float hue, float satPct, float lumPct)
    {
        var adj = new AdjustmentLayerData
        {
            Kind = AdjustmentKind.HueSaturationLuminosity,
            Hue = hue,
            Saturation = satPct,
            Luminosity = lumPct,
        };
        Span<byte> px = stackalloc byte[4] { b, g, r, 255 };
        AdjustmentLayerProcessor.TransformPixel(px, adj);

        var expected = FilterStyleHsl(r, g, b, hue, satPct / 100f, lumPct / 100f);
        Assert.Equal(expected.R, px[2]);
        Assert.Equal(expected.G, px[1]);
        Assert.Equal(expected.B, px[0]);
    }
}
