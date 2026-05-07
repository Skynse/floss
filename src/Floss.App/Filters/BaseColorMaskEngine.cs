using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Floss.App.Filters;

public static class BaseColorMaskEngine
{
    public static List<byte[]> GenerateMasks(byte[] bgra, int w, int h, int minDistance)
    {
        var scale = Math.Max(1, Math.Max(w, h) / 1024);

        byte[] smallGray;
        int sw, sh;

        if (scale > 1)
        {
            sw = w / scale;
            sh = h / scale;
            smallGray = DownscaleBgraToGray(bgra, w, h, sw, sh);
        }
        else
        {
            sw = w;
            sh = h;
            smallGray = BgrToGray(bgra, w, h);
        }

        var lines = ExtractSketchLines(smallGray, sw, sh);

        var drawingMask = ComputeDrawingMask(lines, sw, sh);

        var scaledMinDist = Math.Max(2, minDistance / scale);
        var (regions, numRegions) = FindFillRegions(lines, drawingMask, sw, sh, scaledMinDist);

        var rawMask = ExtractLargestMask(regions, numRegions, drawingMask, sw, sh);
        if (rawMask == null) return [];

        var upscaled = UpscaleMask(rawMask, sw, sh, w, h);
        var bgraMask = new byte[w * h * 4];
        for (var i = 0; i < w * h; i++)
        {
            var p = i * 4;
            if (upscaled[i] == 0)
            {
                bgraMask[p] = 51;
                bgraMask[p + 1] = 51;
                bgraMask[p + 2] = 51;
                bgraMask[p + 3] = 255;
            }
        }
        return [bgraMask];
    }

    private static byte[] DownscaleBgraToGray(byte[] bgra, int w, int h, int sw, int sh)
    {
        var result = new byte[sw * sh];
        var xStep = (double)w / sw;
        var yStep = (double)h / sh;
        for (var sy = 0; sy < sh; sy++)
        {
            var srcYStart = (int)(sy * yStep);
            var srcYEnd = (int)((sy + 1) * yStep);
            for (var sx = 0; sx < sw; sx++)
            {
                var srcXStart = (int)(sx * xStep);
                var srcXEnd = (int)((sx + 1) * xStep);
                var sumR = 0L;
                var sumG = 0L;
                var sumB = 0L;
                var count = 0;
                for (var syy = srcYStart; syy < srcYEnd && syy < h; syy++)
                {
                    for (var sxx = srcXStart; sxx < srcXEnd && sxx < w; sxx++)
                    {
                        var p = (syy * w + sxx) * 4;
                        sumB += bgra[p];
                        sumG += bgra[p + 1];
                        sumR += bgra[p + 2];
                        count++;
                    }
                }
                if (count > 0)
                {
                    var avgR = (byte)(sumR / count);
                    var avgG = (byte)(sumG / count);
                    var avgB = (byte)(sumB / count);
                    result[sy * sw + sx] = (byte)((avgR * 77 + avgG * 150 + avgB * 29 + 128) >> 8);
                }
            }
        }
        return result;
    }

    private static byte[] UpscaleMask(byte[] smallMask, int sw, int sh, int dw, int dh)
    {
        var result = new byte[dw * dh];
        var xStep = (double)sw / dw;
        var yStep = (double)sh / dh;
        for (var dy = 0; dy < dh; dy++)
        {
            var sy = (int)(dy * yStep);
            if (sy >= sh) sy = sh - 1;
            for (var dx = 0; dx < dw; dx++)
            {
                var sx = (int)(dx * xStep);
                if (sx >= sw) sx = sw - 1;
                result[dy * dw + dx] = smallMask[sy * sw + sx];
            }
        }
        return result;
    }

    private static byte[] BgrToGray(byte[] bgra, int w, int h)
    {
        var gray = new byte[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var p = i * 4;
            gray[i] = (byte)((bgra[p + 2] * 77 + bgra[p + 1] * 150 + bgra[p] * 29 + 128) >> 8);
        }
        return gray;
    }

    public static byte[] ExtractSketchLines(byte[] gray, int w, int h)
    {
        var blurred = GaussianBlur(gray, w, h, 3);

        var otsu = OtsuThreshold(blurred, w, h, invert: true);

        var adapt = AdaptiveThreshold(blurred, w, h, blockSize: 31, c: 3, invert: true);

        var lines = BitwiseOr(otsu, adapt, w, h);

        lines = MorphologicalOpen(lines, w, h, radius: 1);

        return lines;
    }

    public static byte[] ComputeDrawingMask(byte[] lines, int w, int h)
    {
        var dilated = Dilate(lines, w, h, 5, iterations: 15);

        var (labels, stats) = ConnectedComponentsWithStats(dilated, w, h);
        if (stats.Count < 2)
            return ConstantImage(w, h, byte.MaxValue);

        var largestIdx = 1;
        var largestArea = stats[1].Area;
        for (var i = 2; i < stats.Count; i++)
        {
            if (stats[i].Area > largestArea)
            {
                largestArea = stats[i].Area;
                largestIdx = i;
            }
        }

        var mask = new byte[w * h];
        for (var i = 0; i < w * h; i++)
            mask[i] = labels[i] == largestIdx ? byte.MaxValue : (byte)0;

        mask = MorphologicalClose(mask, w, h, radius: 7, iterations: 2);
        return mask;
    }

    public static (int[] Labels, int Count) FindFillRegions(byte[] lines, byte[] drawingMask, int w, int h, int minDistance)
    {
        var inv = Invert(lines, w, h);

        var dist = DistanceTransform(inv, w, h);

        var fillable = new byte[w * h];
        for (var i = 0; i < w * h; i++)
            fillable[i] = (dist[i] >= minDistance && drawingMask[i] != 0) ? byte.MaxValue : (byte)0;

        fillable = Erode(fillable, w, h, 1);

        var (labels, numLabels) = ConnectedComponents(fillable, w, h);
        return (labels, numLabels);
    }

    private static byte[]? ExtractLargestMask(int[] regions, int numRegions, byte[] drawingMask, int w, int h)
    {
        // Any region touching the image boundary is the canvas-edge artifact, not a character fill.
        var touchesBorder = new System.Collections.Generic.HashSet<int>();
        for (var x = 0; x < w; x++)
        {
            var t = regions[x]; if (t != 0) touchesBorder.Add(t);
            var b = regions[(h - 1) * w + x]; if (b != 0) touchesBorder.Add(b);
        }
        for (var y = 0; y < h; y++)
        {
            var l = regions[y * w]; if (l != 0) touchesBorder.Add(l);
            var r = regions[y * w + w - 1]; if (r != 0) touchesBorder.Add(r);
        }

        byte[]? largestMask = null;
        var largestArea = 0;

        for (var rid = 1; rid <= numRegions; rid++)
        {
            if (touchesBorder.Contains(rid)) continue;

            var area = 0;
            for (var i = 0; i < w * h; i++)
                if (regions[i] == rid) area++;

            if (area <= largestArea) continue;

            var mask = new byte[w * h];
            var overlap = 0;
            for (var i = 0; i < w * h; i++)
            {
                if (regions[i] == rid)
                {
                    mask[i] = byte.MaxValue;
                    if (drawingMask[i] != 0) overlap++;
                }
            }

            if (overlap < area * 0.5) continue;

            largestMask = mask;
            largestArea = area;
        }

        return largestMask;
    }

    private static byte[] GaussianBlur(byte[] src, int w, int h, float sigma)
    {
        using var bmp = ToSKBitmap(src, w, h);
        using var filter = SKImageFilter.CreateBlur(sigma, sigma);
        using var dst = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
        using var canvas = new SKCanvas(dst);
        using var paint = new SKPaint { ImageFilter = filter };
        canvas.DrawBitmap(bmp, 0, 0, paint);

        var result = new byte[w * h];
        var ptr = dst.GetPixels();
        Marshal.Copy(ptr, result, 0, result.Length);
        return result;
    }

    private static byte[] OtsuThreshold(byte[] gray, int w, int h, bool invert)
    {
        var hist = new int[256];
        for (var i = 0; i < gray.Length; i++)
            hist[gray[i]]++;

        var total = gray.Length;
        var sum = 0.0;
        for (var i = 0; i < 256; i++)
            sum += i * hist[i];

        var sumB = 0.0;
        var wB = 0;
        var wF = 0;
        var maxVariance = 0.0;
        var threshold = 0;

        for (var i = 0; i < 256; i++)
        {
            wB += hist[i];
            if (wB == 0) continue;
            wF = total - wB;
            if (wF == 0) break;

            sumB += i * hist[i];
            var mB = sumB / wB;
            var mF = (sum - sumB) / wF;
            var between = (double)wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVariance)
            {
                maxVariance = between;
                threshold = i;
            }
        }

        var result = new byte[gray.Length];
        if (invert)
        {
            for (var i = 0; i < gray.Length; i++)
                result[i] = gray[i] <= threshold ? byte.MaxValue : (byte)0;
        }
        else
        {
            for (var i = 0; i < gray.Length; i++)
                result[i] = gray[i] > threshold ? byte.MaxValue : (byte)0;
        }
        return result;
    }

    private static byte[] AdaptiveThreshold(byte[] gray, int w, int h, int blockSize, int c, bool invert)
    {
        var result = new byte[gray.Length];
        var half = blockSize / 2;

        var integral = new int[(w + 1) * (h + 1)];
        for (var y = 0; y < h; y++)
        {
            var rowSum = 0;
            for (var x = 0; x < w; x++)
            {
                rowSum += gray[y * w + x];
                integral[(y + 1) * (w + 1) + (x + 1)] = integral[y * (w + 1) + (x + 1)] + rowSum;
            }
        }

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var x1 = Math.Max(x - half, 0);
                var x2 = Math.Min(x + half, w - 1);
                var y1 = Math.Max(y - half, 0);
                var y2 = Math.Min(y + half, h - 1);

                var count = (x2 - x1 + 1) * (y2 - y1 + 1);
                var sum = integral[(y2 + 1) * (w + 1) + (x2 + 1)]
                        - integral[(y1) * (w + 1) + (x2 + 1)]
                        - integral[(y2 + 1) * (w + 1) + (x1)]
                        + integral[(y1) * (w + 1) + (x1)];

                var mean = sum / count;

                if (invert)
                    result[y * w + x] = gray[y * w + x] <= (mean - c) ? byte.MaxValue : (byte)0;
                else
                    result[y * w + x] = gray[y * w + x] > (mean - c) ? byte.MaxValue : (byte)0;
            }
        }
        return result;
    }

    private static byte[] BitwiseOr(byte[] a, byte[] b, int w, int h)
    {
        var result = new byte[a.Length];
        for (var i = 0; i < a.Length; i++)
            result[i] = (byte)(a[i] | b[i]);
        return result;
    }

    private static byte[] BitwiseAnd(byte[] a, byte[] b, int w, int h)
    {
        var result = new byte[a.Length];
        for (var i = 0; i < a.Length; i++)
            result[i] = (byte)((a[i] & b[i]));
        return result;
    }

    private static byte[] Invert(byte[] src, int w, int h)
    {
        var result = new byte[src.Length];
        for (var i = 0; i < src.Length; i++)
            result[i] = (byte)(255 - src[i]);
        return result;
    }

    private static byte[] ConstantImage(int w, int h, byte value)
    {
        var img = new byte[w * h];
        Array.Fill(img, value);
        return img;
    }

    private static byte[] Dilate(byte[] src, int w, int h, int radius, int iterations = 1)
    {
        var result = (byte[])src.Clone();
        for (var iter = 0; iter < iterations; iter++)
            result = MorphologyDilate(result, w, h, radius);
        return result;
    }

    private static byte[] Erode(byte[] src, int w, int h, int radius)
    {
        var result = new byte[src.Length];
        var r = radius;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var all = true;
                for (var ky = -r; ky <= r && all; ky++)
                {
                    var py = y + ky;
                    if (py < 0 || py >= h) { all = false; break; }
                    for (var kx = -r; kx <= r && all; kx++)
                    {
                        var px = x + kx;
                        if (px < 0 || px >= w) { all = false; break; }
                        if (src[py * w + px] == 0)
                            all = false;
                    }
                }
                if (all)
                    result[y * w + x] = byte.MaxValue;
            }
        }
        return result;
    }

    private static byte[] MorphologyDilate(byte[] src, int w, int h, int radius)
    {
        var result = new byte[src.Length];
        var r = radius;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                if (src[y * w + x] == 0) continue;
                for (var ky = -r; ky <= r; ky++)
                {
                    var py = y + ky;
                    if (py < 0 || py >= h) continue;
                    for (var kx = -r; kx <= r; kx++)
                    {
                        var px = x + kx;
                        if (px >= 0 && px < w)
                            result[py * w + px] = byte.MaxValue;
                    }
                }
            }
        }
        return result;
    }

    private static byte[] MorphologicalOpen(byte[] src, int w, int h, int radius)
    {
        var eroded = Erode(src, w, h, radius);
        return MorphologyDilate(eroded, w, h, radius);
    }

    private static byte[] MorphologicalClose(byte[] src, int w, int h, int radius, int iterations = 1)
    {
        var result = (byte[])src.Clone();
        for (var i = 0; i < iterations; i++)
        {
            var dilated = MorphologyDilate(result, w, h, radius);
            result = Erode(dilated, w, h, radius);
        }
        return result;
    }

    private static (int[] Labels, List<ComponentStats> Stats) ConnectedComponentsWithStats(byte[] binary, int w, int h)
    {
        var labels = new int[w * h];
        var parent = new List<int> { 0 };
        var areas = new List<int> { 0 };
        var nextLabel = 1;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var idx = y * w + x;
                if (binary[idx] == 0) continue;

                var left = x > 0 ? labels[idx - 1] : 0;
                var top = y > 0 ? labels[idx - w] : 0;

                if (left == 0 && top == 0)
                {
                    labels[idx] = nextLabel;
                    parent.Add(nextLabel);
                    areas.Add(1);
                    nextLabel++;
                }
                else if (left != 0 && top == 0)
                {
                    labels[idx] = Find(parent, left);
                    areas[labels[idx]]++;
                }
                else if (left == 0 && top != 0)
                {
                    labels[idx] = Find(parent, top);
                    areas[labels[idx]]++;
                }
                else
                {
                    var pl = Find(parent, left);
                    var pt = Find(parent, top);
                    if (pl == pt)
                    {
                        labels[idx] = pl;
                        areas[pl]++;
                    }
                    else
                    {
                        var min = Math.Min(pl, pt);
                        var max = Math.Max(pl, pt);
                        parent[max] = min;
                        labels[idx] = min;
                        areas[min] += areas[max] + 1;
                    }
                }
            }
        }

        for (var i = 0; i < w * h; i++)
        {
            if (labels[i] != 0)
                labels[i] = Find(parent, labels[i]);
        }

        var finalAreas = new int[nextLabel];
        for (var i = 0; i < w * h; i++)
        {
            if (labels[i] != 0)
                finalAreas[labels[i]]++;
        }

        var stats = new List<ComponentStats>();
        for (var i = 1; i < nextLabel; i++)
        {
            if (finalAreas[i] > 0)
                stats.Add(new ComponentStats { Area = finalAreas[i] });
        }

        return (labels, stats);
    }

    private static int Find(List<int> parent, int x)
    {
        while (parent[x] != x)
        {
            parent[x] = parent[parent[x]];
            x = parent[x];
        }
        return x;
    }

    private static (int[] Labels, int Count) ConnectedComponents(byte[] binary, int w, int h)
    {
        var (labels, _) = ConnectedComponentsWithStats(binary, w, h);

        var maxLabel = 0;
        for (var i = 0; i < labels.Length; i++)
        {
            if (labels[i] > maxLabel)
                maxLabel = labels[i];
        }

        return (labels, maxLabel);
    }

    private static int[] DistanceTransform(byte[] binary, int w, int h)
    {
        var dist = new int[w * h];
        var large = w + h;

        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var idx = y * w + x;
                if (binary[idx] == 0)
                {
                    dist[idx] = 0;
                }
                else
                {
                    var top = y > 0 ? dist[idx - w] + 1 : large;
                    var left = x > 0 ? dist[idx - 1] + 1 : large;
                    var topleft = x > 0 && y > 0 ? dist[idx - w - 1] + 2 : large;
                    var topright = x < w - 1 && y > 0 ? dist[idx - w + 1] + 2 : large;
                    dist[idx] = Math.Min(Math.Min(top, left), Math.Min(topleft, topright));
                }
            }
        }

        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = w - 1; x >= 0; x--)
            {
                var idx = y * w + x;
                var bottom = y < h - 1 ? dist[idx + w] + 1 : large;
                var right = x < w - 1 ? dist[idx + 1] + 1 : large;
                var botleft = x > 0 && y < h - 1 ? dist[idx + w - 1] + 2 : large;
                var botright = x < w - 1 && y < h - 1 ? dist[idx + w + 1] + 2 : large;
                dist[idx] = Math.Min(dist[idx], Math.Min(Math.Min(bottom, right), Math.Min(botleft, botright)));
            }
        }

        return dist;
    }

    private static SKBitmap ToSKBitmap(byte[] gray, int w, int h)
    {
        var bmp = new SKBitmap(w, h, SKColorType.Gray8, SKAlphaType.Opaque);
        Marshal.Copy(gray, 0, bmp.GetPixels(), gray.Length);
        return bmp;
    }

    private struct ComponentStats
    {
        public int Area;
    }
}
