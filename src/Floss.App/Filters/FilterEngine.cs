using System;
using System.Runtime.InteropServices;
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

        // 1. Pre-calculate values to avoid doing it inside the loop
        float cx = w / 2f;
        float cy = h / 2f;
        float maxDist = (float)Math.Sqrt(cx * cx + cy * cy);

        float latShiftX = 0f;
        float latShiftY = 0f;

        if (!is_radial)
        {
            // Convert angle from degrees to radians for Math.Sin/Cos
            float rad = angle * (float)(Math.PI / 180.0);
            latShiftX = (float)Math.Cos(rad) * intensity;
            latShiftY = (float)Math.Sin(rad) * intensity;
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;

                if (sel != null && sel.HasSelection && !sel.IsSelected(x, y))
                {
                    result[i] = orig[i];         // B
                    result[i + 1] = orig[i + 1]; // G
                    result[i + 2] = orig[i + 2]; // R
                    result[i + 3] = orig[i + 3]; // A
                    continue;
                }

                float shiftXRed, shiftYRed, shiftXBlue, shiftYBlue;

                if (is_radial)
                {
                    // Calculate distance from center
                    float dx = x - cx;
                    float dy = y - cy;
                    float dist = (float)Math.Sqrt(dx * dx + dy * dy);

                    if (dist == 0)
                    {
                        shiftXRed = shiftYRed = shiftXBlue = shiftYBlue = 0;
                    }
                    else
                    {
                        // Scale intensity based on distance from center
                        float currentIntensity = intensity * (dist / maxDist);

                        // Normalize the direction vector
                        float ux = dx / dist;
                        float uy = dy / dist;

                        shiftXRed = ux * currentIntensity;
                        shiftYRed = uy * currentIntensity;

                        // Blue shifts in the exact opposite direction
                        shiftXBlue = -shiftXRed;
                        shiftYBlue = -shiftYRed;
                    }
                }
                else
                {
                    // Lateral shift uses the pre-calculated angle components
                    shiftXRed = latShiftX;
                    shiftYRed = latShiftY;
                    shiftXBlue = -latShiftX;
                    shiftYBlue = -latShiftY;
                }

                // 2. Apply offsets and clamp to image boundaries
                int rx = Math.Clamp((int)(x + shiftXRed), 0, w - 1);
                int ry = Math.Clamp((int)(y + shiftYRed), 0, h - 1);

                int bx = Math.Clamp((int)(x + shiftXBlue), 0, w - 1);
                int by = Math.Clamp((int)(y + shiftYBlue), 0, h - 1);

                int iRed = (ry * w + rx) * 4;
                int iBlue = (by * w + bx) * 4;

                // 3. Construct the new pixel (BGRA format)
                result[i] = orig[iBlue];           // B from shifted coordinate
                result[i + 1] = orig[i + 1];       // G from current coordinate (anchored)
                result[i + 2] = orig[iRed + 2];    // R from shifted coordinate
                result[i + 3] = orig[i + 3];       // A from current coordinate
            }
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

        for (var y = 0; y < h; y++)
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
        }

        WriteBytes(layer, orig, w, h);
    }

    public static void ApplyNoise(DrawingLayer layer, float amount, bool monochromatic, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var pixels = layer.CapturePixels();
        var rng = new Random();
        var strength = (int)(amount * 255);

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var i = (y * w + x) * 4;
                if (pixels[i + 3] == 0) continue;
                if (sel != null && sel.HasSelection && !sel.IsSelected(x, y)) continue;
                if (monochromatic)
                {
                    var n = rng.Next(-strength, strength + 1);
                    pixels[i + 0] = (byte)Math.Clamp(pixels[i + 0] + n, 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] + n, 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] + n, 0, 255);
                }
                else
                {
                    pixels[i + 0] = (byte)Math.Clamp(pixels[i + 0] + rng.Next(-strength, strength + 1), 0, 255);
                    pixels[i + 1] = (byte)Math.Clamp(pixels[i + 1] + rng.Next(-strength, strength + 1), 0, 255);
                    pixels[i + 2] = (byte)Math.Clamp(pixels[i + 2] + rng.Next(-strength, strength + 1), 0, 255);
                }
            }
        }

        WriteBytes(layer, pixels, w, h);
    }

    // lutMaster applied first (combined RGB), then per-channel luts.
    public static void ApplyCurves(DrawingLayer layer, byte[] lutMaster, byte[] lutR, byte[] lutG, byte[] lutB, SelectionMask? sel = null)
    {
        var w = layer.Width;
        var h = layer.Height;
        var pixels = layer.CapturePixels();

        for (var y = 0; y < h; y++)
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
        }

        WriteBytes(layer, pixels, w, h);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

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

    private static void WriteBytes(DrawingLayer layer, byte[] pixels, int w, int h)
    {
        layer.Pixels.Clear();
        layer.Pixels.CopyFromBgra(pixels, w, h);
        layer.MarkThumbnailDirty();
    }
}
