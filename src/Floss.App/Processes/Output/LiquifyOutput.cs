using System;
using System.Collections.Generic;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Warps pixels on the active layer using a displacement kernel applied along the stroke path.
// Reads ctx.ActivePreset for live Size/Strength/Mode so changes in the properties window
// take effect on the next stroke without recreating the tool.
public sealed class LiquifyOutput : IOutputProcess
{
    public bool IsPaintOutput => true;
    public bool Antialiasing { get; set; } = true;

    private Dictionary<(int, int), byte[]?>? _beforeTiles;
    private PixelRegion _dirtyRegion;
    private bool _strokeActive;
    private int _lastProcessedIndex;
    private DrawingLayer? _currentLayer;
    private float _lastKernelX, _lastKernelY;   // position of last applied kernel
    private float _lastSdx, _lastSdy;            // stroke direction carried across samples
    private float _distAccum;                    // accumulated distance since last kernel

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not StrokeInput stroke || stroke.SmoothedSamples.Count == 0) return;
        var layer = ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked) return;

        var samples = stroke.SmoothedSamples;
        var preset = ctx.ActivePreset;
        float radius = (float)(preset?.LiquifySize ?? 80) * 0.5f;
        float strength = (float)(preset?.LiquifyStrength ?? 0.3);
        var mode = preset?.LiquifyMode ?? LiquifyMode.Push;
        // Spacing: apply one kernel per (radius * 0.25) of stroke travel, min 2px.
        // This prevents hundreds of overlapping kernels from compounding the distortion.
        float spacing = Math.Max(2f, radius * 0.25f);

        var startIdx = Math.Max(0, _lastProcessedIndex + 1);

        // If startIdx has overshot the sample list, the input was cancelled and restarted.
        // Discard stale state so the new stroke initializes cleanly.
        if (_strokeActive && startIdx >= samples.Count)
            Cleanup();

        if (!_strokeActive)
        {
            _strokeActive = true;
            _lastProcessedIndex = -1;
            startIdx = 0;
            _beforeTiles = new Dictionary<(int, int), byte[]?>();
            _dirtyRegion = PixelRegion.Empty;
            _currentLayer = layer;
            _distAccum = spacing; // fire immediately on first sample
            if (samples.Count > 0)
            {
                _lastKernelX = (float)(samples[0].X - layer.OffsetX);
                _lastKernelY = (float)(samples[0].Y - layer.OffsetY);
            }
        }

        float prevX = _lastKernelX, prevY = _lastKernelY;
        for (int i = startIdx; i < samples.Count; i++)
        {
            float cx = (float)(samples[i].X - layer.OffsetX);
            float cy = (float)(samples[i].Y - layer.OffsetY);

            float ddx = cx - prevX, ddy = cy - prevY;
            float dist = MathF.Sqrt(ddx * ddx + ddy * ddy);
            if (dist > 0.001f)
            {
                _lastSdx = ddx / dist;
                _lastSdy = ddy / dist;
            }
            _distAccum += dist;
            prevX = cx;
            prevY = cy;
            _lastProcessedIndex = i;

            while (_distAccum >= spacing)
            {
                _distAccum -= spacing;
                var dirty = ApplyWarpKernel(layer, cx, cy, radius, strength, mode, _lastSdx, _lastSdy);
                if (!dirty.IsEmpty)
                    _dirtyRegion = _dirtyRegion.Union(dirty.Translate(layer.OffsetX, layer.OffsetY));
                _lastKernelX = cx;
                _lastKernelY = cy;
            }
        }

        if (!_dirtyRegion.IsEmpty)
        {
            layer.MarkThumbnailDirty();
            ctx.Document.NotifyChanged(_dirtyRegion, ctx.ActiveLayerIndex);
            _dirtyRegion = PixelRegion.Empty;
        }
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        Preview(ctx, input);

        if (_beforeTiles != null && _beforeTiles.Count > 0 && _currentLayer != null)
        {
            var tileDirty = ComputeTileDirtyRegion(_beforeTiles)
                .Translate(_currentLayer.OffsetX, _currentLayer.OffsetY);
            if (!tileDirty.IsEmpty)
            {
                _currentLayer.MarkThumbnailDirty();
                ctx.Document.CommitLayerTileMutation(ctx.ActiveLayerIndex, _beforeTiles, tileDirty);
                ctx.Document.NotifyChanged(tileDirty, ctx.ActiveLayerIndex);
            }
        }

        ctx.Document.CommitStroke();
        Cleanup();
    }

    private PixelRegion ApplyWarpKernel(
        DrawingLayer layer, float cx, float cy, float radius,
        float strength, LiquifyMode mode, float sdx, float sdy)
    {
        int bufW = layer.Pixels.Width;
        int bufH = layer.Pixels.Height;

        int x0 = Math.Max(0, (int)(cx - radius) - 1);
        int y0 = Math.Max(0, (int)(cy - radius) - 1);
        int x1 = Math.Min(bufW - 1, (int)(cx + radius) + 1);
        int y1 = Math.Min(bufH - 1, (int)(cy + radius) + 1);

        if (x1 < x0 || y1 < y0) return PixelRegion.Empty;

        var region = new PixelRegion(x0, y0, x1 - x0 + 1, y1 - y0 + 1);

        if (_beforeTiles != null)
            layer.CaptureTiles(region, _beforeTiles);

        // Snapshot this region so all reads come from pre-warp state within one kernel
        var src = layer.Pixels.Capture(region);
        int srcW = region.Width;
        int srcH = region.Height;

        float rSq = radius * radius;
        float maxDisplace = strength * radius * 0.3f;

        for (int py = y0; py <= y1; py++)
        {
            for (int px = x0; px <= x1; px++)
            {
                float dx = px - cx;
                float dy = py - cy;
                float distSq = dx * dx + dy * dy;
                if (distSq >= rSq) continue;

                float r = MathF.Sqrt(distSq);
                float t = 1f - r / radius;
                float weight = t * t * (3f - 2f * t);  // smoothstep falloff
                float w = weight * maxDisplace;

                float srcX = px, srcY = py;
                switch (mode)
                {
                    case LiquifyMode.Push:
                        srcX -= sdx * w;
                        srcY -= sdy * w;
                        break;

                    case LiquifyMode.Expand:
                        {
                            float rInv = r < 0.001f ? 0 : 1f / r;
                            srcX -= dx * rInv * w;
                            srcY -= dy * rInv * w;
                            break;
                        }

                    case LiquifyMode.Pinch:
                        {
                            float rInv = r < 0.001f ? 0 : 1f / r;
                            srcX += dx * rInv * w;
                            srcY += dy * rInv * w;
                            break;
                        }

                    case LiquifyMode.PushLeft:
                        // Perp-left of stroke direction is (-sdy, sdx).
                        // Pixel slides left, so source is to the right: (+sdy, -sdx) offset.
                        srcX -= (-sdy) * w;
                        srcY -= sdx * w;
                        break;

                    case LiquifyMode.PushRight:
                        srcX -= sdy * w;
                        srcY -= (-sdx) * w;
                        break;

                    case LiquifyMode.TwirlCW:
                        {
                            // Sample from a position rotated CCW by θ so rendered result appears CW.
                            float angle = weight * strength * MathF.PI * 0.2f;
                            float cos = MathF.Cos(angle);
                            float sin = MathF.Sin(angle);
                            srcX = cx + dx * cos - dy * sin;
                            srcY = cy + dx * sin + dy * cos;
                            break;
                        }

                    case LiquifyMode.TwirlCCW:
                        {
                            float angle = weight * strength * MathF.PI * 0.2f;
                            float cos = MathF.Cos(angle);
                            float sin = MathF.Sin(angle);
                            srcX = cx + dx * cos + dy * sin;
                            srcY = cy - dx * sin + dy * cos;
                            break;
                        }
                }

                var (b, g, r2, a) = BilinearSample(src, x0, y0, srcW, srcH, srcX, srcY, bufW, bufH);
                layer.Pixels.SetPixel(px, py, b, g, r2, a);
            }
        }

        return region;
    }

    private static (byte b, byte g, byte r, byte a) BilinearSample(
        byte[] src, int srcX0, int srcY0, int srcW, int srcH,
        float sx, float sy, int bufW, int bufH)
    {
        sx = Math.Clamp(sx, 0, bufW - 1.001f);
        sy = Math.Clamp(sy, 0, bufH - 1.001f);

        float lx = sx - srcX0;
        float ly = sy - srcY0;
        lx = Math.Clamp(lx, 0, srcW - 1.001f);
        ly = Math.Clamp(ly, 0, srcH - 1.001f);

        int ix = (int)lx;
        int iy = (int)ly;
        float fx = lx - ix;
        float fy = ly - iy;

        var c00 = GetPixel(src, srcW, srcH, ix, iy);
        var c10 = GetPixel(src, srcW, srcH, ix + 1, iy);
        var c01 = GetPixel(src, srcW, srcH, ix, iy + 1);
        var c11 = GetPixel(src, srcW, srcH, ix + 1, iy + 1);

        return (
            Lerp(Lerp(c00.b, c10.b, fx), Lerp(c01.b, c11.b, fx), fy),
            Lerp(Lerp(c00.g, c10.g, fx), Lerp(c01.g, c11.g, fx), fy),
            Lerp(Lerp(c00.r, c10.r, fx), Lerp(c01.r, c11.r, fx), fy),
            Lerp(Lerp(c00.a, c10.a, fx), Lerp(c01.a, c11.a, fx), fy)
        );
    }

    private static (byte b, byte g, byte r, byte a) GetPixel(byte[] src, int w, int h, int x, int y)
    {
        x = Math.Clamp(x, 0, w - 1);
        y = Math.Clamp(y, 0, h - 1);
        int off = (y * w + x) * 4;
        return (src[off], src[off + 1], src[off + 2], src[off + 3]);
    }

    private static byte Lerp(byte a, byte b, float t) => (byte)(a + (b - a) * t);

    private static PixelRegion ComputeTileDirtyRegion(Dictionary<(int, int), byte[]?> tiles)
    {
        if (tiles.Count == 0) return PixelRegion.Empty;
        const int ts = TiledPixelBuffer.TileSize;
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        foreach (var ((tx, ty), _) in tiles)
        {
            minX = Math.Min(minX, tx * ts);
            minY = Math.Min(minY, ty * ts);
            maxX = Math.Max(maxX, tx * ts + ts);
            maxY = Math.Max(maxY, ty * ts + ts);
        }
        return new PixelRegion(minX, minY, maxX - minX, maxY - minY);
    }

    private void Cleanup()
    {
        _strokeActive = false;
        _lastProcessedIndex = -1;
        _beforeTiles = null;
        _dirtyRegion = PixelRegion.Empty;
        _currentLayer = null;
        _distAccum = 0;
        _lastSdx = _lastSdy = 0;
    }
}
