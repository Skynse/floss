using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using SkiaSharp;
using Floss.App.Document;
using Floss.App.Tools;

namespace Floss.App.Filters;

public static class FilterEngine
{
    public static void ApplyGaussianBlur(DrawingLayer layer, float sigma, SelectionMask? sel = null)
    {
        using var filter = SKImageFilter.CreateBlur(sigma, sigma);
        ApplySkiaFilter(layer, filter, sel);
    }

    public static void ApplyChromaticAbberation(DrawingLayer layer, float intensity, bool is_radial, float angle = 0f, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var orig = layer.CapturePixels();
        var result = new byte[orig.Length];

        if (is_radial)
        {
            float cx = w / 2f;
            float cy = h / 2f;
            float scale = intensity / MathF.Sqrt(cx * cx + cy * cy);
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    if (sel != null && sel.HasSelection && !sel.IsSelected(x, y))
                    {
                        result[i] = orig[i];
                        result[i + 1] = orig[i + 1];
                        result[i + 2] = orig[i + 2];
                        result[i + 3] = orig[i + 3];
                        continue;
                    }

                    var shiftX = (x - cx) * scale;
                    var shiftY = (y - cy) * scale;

                    int rx = Math.Clamp((int)(x + shiftX), 0, w - 1);
                    int ry = Math.Clamp((int)(y + shiftY), 0, h - 1);
                    int bx = Math.Clamp((int)(x - shiftX), 0, w - 1);
                    int by = Math.Clamp((int)(y - shiftY), 0, h - 1);

                    result[i] = orig[((by * w + bx) * 4)];
                    result[i + 1] = orig[i + 1];
                    result[i + 2] = orig[((ry * w + rx) * 4) + 2];
                    result[i + 3] = orig[i + 3];
                }
            });
        }
        else
        {
            float rad = angle * MathF.PI / 180f;
            var latShiftX = MathF.Cos(rad) * intensity;
            var latShiftY = MathF.Sin(rad) * intensity;
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    if (sel != null && sel.HasSelection && !sel.IsSelected(x, y))
                    {
                        result[i] = orig[i];
                        result[i + 1] = orig[i + 1];
                        result[i + 2] = orig[i + 2];
                        result[i + 3] = orig[i + 3];
                        continue;
                    }

                    int rx = Math.Clamp((int)(x + latShiftX), 0, w - 1);
                    int ry = Math.Clamp((int)(y + latShiftY), 0, h - 1);
                    int bx = Math.Clamp((int)(x - latShiftX), 0, w - 1);
                    int by = Math.Clamp((int)(y - latShiftY), 0, h - 1);

                    result[i] = orig[((by * w + bx) * 4)];
                    result[i + 1] = orig[i + 1];
                    result[i + 2] = orig[((ry * w + rx) * 4) + 2];
                    result[i + 3] = orig[i + 3];
                }
            });
        }

        WriteBytes(layer, result, w, h);
    }

    public static void ApplySharpen(DrawingLayer layer, float amount, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var orig = layer.CapturePixels();

        using var srcBmp = ToSKBitmap(orig, w, h);
        using var blurredBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var blurCanvas = new SKCanvas(blurredBmp);
        using var blurPaint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(1.5f, 1.5f) };
        blurCanvas.DrawBitmap(srcBmp, 0, 0, blurPaint);

        var blurred = new byte[w * h * 4];
        Marshal.Copy(blurredBmp.GetPixels(), blurred, 0, blurred.Length);

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                var i = (y * w + x) * 4;
                for (var c = 0; c < 3; c++)
                {
                    var v = orig[i + c] + (int)(amount * (orig[i + c] - blurred[i + c]));
                    orig[i + c] = (byte)Math.Clamp(v, 0, 255);
                }
            }
        });

        WriteBytes(layer, orig, w, h);
    }

    public static void ApplyNoise(DrawingLayer layer, float amount, bool monochromatic, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var pixels = layer.CapturePixels();
        var strength = (int)(amount * 255);
        var seed = Environment.TickCount;

        if (monochromatic)
        {
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    if (pixels[i + 3] == 0) continue;
                    if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                    var n = (int)((FastHashNoise01(x, y, seed) * 2f - 1f) * strength);
                    pixels[i + 0] = (byte)Math.Clamp(pixels[i + 0] + n, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] + n, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] + n, 0, 255);
                }
            });
        }
        else
        {
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    var i = (y * w + x) * 4;
                    if (pixels[i + 3] == 0) continue;
                    if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                    pixels[i + 0] = (byte)Math.Clamp(pixels[i + 0] + (int)((FastHashNoise01(x, y, seed | 0) * 2f - 1f) * strength), 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] + (int)((FastHashNoise01(x, y, seed | 1) * 2f - 1f) * strength), 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] + (int)((FastHashNoise01(x, y, seed | 2) * 2f - 1f) * strength), 0, 255);
                }
            });
        }

        WriteBytes(layer, pixels, w, h);
    }

    public static void ApplyBrightnessContrast(DrawingLayer layer, float brightness, float contrast, SelectionMask? sel = null)
    {
        var curve = contrast >= 0
            ? 1f + contrast * 3f
            : 1f + contrast * 0.75f;
        var offset = brightness * 255f;
        ApplyRgbTransform(layer, sel, (r, g, b) => (
            ClampByte(((r - 127.5f) * curve) + 127.5f + offset),
            ClampByte(((g - 127.5f) * curve) + 127.5f + offset),
            ClampByte(((b - 127.5f) * curve) + 127.5f + offset)));
    }

    public static void ApplyExposureGamma(DrawingLayer layer, float exposureStops, float gamma, SelectionMask? sel = null)
    {
        var exposure = MathF.Pow(2f, exposureStops);
        var invGamma = 1f / MathF.Max(0.05f, gamma);
        ApplyRgbTransform(layer, sel, (r, g, b) => (
            ClampByte(MathF.Pow(Math.Clamp(r / 255f * exposure, 0f, 1f), invGamma) * 255f),
            ClampByte(MathF.Pow(Math.Clamp(g / 255f * exposure, 0f, 1f), invGamma) * 255f),
            ClampByte(MathF.Pow(Math.Clamp(b / 255f * exposure, 0f, 1f), invGamma) * 255f)));
    }

    public static void ApplyLevels(DrawingLayer layer, int black, float gamma, int white, int outputBlack, int outputWhite, SelectionMask? sel = null)
    {
        white = Math.Max(black + 1, white);
        gamma = MathF.Max(0.05f, gamma);
        var outRange = outputWhite - outputBlack;

        byte Map(byte value)
        {
            var normalized = Math.Clamp((value - black) / (float)(white - black), 0f, 1f);
            var corrected = MathF.Pow(normalized, 1f / gamma);
            return ClampByte(outputBlack + corrected * outRange);
        }

        ApplyRgbTransform(layer, sel, (r, g, b) => (Map(r), Map(g), Map(b)));
    }

    public static void ApplyHueSaturationLightness(DrawingLayer layer, float hueDegrees, float saturation, float lightness, SelectionMask? sel = null)
    {
        ApplyRgbTransform(layer, sel, (r, g, b) =>
        {
            var (h, s, l) = RgbToHsl(r / 255f, g / 255f, b / 255f);
            h = Wrap01(h + hueDegrees / 360f);
            s = saturation >= 0 ? s + (1f - s) * saturation : s * (1f + saturation);
            l = lightness >= 0 ? l + (1f - l) * lightness : l * (1f + lightness);
            var (rr, gg, bb) = HslToRgb(h, Math.Clamp(s, 0f, 1f), Math.Clamp(l, 0f, 1f));
            return (ClampByte(rr * 255f), ClampByte(gg * 255f), ClampByte(bb * 255f));
        });
    }

    public static void ApplyInvert(DrawingLayer layer, SelectionMask? sel = null)
        => ApplyRgbTransform(layer, sel, (r, g, b) => ((byte)(255 - r), (byte)(255 - g), (byte)(255 - b)));

    public static void ApplyDesaturate(DrawingLayer layer, SelectionMask? sel = null)
        => ApplyRgbTransform(layer, sel, (r, g, b) =>
        {
            var luma = ClampByte(0.2126f * r + 0.7152f * g + 0.0722f * b);
            return (luma, luma, luma);
        });

    public static void ApplySepia(DrawingLayer layer, float amount, SelectionMask? sel = null)
        => ApplyRgbTransform(layer, sel, (r, g, b) =>
        {
            var sr = ClampByte(r * 0.393f + g * 0.769f + b * 0.189f);
            var sg = ClampByte(r * 0.349f + g * 0.686f + b * 0.168f);
            var sb = ClampByte(r * 0.272f + g * 0.534f + b * 0.131f);
            return (
                LerpByte(r, sr, amount),
                LerpByte(g, sg, amount),
                LerpByte(b, sb, amount));
        });

    public static void ApplyThreshold(DrawingLayer layer, byte threshold, SelectionMask? sel = null)
        => ApplyRgbTransform(layer, sel, (r, g, b) =>
        {
            var v = (byte)((0.2126f * r + 0.7152f * g + 0.0722f * b) >= threshold ? 255 : 0);
            return (v, v, v);
        });

    public static void ApplyPosterize(DrawingLayer layer, int levels, SelectionMask? sel = null)
    {
        levels = Math.Clamp(levels, 2, 32);
        byte Map(byte v) => ClampByte(MathF.Round(v / 255f * (levels - 1)) / (levels - 1) * 255f);
        ApplyRgbTransform(layer, sel, (r, g, b) => (Map(r), Map(g), Map(b)));
    }

    public static void ApplyPixelate(DrawingLayer layer, int blockSize, SelectionMask? sel = null)
    {
        blockSize = Math.Clamp(blockSize, 2, 256);
        var w = layer.Width;
        var h = layer.Height;
        var pixels = layer.CapturePixels();

        for (var by = 0; by < h; by += blockSize)
        {
            for (var bx = 0; bx < w; bx += blockSize)
            {
                long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                var count = 0;
                var right = Math.Min(w, bx + blockSize);
                var bottom = Math.Min(h, by + blockSize);

                for (var y = by; y < bottom; y++)
                {
                    for (var x = bx; x < right; x++)
                    {
                        if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                        var i = (y * w + x) * 4;
                        if (pixels[i + 3] == 0) continue;
                        sumB += pixels[i + 0];
                        sumG += pixels[i + 1];
                        sumR += pixels[i + 2];
                        sumA += pixels[i + 3];
                        count++;
                    }
                }

                if (count == 0) continue;
                var b = (byte)(sumB / count);
                var g = (byte)(sumG / count);
                var r = (byte)(sumR / count);
                var a = (byte)(sumA / count);

                for (var y = by; y < bottom; y++)
                {
                    for (var x = bx; x < right; x++)
                    {
                        if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                        var i = (y * w + x) * 4;
                        pixels[i + 0] = b;
                        pixels[i + 1] = g;
                        pixels[i + 2] = r;
                        pixels[i + 3] = a;
                    }
                }
            }
        }

        WriteBytes(layer, pixels, w, h);
    }

    public static void ApplyVignette(DrawingLayer layer, float strength, float radius, float softness, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var cx = (w - 1) * 0.5f;
        var cy = (h - 1) * 0.5f;
        var maxDistance = MathF.Sqrt(cx * cx + cy * cy);
        radius = Math.Clamp(radius, 0.05f, 1f);
        softness = Math.Clamp(softness, 0.01f, 1f);

        ApplyRgbTransform(layer, sel, (x, y, r, g, b) =>
        {
            var dx = x - cx;
            var dy = y - cy;
            var d = MathF.Sqrt(dx * dx + dy * dy) / maxDistance;
            var t = SmoothStep(radius, Math.Min(1f, radius + softness), d) * strength;
            var scale = 1f - Math.Clamp(t, 0f, 1f);
            return (ClampByte(r * scale), ClampByte(g * scale), ClampByte(b * scale));
        });
    }

    public static void ApplyBloom(DrawingLayer layer, float radius, float intensity, byte threshold, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var pixels = layer.CapturePixels();
        var bright = new byte[pixels.Length];

        for (var i = 0; i < pixels.Length; i += 4)
        {
            var luma = 0.2126f * pixels[i + 2] + 0.7152f * pixels[i + 1] + 0.0722f * pixels[i + 0];
            if (luma < threshold || pixels[i + 3] == 0) continue;
            bright[i + 0] = pixels[i + 0];
            bright[i + 1] = pixels[i + 1];
            bright[i + 2] = pixels[i + 2];
            bright[i + 3] = pixels[i + 3];
        }

        using var srcBmp = ToSKBitmap(bright, w, h);
        using var glowBmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using (var canvas = new SKCanvas(glowBmp))
        using (var paint = new SKPaint { ImageFilter = SKImageFilter.CreateBlur(radius, radius) })
        {
            canvas.DrawBitmap(srcBmp, 0, 0, paint);
        }

        var glow = new byte[pixels.Length];
        Marshal.Copy(glowBmp.GetPixels(), glow, 0, glow.Length);

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                var i = (y * w + x) * 4;
                if (pixels[i + 3] == 0) continue;
                pixels[i + 0] = ClampByte(pixels[i + 0] + glow[i + 0] * intensity);
                pixels[i + 1] = ClampByte(pixels[i + 1] + glow[i + 1] * intensity);
                pixels[i + 2] = ClampByte(pixels[i + 2] + glow[i + 2] * intensity);
            }
        });

        WriteBytes(layer, pixels, w, h);
    }

    public static void ApplyMotionBlur(DrawingLayer layer, int length, float angleDegrees, SelectionMask? sel = null)
    {
        length = Math.Clamp(length, 1, 128);
        var w = layer.Width;
        var h = layer.Height;
        var source = layer.CapturePixels();
        var result = (byte[])source.Clone();
        var rad = angleDegrees * MathF.PI / 180f;
        var dx = MathF.Cos(rad);
        var dy = MathF.Sin(rad);
        int radius = length / 2;
        int rightEdge = length - radius - 1;

        if (MathF.Abs(dy) < 0.005f)
        {
            if (sel == null || !sel.HasSelection)
            {
                Parallel.For(0, h, y =>
                {
                    long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    for (int k = 0; k < length; k++)
                    {
                        var sx = Math.Clamp(k - radius, 0, w - 1);
                        var si = (y * w + sx) * 4;
                        sumB += source[si + 0]; sumG += source[si + 1]; sumR += source[si + 2]; sumA += source[si + 3];
                    }
                    var d0 = (y * w) * 4;
                    result[d0 + 0] = (byte)(sumB / length); result[d0 + 1] = (byte)(sumG / length);
                    result[d0 + 2] = (byte)(sumR / length); result[d0 + 3] = (byte)(sumA / length);

                    for (var x = 1; x < w; x++)
                    {
                        var remX = Math.Clamp(x - 1 - radius, 0, w - 1);
                        var addX = Math.Clamp(x + rightEdge, 0, w - 1);
                        var ri = (y * w + remX) * 4;
                        var ai = (y * w + addX) * 4;
                        sumB += source[ai + 0] - source[ri + 0];
                        sumG += source[ai + 1] - source[ri + 1];
                        sumR += source[ai + 2] - source[ri + 2];
                        sumA += source[ai + 3] - source[ri + 3];
                        var di = (y * w + x) * 4;
                        result[di + 0] = (byte)(sumB / length); result[di + 1] = (byte)(sumG / length);
                        result[di + 2] = (byte)(sumR / length); result[di + 3] = (byte)(sumA / length);
                    }
                });
            }
            else
            {
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        if (!sel.IsSelected(x, y)) continue;
                        long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                        for (var step = 0; step < length; step++)
                        {
                            var sx = Math.Clamp((int)MathF.Round(x + dx * (step - radius)), 0, w - 1);
                            var si = (y * w + sx) * 4;
                            sumB += source[si + 0]; sumG += source[si + 1];
                            sumR += source[si + 2]; sumA += source[si + 3];
                        }
                        var i = (y * w + x) * 4;
                        result[i + 0] = (byte)(sumB / length); result[i + 1] = (byte)(sumG / length);
                        result[i + 2] = (byte)(sumR / length); result[i + 3] = (byte)(sumA / length);
                    }
            }
        }
        else if (MathF.Abs(dx) < 0.005f)
        {
            if (sel == null || !sel.HasSelection)
            {
                for (var x = 0; x < w; x++)
                {
                    long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    for (int k = 0; k < length; k++)
                    {
                        var sy = Math.Clamp(k - radius, 0, h - 1);
                        var si = (sy * w + x) * 4;
                        sumB += source[si + 0]; sumG += source[si + 1]; sumR += source[si + 2]; sumA += source[si + 3];
                    }
                    var d0 = x * 4;
                    result[d0 + 0] = (byte)(sumB / length); result[d0 + 1] = (byte)(sumG / length);
                    result[d0 + 2] = (byte)(sumR / length); result[d0 + 3] = (byte)(sumA / length);

                    for (var y = 1; y < h; y++)
                    {
                        var remY = Math.Clamp(y - 1 - radius, 0, h - 1);
                        var addY = Math.Clamp(y + rightEdge, 0, h - 1);
                        var ri = (remY * w + x) * 4;
                        var ai = (addY * w + x) * 4;
                        sumB += source[ai + 0] - source[ri + 0];
                        sumG += source[ai + 1] - source[ri + 1];
                        sumR += source[ai + 2] - source[ri + 2];
                        sumA += source[ai + 3] - source[ri + 3];
                        var di = (y * w + x) * 4;
                        result[di + 0] = (byte)(sumB / length); result[di + 1] = (byte)(sumG / length);
                        result[di + 2] = (byte)(sumR / length); result[di + 3] = (byte)(sumA / length);
                    }
                }
            }
            else
            {
                for (var y = 0; y < h; y++)
                    for (var x = 0; x < w; x++)
                    {
                        if (!sel.IsSelected(x, y)) continue;
                        long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                        for (var step = 0; step < length; step++)
                        {
                            var sy = Math.Clamp((int)MathF.Round(y + dy * (step - radius)), 0, h - 1);
                            var si = (sy * w + x) * 4;
                            sumB += source[si + 0]; sumG += source[si + 1];
                            sumR += source[si + 2]; sumA += source[si + 3];
                        }
                        var i = (y * w + x) * 4;
                        result[i + 0] = (byte)(sumB / length); result[i + 1] = (byte)(sumG / length);
                        result[i + 2] = (byte)(sumR / length); result[i + 3] = (byte)(sumA / length);
                    }
            }
        }
        else
        {
            var stride = Math.Max(1, length / 12);
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                    long sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    var count = 0;
                    for (var step = 0; step < length; step += stride)
                    {
                        var sample = step - radius;
                        var sx = Math.Clamp((int)MathF.Round(x + dx * sample), 0, w - 1);
                        var sy = Math.Clamp((int)MathF.Round(y + dy * sample), 0, h - 1);
                        var si = (sy * w + sx) * 4;
                        sumB += source[si + 0]; sumG += source[si + 1];
                        sumR += source[si + 2]; sumA += source[si + 3];
                        count++;
                    }
                    var i = (y * w + x) * 4;
                    result[i + 0] = (byte)(sumB / count); result[i + 1] = (byte)(sumG / count);
                    result[i + 2] = (byte)(sumR / count); result[i + 3] = (byte)(sumA / count);
                }
            });
        }

        WriteBytes(layer, result, w, h);
    }

    public static void ApplyEmboss(DrawingLayer layer, float amount, SelectionMask? sel = null)
        => ApplyKernel3(layer, sel, [
            -2 * amount, -1 * amount, 0,
            -1 * amount, 1, 1 * amount,
            0, 1 * amount, 2 * amount
        ], bias: 128f * amount);

    public static void ApplyEdgeDetect(DrawingLayer layer, float amount, SelectionMask? sel = null)
        => ApplyKernel3(layer, sel, [
            -1 * amount, -1 * amount, -1 * amount,
            -1 * amount, 8 * amount, -1 * amount,
            -1 * amount, -1 * amount, -1 * amount
        ], bias: 0f, grayscale: true);

    // lutMaster applied first (combined RGB), then per-channel luts.
    public static void ApplyCurves(DrawingLayer layer, byte[] lutMaster, byte[] lutR, byte[] lutG, byte[] lutB, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var pixels = layer.CapturePixels();

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (pixels[i + 3] == 0) continue;
                if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                var b = lutMaster[pixels[i + 0]];
                var g = lutMaster[pixels[i + 1]];
                var r = lutMaster[pixels[i + 2]];
                pixels[i + 0] = lutB[b];
                pixels[i + 1] = lutG[g];
                pixels[i + 2] = lutR[r];
            }
        });

        WriteBytes(layer, pixels, w, h);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private delegate (byte R, byte G, byte B) RgbTransform(byte r, byte g, byte b);
    private delegate (byte R, byte G, byte B) SpatialRgbTransform(int x, int y, byte r, byte g, byte b);

    private static void ApplyRgbTransform(DrawingLayer layer, SelectionMask? sel, RgbTransform transform)
        => ApplyRgbTransform(layer, sel, (x, y, r, g, b) => transform(r, g, b));

    private static void ApplyRgbTransform(DrawingLayer layer, SelectionMask? sel, SpatialRgbTransform transform)
    {
        var w = layer.Width;
        var h = layer.Height;
        var pixels = layer.CapturePixels();

        Parallel.For(0, h, y =>
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (pixels[i + 3] == 0) continue;
                if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                var (r, g, b) = transform(x, y, pixels[i + 2], pixels[i + 1], pixels[i + 0]);
                pixels[i + 0] = b;
                pixels[i + 1] = g;
                pixels[i + 2] = r;
            }
        });

        WriteBytes(layer, pixels, w, h);
    }

    private static void ApplyKernel3(DrawingLayer layer, SelectionMask? sel, float[] kernel, float bias, bool grayscale = false)
    {
        var w = layer.Width;
        var h = layer.Height;
        var source = layer.CapturePixels();
        var result = (byte[])source.Clone();

        // Detect separable kernel: row vector [kr0,kr1,kr2] × col vector [kc0,kc1,kc2]
        bool separable = false;
        float kr0 = 0, kr1 = 0, kr2 = 0, kc0 = 0, kc1 = 0, kc2 = 0;
        if (MathF.Abs(kernel[4]) > 0.0001f)
        {
            float k00 = kernel[0], k01 = kernel[1], k02 = kernel[2];
            float k10 = kernel[3], k11 = kernel[4], k12 = kernel[5];
            float k20 = kernel[6], k21 = kernel[7], k22 = kernel[8];
            kc0 = MathF.Sqrt(k00 > 0 ? k00 : -k00) * MathF.Sign(k00);
            kc1 = MathF.Sqrt(k11 > 0 ? k11 : -k11) * MathF.Sign(k11);
            kc2 = MathF.Sqrt(k22 > 0 ? k22 : -k22) * MathF.Sign(k22);
            kr0 = k00 / (kc0 == 0 ? 1 : kc0);
            kr1 = k11 / (kc1 == 0 ? 1 : kc1);
            kr2 = k22 / (kc2 == 0 ? 1 : kc2);
            float eps = 0.001f;
            separable = MathF.Abs(kr0 * kc0 - k00) < eps && MathF.Abs(kr0 * kc1 - k01) < eps && MathF.Abs(kr0 * kc2 - k02) < eps
                     && MathF.Abs(kr1 * kc0 - k10) < eps && MathF.Abs(kr1 * kc1 - k11) < eps && MathF.Abs(kr1 * kc2 - k12) < eps
                     && MathF.Abs(kr2 * kc0 - k20) < eps && MathF.Abs(kr2 * kc1 - k21) < eps && MathF.Abs(kr2 * kc2 - k22) < eps;
        }

        if (separable)
        {
            // Two-pass separable convolution: horizontal then vertical
            var temp = new float[w * h * 3];
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                    float b = 0, g = 0, r = 0;
                    for (var ox = -1; ox <= 1; ox++)
                    {
                        var sx = Math.Clamp(x + ox, 0, w - 1);
                        var si = (y * w + sx) * 4;
                        var kw = ox == -1 ? kr0 : ox == 0 ? kr1 : kr2;
                        b += source[si + 0] * kw;
                        g += source[si + 1] * kw;
                        r += source[si + 2] * kw;
                    }
                    var ti = (y * w + x) * 3;
                    temp[ti] = b; temp[ti + 1] = g; temp[ti + 2] = r;
                }
            });

            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                    float b = bias, g = bias, r = bias;
                    for (var oy = -1; oy <= 1; oy++)
                    {
                        var sy = Math.Clamp(y + oy, 0, h - 1);
                        var ti = (sy * w + x) * 3;
                        var kw = oy == -1 ? kc0 : oy == 0 ? kc1 : kc2;
                        b += temp[ti] * kw;
                        g += temp[ti + 1] * kw;
                        r += temp[ti + 2] * kw;
                    }
                    var i = (y * w + x) * 4;
                    if (grayscale)
                    {
                        var v = ClampByte(0.2126f * r + 0.7152f * g + 0.0722f * b);
                        result[i + 0] = v; result[i + 1] = v; result[i + 2] = v;
                    }
                    else
                    {
                        result[i + 0] = ClampByte(b);
                        result[i + 1] = ClampByte(g);
                        result[i + 2] = ClampByte(r);
                    }
                }
            });
        }
        else
        {
            Parallel.For(0, h, y =>
            {
                for (var x = 0; x < w; x++)
                {
                    if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                    float b = bias, g = bias, r = bias;
                    var k = 0;
                    for (var oy = -1; oy <= 1; oy++)
                    {
                        var sy = Math.Clamp(y + oy, 0, h - 1);
                        for (var ox = -1; ox <= 1; ox++, k++)
                        {
                            var sx = Math.Clamp(x + ox, 0, w - 1);
                            var si = (sy * w + sx) * 4;
                            b += source[si + 0] * kernel[k];
                            g += source[si + 1] * kernel[k];
                            r += source[si + 2] * kernel[k];
                        }
                    }

                    var i = (y * w + x) * 4;
                    if (grayscale)
                    {
                        var v = ClampByte(0.2126f * r + 0.7152f * g + 0.0722f * b);
                        result[i + 0] = v; result[i + 1] = v; result[i + 2] = v;
                    }
                    else
                    {
                        result[i + 0] = ClampByte(b);
                        result[i + 1] = ClampByte(g);
                        result[i + 2] = ClampByte(r);
                    }
                }
            });
        }

        WriteBytes(layer, result, w, h);
    }

    private static void ApplySkiaFilter(DrawingLayer layer, SKImageFilter filter, SelectionMask? sel)
    {
        var w = layer.Width;
        var h = layer.Height;
        var original = layer.CapturePixels();

        using var src = ToSKBitmap(original, w, h);
        using var dst = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.DrawBitmap(src, 0, 0, paint);

        var result = new byte[w * h * 4];
        Marshal.Copy(dst.GetPixels(), result, 0, result.Length);

        if (sel != null && sel.HasSelection)
        {
            // Restore pixels outside selection
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (!sel.IsSelected(x, y))
                    {
                        var i = (y * w + x) * 4;
                        result[i] = original[i];
                        result[i + 1] = original[i + 1];
                        result[i + 2] = original[i + 2];
                        result[i + 3] = original[i + 3];
                    }
        }

        WriteBytes(layer, result, w, h);
    }

    private static SKBitmap ToSKBitmap(byte[] pixels, int w, int h)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        Marshal.Copy(pixels, 0, bmp.GetPixels(), pixels.Length);
        return bmp;
    }

    private static byte ClampByte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);

    private static byte LerpByte(byte from, byte to, float amount)
        => ClampByte(from + (to - from) * Math.Clamp(amount, 0f, 1f));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static float FastHashNoise01(int x, int y, int seed)
    {
        unchecked
        {
            uint h = (uint)(x * 1619 + y * 31337 + seed * 8191);
            h ^= h >> 17; h *= 0xbf324c81u;
            h ^= h >> 13; h *= 0x9b2e1515u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0f;
        }
    }

    private static float SmoothStep(float edge0, float edge1, float x)
    {
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static float Wrap01(float value)
    {
        value %= 1f;
        return value < 0 ? value + 1f : value;
    }

    private static (float H, float S, float L) RgbToHsl(float r, float g, float b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) * 0.5f;

        if (Math.Abs(max - min) < 0.0001f)
            return (0f, 0f, l);

        var d = max - min;
        var s = l > 0.5f ? d / (2f - max - min) : d / (max + min);
        float h;
        if (Math.Abs(max - r) < 0.0001f)
            h = (g - b) / d + (g < b ? 6f : 0f);
        else if (Math.Abs(max - g) < 0.0001f)
            h = (b - r) / d + 2f;
        else
            h = (r - g) / d + 4f;
        h /= 6f;
        return (h, s, l);
    }

    private static (float R, float G, float B) HslToRgb(float h, float s, float l)
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
        return (
            HueToRgb(p, q, h + 1f / 3f),
            HueToRgb(p, q, h),
            HueToRgb(p, q, h - 1f / 3f));
    }

    // Erases connected ink regions whose pixel count is <= maxSpeckSize.
    // "Ink" = any pixel whose alpha channel >= alphaThreshold.
    // Uses 8-connectivity BFS so diagonal neighbours count as connected.
    public static void RemoveDust(DrawingLayer layer, int maxSpeckSize, byte alphaThreshold = 1, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var n = w * h;
        var pixels = layer.CapturePixels();

        // Build ink mask
        var isInk = new bool[n];
        for (var i = 0; i < n; i++)
            isInk[i] = pixels[i * 4 + 3] >= alphaThreshold;

        // BFS label pass — 0 means unlabeled/transparent
        var labels = new int[n];
        var sizes = new System.Collections.Generic.List<int> { 0 }; // 1-indexed
        var queue = new System.Collections.Generic.Queue<int>(1024);
        var labelId = 0;

        for (var idx = 0; idx < n; idx++)
        {
            if (!isInk[idx] || labels[idx] != 0) continue;
            labelId++;
            sizes.Add(0);
            labels[idx] = labelId;
            queue.Enqueue(idx);

            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                sizes[labelId]++;
                var cx = cur % w;
                var cy = cur / w;
                for (var dy = -1; dy <= 1; dy++)
                {
                    for (var dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = cx + dx;
                        var ny = cy + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        var ni = ny * w + nx;
                        if (!isInk[ni] || labels[ni] != 0) continue;
                        labels[ni] = labelId;
                        queue.Enqueue(ni);
                    }
                }
            }
        }

        // Erase pixels whose component is too small
        var changed = false;
        for (var i = 0; i < n; i++)
        {
            var lbl = labels[i];
            if (lbl == 0 || sizes[lbl] > maxSpeckSize) continue;
            if (sel != null && sel.HasSelection && !sel.IsSelected(i % w, i / w)) continue;
            pixels[i * 4 + 3] = 0;
            changed = true;
        }

        if (changed)
            WriteBytes(layer, pixels, w, h);
    }

    private static void WriteBytes(DrawingLayer layer, byte[] pixels, int w, int h)
    {
        var bounds = layer.Pixels.Bounds;
        var region = new PixelRegion(bounds.X, bounds.Y, Math.Min(w, bounds.Width), Math.Min(h, bounds.Height));
        if (region.IsEmpty) return;
        layer.Pixels.Restore(region, pixels);
        layer.MarkThumbnailDirty();
    }
}
