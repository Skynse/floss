using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;
using SkiaSharp;
using static Floss.App.Brushes.BrushDynamics;

namespace Floss.App.Brushes.Engine;

public sealed partial class BrushEngine
{
    private SKBitmap? GetOrLoadTexture(string path)
    {
        if (_textureCache.TryGetValue(path, out var cached)) return cached;
        SKBitmap? bmp = null;
        try
        {
            using var original = SKBitmap.Decode(path);
            if (original != null)
                bmp = original.Copy(SKColorType.Gray8);
        }
        catch { }
        _textureCache[path] = bmp;
        return bmp;
    }
    private static unsafe float SampleBrushGrain(
        int px, int py, int gx, int gy, PixelRegion dirty, float[]? grainTable, int tableW, int tableH,
        float brushGrain, byte* texPx, int texW, int texH, int texStride)
    {
        if (grainTable != null)
        {
            if ((uint)gx < (uint)tableW && (uint)gy < (uint)tableH)
                return grainTable[gy * tableW + gx];
            return 1f;
        }

        if (brushGrain <= 0f)
            return 1f;

        float noise;
        if (texPx != null && texW > 0 && texH > 0)
        {
            var tx = px % texW;
            if (tx < 0) tx += texW;
            var ty = py % texH;
            if (ty < 0) ty += texH;
            noise = texPx[ty * texStride + tx] / 255.0f;
        }
        else
        {
            noise = GrainNoise(px, py);
        }

        return 1f - brushGrain + noise * brushGrain;
    }
    private static float Hash01(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 1619 + y * 31337);
            h ^= h >> 17; h *= 0xbf324c81u;
            h ^= h >> 13; h *= 0x9b2e1515u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0f;
        }
    }

    private unsafe float[]? PrecomputeGrain(PixelRegion region, byte* texPx, int texW, int texH, int texStride, float brushGrain)
    {
        if (brushGrain <= 0f) return null;
        int w = region.Width, h = region.Height;
        if ((long)w * h > MaxPrecomputedGrainPixels)
            return null;

        if (_grainTable == null || _grainTable.Length < w * h)
            _grainTable = new float[w * h];
        var table = _grainTable;
        if (texPx != null)
        {
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    int px = region.X + x, py = region.Y + y;
                    int gtx = px % texW; if (gtx < 0) gtx += texW;
                    int gty = py % texH; if (gty < 0) gty += texH;
                    float noise = texPx[gty * texStride + gtx] / 255.0f;
                    table[y * w + x] = 1f - brushGrain + noise * brushGrain;
                }
            });
        }
        else
        {
            Parallel.For(0, h, y =>
            {
                for (int x = 0; x < w; x++)
                {
                    float noise = GrainNoise(region.X + x, region.Y + y);
                    table[y * w + x] = 1f - brushGrain + noise * brushGrain;
                }
            });
        }
        return table;
    }
    private static float GrainNoise(int cx, int cy)
    {
        int gx = cx >> 2, gy = cy >> 2;
        float fx = (cx & 3) * 0.25f, fy = (cy & 3) * 0.25f;
        float h00 = HashF(gx, gy), h10 = HashF(gx + 1, gy);
        float h01 = HashF(gx, gy + 1), h11 = HashF(gx + 1, gy + 1);
        return h00 + (h10 - h00) * fx + (h01 - h00) * fy + (h00 - h10 - h01 + h11) * fx * fy;
    }

    private static float HashF(int x, int y)
    {
        unchecked
        {
            uint h = (uint)(x * 1619 + y * 31337);
            h ^= h >> 17; h *= 0xbf324c81u;
            h ^= h >> 13; h *= 0x9b2e1515u;
            h ^= h >> 16;
            return (h & 0xFFFF) / 65535.0f;
        }
    }
}
