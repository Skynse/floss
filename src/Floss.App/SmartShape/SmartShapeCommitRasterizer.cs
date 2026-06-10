using System;
using System.Collections.Generic;
using Floss.App.Brushes;
using Floss.App.Brushes.Engine;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.SmartShape;

/// <summary>
/// Rasterizes raw and fitted smart-shape strokes on a scratch layer so commit can
/// store both undo steps without painting the raw stroke onto the live layer.
/// </summary>
internal static class SmartShapeCommitRasterizer
{
    internal readonly record struct PlannedCommit(
        IReadOnlyList<LayerTileHistoryPatch> RawStepPatches,
        IReadOnlyList<LayerTileHistoryPatch> SmartStepPatches,
        PixelRegion DirtyRegion);

    public static PlannedCommit Plan(
        BrushEngine brushEngine,
        ToolContext ctx,
        DrawingLayer layer,
        BrushPreset brush,
        IReadOnlyDictionary<(int X, int Y), byte[]?> baselineTiles,
        PixelRegion dirtyRegion,
        IReadOnlyList<CanvasInputSample> rawSamples,
        IReadOnlyList<CanvasInputSample> shapeSamples)
    {
        using var scratch = CreateAlignedScratch(layer);
        RestoreBaseline(scratch, baselineTiles);

        Dictionary<(int X, int Y), byte[]?>? rawTiles = null;
        if (rawSamples.Count >= 2)
        {
            Rasterize(scratch, brushEngine, brush, rawSamples, ctx, baselineTiles);
            rawTiles = CaptureTiles(scratch, baselineTiles.Keys);
        }

        RestoreBaseline(scratch, baselineTiles);
        Rasterize(scratch, brushEngine, brush, shapeSamples, ctx, baselineTiles);
        var smartTiles = CaptureTiles(scratch, baselineTiles.Keys);

        var rawPatches = rawTiles == null
            ? []
            : BuildPatches(baselineTiles, rawTiles);
        var smartBefore = rawTiles ?? baselineTiles;
        var smartPatches = BuildPatches(smartBefore, smartTiles);

        return new PlannedCommit(rawPatches, smartPatches, dirtyRegion);
    }

    private static DrawingLayer CreateAlignedScratch(DrawingLayer source)
        => new("_smart_shape_commit", source.Width, source.Height)
        {
            OffsetX = source.OffsetX,
            OffsetY = source.OffsetY
        };

    private static void RestoreBaseline(DrawingLayer scratch, IReadOnlyDictionary<(int X, int Y), byte[]?> baselineTiles)
    {
        scratch.Pixels.Clear();
        foreach (var (key, bytes) in baselineTiles)
            scratch.RestorePaintTile(key.X, key.Y, bytes, maskBuffer: false);
    }

    private static void Rasterize(
        DrawingLayer layer,
        BrushEngine brushEngine,
        BrushPreset brush,
        IReadOnlyList<CanvasInputSample> samples,
        ToolContext ctx,
        IReadOnlyDictionary<(int X, int Y), byte[]?> baselineTiles)
    {
        if (samples.Count < 2)
            return;

        brushEngine.CanvasZoom = ctx.Viewport?.Zoom ?? 1.0;
        brushEngine.BeginStroke(brush, samples[0]);
        var dirty = brushEngine.RasterizeSegments(layer, brush, samples, 1, samples.Count - 1);
        brushEngine.EndStroke();

        RestoreUnselectedPixels(layer, dirty, ctx.Selection, baselineTiles);
    }

    private static Dictionary<(int X, int Y), byte[]?> CaptureTiles(
        DrawingLayer layer,
        IEnumerable<(int X, int Y)> keys)
    {
        var tiles = new Dictionary<(int X, int Y), byte[]?>();
        foreach (var key in keys)
            tiles[key] = layer.ActivePixels.CaptureTile(key.X, key.Y);
        return tiles;
    }

    private static List<LayerTileHistoryPatch> BuildPatches(
        IReadOnlyDictionary<(int X, int Y), byte[]?> beforeTiles,
        IReadOnlyDictionary<(int X, int Y), byte[]?> afterTiles)
    {
        var patches = new List<LayerTileHistoryPatch>();
        foreach (var (key, after) in afterTiles)
        {
            beforeTiles.TryGetValue(key, out var before);
            if (TileBytesEqual(before, after))
                continue;
            patches.Add(new LayerTileHistoryPatch(key.X, key.Y, before, after));
        }
        return patches;
    }

    private static void RestoreUnselectedPixels(
        DrawingLayer layer,
        PixelRegion dirty,
        SelectionMask selection,
        IReadOnlyDictionary<(int X, int Y), byte[]?>? captureBaselineForClip)
    {
        if (dirty.IsEmpty)
            return;

        var hasSelection = selection.HasSelection;
        var alphaLocked = layer.IsAlphaLocked;
        if (!hasSelection && !alphaLocked)
            return;

        const int ts = TiledPixelBuffer.TileSize;
        var firstTileX = FloorDiv(dirty.X, ts);
        var firstTileY = FloorDiv(dirty.Y, ts);
        var lastTileX = FloorDiv(dirty.Right - 1, ts);
        var lastTileY = FloorDiv(dirty.Bottom - 1, ts);

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            var tilePixY = ty * ts;
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                byte[]? beforeTile = null;
                if (captureBaselineForClip != null)
                    captureBaselineForClip.TryGetValue((tx, ty), out beforeTile);
                else
                    beforeTile = layer.ActivePixels.CaptureTile(tx, ty);

                var pxMin = Math.Max(dirty.X, tx * ts);
                var pxMax = Math.Min(dirty.Right, tx * ts + ts);
                var pyMin = Math.Max(dirty.Y, ty * ts);
                var pyMax = Math.Min(dirty.Bottom, ty * ts + ts);
                if (pxMin >= pxMax || pyMin >= pyMax)
                    continue;

                byte[]? liveTile = null;
                for (var py = pyMin; py < pyMax; py++)
                {
                    var ly = py - tilePixY;
                    var rowBase = ly * ts * 4;
                    for (var px = pxMin; px < pxMax; px++)
                    {
                        var inSelection = !hasSelection || selection.IsSelected(px + layer.OffsetX, py + layer.OffsetY);
                        var lx = px - tx * ts;
                        var offset = rowBase + lx * 4;
                        var hadAlpha = !alphaLocked || beforeTile is { } bt && bt[offset + 3] > 0;
                        if (inSelection && hadAlpha)
                            continue;

                        if (beforeTile != null)
                        {
                            liveTile ??= layer.ActivePixels.GetOrCreateRawTile(tx, ty);
                            liveTile[offset] = beforeTile[offset];
                            liveTile[offset + 1] = beforeTile[offset + 1];
                            liveTile[offset + 2] = beforeTile[offset + 2];
                            liveTile[offset + 3] = beforeTile[offset + 3];
                        }
                        else
                        {
                            liveTile ??= layer.ActivePixels.GetOrCreateRawTile(tx, ty);
                            liveTile[offset] = 0;
                            liveTile[offset + 1] = 0;
                            liveTile[offset + 2] = 0;
                            liveTile[offset + 3] = 0;
                        }
                    }
                }
            }
        }
    }

    private static bool TileBytesEqual(byte[]? a, byte[]? b)
    {
        if (ReferenceEquals(a, b))
            return true;
        if (a == null || b == null)
            return a == null && b == null;
        if (a.Length != b.Length)
            return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (a[i] != b[i])
                return false;
        }
        return true;
    }

    private static int FloorDiv(int value, int divisor)
        => (int)Math.Floor(value / (double)divisor);
}
