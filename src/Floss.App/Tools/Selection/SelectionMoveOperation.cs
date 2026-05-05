using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

// Moves the selected pixels (floating selection) to a new position.
// Extracts selected pixels from the layer on creation, erases them, then stamps
// them at the drag-offset position on commit.
public sealed class SelectionMoveOperation : IToolOperation, IToolOperationOverlay
{
    private readonly ToolContext _context;
    private readonly int _layerIndex;
    private readonly double _startDocX, _startDocY;
    private readonly int _origOffsetX, _origOffsetY;

    private readonly int _floatOriginX, _floatOriginY; // doc coords of extracted bbox top-left
    private readonly int _floatW, _floatH;
    private readonly byte[]? _floatPixels;              // BGRA, floatW * floatH * 4
    private readonly Dictionary<(int, int), byte[]?> _beforeTiles;
    private WriteableBitmap? _overlayBitmap;
    private bool _overlayDirty = true;

    private int _dx, _dy;

    public SelectionMoveOperation(ToolContext ctx, CanvasInputSample start)
    {
        _context = ctx;
        _layerIndex = ctx.ActiveLayerIndex;
        _startDocX = start.X;
        _startDocY = start.Y;
        SampleCount = 1;

        var layer = ctx.ActiveLayer!;
        _origOffsetX = layer.OffsetX;
        _origOffsetY = layer.OffsetY;

        var bounds = ctx.Selection.GetMaskBounds();
        if (bounds == null) { _beforeTiles = []; return; }

        var b = bounds.Value;
        _floatOriginX = b.Left;
        _floatOriginY = b.Top;
        _floatW = b.Width;
        _floatH = b.Height;
        _floatPixels = new byte[_floatW * _floatH * 4];

        var layerBounds = new PixelRegion(
            b.Left - layer.OffsetX, b.Top - layer.OffsetY, _floatW, _floatH)
            ;

        _beforeTiles = layer.Pixels.CaptureTiles(layerBounds);

        // Extract selected pixels then erase them from the layer.
        for (int docY = b.Top; docY < b.Bottom; docY++)
        {
            int layY = docY - layer.OffsetY;
            for (int docX = b.Left; docX < b.Right; docX++)
            {
                if (!ctx.Selection.IsSelected(docX, docY)) continue;
                int layX = docX - layer.OffsetX;

                layer.Pixels.GetPixel(layX, layY, out byte px_b, out byte px_g, out byte px_r, out byte px_a);
                if (px_a == 0) continue;

                int fi = ((docY - b.Top) * _floatW + (docX - b.Left)) * 4;
                _floatPixels[fi]     = px_b;
                _floatPixels[fi + 1] = px_g;
                _floatPixels[fi + 2] = px_r;
                _floatPixels[fi + 3] = px_a;

                layer.Pixels.SetPixel(layX, layY, 0, 0, 0, 0);
            }
        }

        layer.MarkThumbnailDirty();
        ctx.Document.NotifyChanged(new PixelRegion(_floatOriginX, _floatOriginY, _floatW, _floatH), _layerIndex);
    }

    public int SampleCount { get; private set; }

    public void Update(CanvasInputSample sample)
    {
        _dx = (int)Math.Round(sample.X - _startDocX);
        _dy = (int)Math.Round(sample.Y - _startDocY);
        _overlayDirty = true;
        SampleCount++;
        _context.InvalidateRender();
    }

    public void Commit(CanvasInputSample sample)
    {
        Update(sample);
        StampPixels();
        SampleCount = 0;
    }

    public void Cancel()
    {
        // Restore original pixels (put them back where they came from).
        if (_floatPixels != null)
        {
            var layer = _context.ActiveLayer;
            if (layer != null && _layerIndex == _context.ActiveLayerIndex)
            {
                for (int relY = 0; relY < _floatH; relY++)
                {
                    int docY = _floatOriginY + relY;
                    int layY = docY - layer.OffsetY;
                    for (int relX = 0; relX < _floatW; relX++)
                    {
                        int docX = _floatOriginX + relX;
                        if (!_context.Selection.IsSelected(docX, docY)) continue;
                        int layX = docX - layer.OffsetX;
                        int fi = (relY * _floatW + relX) * 4;
                        layer.Pixels.SetPixel(layX, layY,
                            _floatPixels[fi], _floatPixels[fi + 1], _floatPixels[fi + 2], _floatPixels[fi + 3]);
                    }
                }
                layer.MarkThumbnailDirty();
                _context.Document.NotifyChanged(
                    new PixelRegion(_floatOriginX, _floatOriginY, _floatW, _floatH), _layerIndex);
            }
        }
        _overlayBitmap?.Dispose();
        _overlayBitmap = null;
        SampleCount = 0;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (_floatPixels == null || _floatW <= 0 || _floatH <= 0) return;

        if (_overlayBitmap == null || _overlayDirty)
        {
            _overlayBitmap?.Dispose();
            _overlayBitmap = new WriteableBitmap(
                new PixelSize(_floatW, _floatH),
                new Vector(96, 96),
                PixelFormat.Bgra8888,
                AlphaFormat.Unpremul);
            using var fb = _overlayBitmap.Lock();
            Marshal.Copy(_floatPixels, 0, fb.Address, _floatPixels.Length);
            _overlayDirty = false;
        }

        var destRect = new Rect(_floatOriginX + _dx, _floatOriginY + _dy, _floatW, _floatH);
        dc.DrawImage(_overlayBitmap, destRect);
    }

    private void StampPixels()
    {
        if (_floatPixels == null) return;
        var layer = _context.ActiveLayer;
        if (layer == null || _layerIndex != _context.ActiveLayerIndex) return;

        var minX = int.MaxValue; var minY = int.MaxValue;
        var maxX = int.MinValue; var maxY = int.MinValue;

        for (int relY = 0; relY < _floatH; relY++)
        {
            int docY = _floatOriginY + _dy + relY;
            int layY = docY - layer.OffsetY;
            for (int relX = 0; relX < _floatW; relX++)
            {
                int fi = (relY * _floatW + relX) * 4;
                if (_floatPixels[fi + 3] == 0) continue;
                int docX = _floatOriginX + _dx + relX;
                int layX = docX - layer.OffsetX;

                if (layer.IsAlphaLocked)
                {
                    layer.Pixels.GetPixel(layX, layY, out _, out _, out _, out byte existingA);
                    if (existingA == 0) continue;
                }

                layer.Pixels.SetPixel(layX, layY,
                    _floatPixels[fi], _floatPixels[fi + 1], _floatPixels[fi + 2], _floatPixels[fi + 3]);

                if (docX < minX) minX = docX;
                if (docY < minY) minY = docY;
                if (docX > maxX) maxX = docX;
                if (docY > maxY) maxY = docY;
            }
        }

        if (maxX == int.MinValue)
        {
            _overlayBitmap?.Dispose();
            _overlayBitmap = null;
            return;
        }

        layer.MarkThumbnailDirty();

        // Also mark the source region dirty (where pixels were erased).
        var sourceRegion = new PixelRegion(_floatOriginX, _floatOriginY, _floatW, _floatH);
        var destRegion   = new PixelRegion(minX, minY, maxX - minX + 1, maxY - minY + 1);
        var fullDirty    = sourceRegion.Union(destRegion);

        // Capture tiles in the destination region for history (before tiles were captured from source on creation).
        var destLayerRegion = new PixelRegion(minX - layer.OffsetX, minY - layer.OffsetY,
            maxX - minX + 1, maxY - minY + 1);
        var destTiles = layer.Pixels.CaptureTiles(destLayerRegion);

        // Merge before+dest tiles and commit. The before tiles already have the original state
        // (before extraction), so undo will restore both the erased source and the written dest.
        foreach (var (k, v) in destTiles)
            _beforeTiles.TryAdd(k, v);

        _context.CommitMutation(_layerIndex, _beforeTiles, fullDirty);
        _overlayBitmap?.Dispose();
        _overlayBitmap = null;
    }
}
