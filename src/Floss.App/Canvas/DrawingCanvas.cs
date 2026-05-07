using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Canvas;

public sealed class DrawingCanvas : Control
{
    private const double PenPressureThreshold = 0.02;

    private readonly DrawingDocument _document = new();
    private readonly LayerCompositor _compositor;
    private readonly ToolContext _ctx;
    private readonly ToolController _toolController;

    private readonly CompositeTool _brushTool;
    private readonly CompositeTool _eraserTool;
    private readonly TransformTool _transformTool = new();

    private BrushPreset _brush = BrushPreset.Defaults[0];
    private Color _paintColor = Color.Parse("#111111");
    private long _activePointerId = -1;
    private Point _pointerPos;
    private Point _lockedPointerPos;
    private bool _isPointerOver;
    private bool _isCursorPreviewLocked;
    private bool _forceBrushOutlineCursor;

    // Ctrl+Shift layer-pick drag state
    private bool _isLayerPickDrag;
    private Point _layerPickAnchor;
    private Point _layerPickCurrent;

    private static readonly Cursor CursorNo = new(StandardCursorType.No);
    private static readonly Cursor CursorNone = new(StandardCursorType.None);
    private static readonly Cursor CursorDefault = new(StandardCursorType.Arrow);

    private static readonly IBrush CursorOuterBrush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
    private static readonly IBrush CursorInnerBrush = new SolidColorBrush(Colors.White);

    public BrushEngine BrushEngine { get; }

    public DrawingCanvas()
    {
        _document.DirtyStateChanged += (_, _) => DirtyStateChanged?.Invoke(this, EventArgs.Empty);
        Focusable = true;
        ClipToBounds = false;
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        BrushEngine = new BrushEngine();
        _compositor = new LayerCompositor();

        _ctx = new ToolContext(_document)
        {
            Brush = _brush,
            PaintColor = _paintColor,
            InvalidateRender = InvalidateVisual,
            OnColorSampled = c =>
            {
                _paintColor = c;
                _brush = _brush with { Color = c };
                _ctx!.PaintColor = c;
                _ctx.Brush = _brush;
                ColorSampled?.Invoke(this, c);
                InvalidateVisual();
            },
            SelectionChanged = NotifySelectionChanged,
            CommitSelectionMutation = _document.CommitSelectionMutation,
            SampleDocumentColor = SampleDocumentColor
        };

        _brushTool = new CompositeTool(new BrushStrokeInputProcess(), new DirectDrawOutput(BrushEngine, _document));
        _eraserTool = new CompositeTool(new BrushStrokeInputProcess(), new DirectDrawOutput(BrushEngine, _document));
        _toolController = new ToolController(_ctx, _brushTool);

        _document.Changed += (_, e) =>
        {
            _compositor.Invalidate(e.DirtyRegion, _document.Layers, e.LayerIndex);
            InvalidateVisual();
            StatsChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
        _document.SelectionChanged += (_, _) => NotifySelectionChanged();
        _document.LayersChanged += (_, _) =>
        {
            _compositor.Invalidate(null);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.LayerRemoved += (_, layer) => _compositor.RemoveGroupCache(layer);
        _document.LayerMetadataChanged += (_, e) => LayerMetadataChanged?.Invoke(this, e);
    }

    public event EventHandler? StatsChanged;
    public event EventHandler? HistoryChanged;
    public event EventHandler? LayersChanged;
    public event Action<IReadOnlyList<int>>? LayersFoundByRect;
    public event EventHandler<LayerMetadataChangedEventArgs>? LayerMetadataChanged;
    public event EventHandler<Color>? ColorSampled;
    public event EventHandler? DirtyStateChanged;
    public event EventHandler? SelectionChanged;

    public int ActiveSampleCount => _toolController.ActiveTool.HasPendingOperation ? 1 : 0;
    public int CommittedStrokeCount => _document.CommittedStrokeCount;
    public bool CanUndo => _document.CanUndo;
    public bool CanRedo => _document.CanRedo;
    public bool CanDeleteLayer => _document.CanDeleteLayer;
    public BrushPreset Brush => _brush;
    public Color PaintColor => _paintColor;
    public bool EraserEnabled => _brush.BlendMode == SkiaSharp.SKBlendMode.DstOut;
    public DrawingDocument Document => _document;
    public IReadOnlyList<DrawingLayer> Layers => _document.Layers;
    public int ActiveLayerIndex => _document.ActiveLayerIndex;
    public ITool ActiveTool => _toolController.ActiveTool;
    public ITool BrushTool => _brushTool;
    public ITool EraserTool => _eraserTool;
    public TransformTool TransformTool => _transformTool;
    public bool HasSelection => _ctx.Selection.HasSelection;
    public SelectionMask Selection => _ctx.Selection;
    public bool IsDirty => _document.IsDirty;
    public bool HasSavedBrushSettings(ToolPresetEngine engine) => _toolController.HasSavedSettings(engine);

    public bool PaintInputSuspended { get; set; }
    public double CanvasZoom { get; set; } = 1.0;
    public double PanOffsetX { get; set; }
    public double PanOffsetY { get; set; }
    public int FlipX { get; set; } = 1;
    public int FlipY { get; set; } = 1;
    public double CanvasRotation { get; set; }

    public void SetActiveTool(ITool tool, ToolPreset? preset = null)
    {
        _toolController.SetActiveTool(tool, preset);
        InvalidateVisual();
    }

    public ITool? AlternateTool => _toolController.AlternateTool;
    public void SetAlternateTool(ITool? tool) => _toolController.AlternateTool = tool;

    public void SetBrush(BrushPreset preset)
    {
        _brush = preset with { Color = _paintColor };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SaveBrushEnginePreset() => _toolController.SaveEnginePreset();

    internal void SyncBrushFromContext(BrushPreset brush)
    {
        _brush = brush with { Color = _paintColor };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetPaintColor(Color color)
    {
        _paintColor = color;
        _ctx.PaintColor = color;
        if (!EraserEnabled)
        {
            _brush = _brush with { Color = color };
            _ctx.Brush = _brush;
        }
        InvalidateVisual();
    }

    public void SetBrushSize(double size)
    {
        _brush = _brush with { Size = Math.Clamp(size, 1, 2000) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushOpacity(double opacity)
    {
        _brush = _brush with { Opacity = Math.Clamp(opacity, 0.01, 1) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushHardness(double hardness)
    {
        _brush = _brush with { Hardness = Math.Clamp(hardness, 0, 1) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushSpacing(double spacing)
    {
        _brush = _brush with { Spacing = Math.Clamp(spacing, 0.02, 1) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushSmoothing(double smoothing)
    {
        _brush = _brush with { Smoothing = Math.Clamp(smoothing, 0, 0.95) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void SetBrushGrain(double grain)
    {
        _brush = _brush with { Grain = Math.Clamp(grain, 0, 1) };
        _ctx.Brush = _brush;
        InvalidateVisual();
    }

    public void LockCursorPreview(Point position, bool forceBrushOutline)
    {
        _lockedPointerPos = position;
        _isCursorPreviewLocked = true;
        _forceBrushOutlineCursor = forceBrushOutline;
        InvalidateVisual();
    }

    public void UnlockCursorPreview()
    {
        if (!_isCursorPreviewLocked) return;
        _isCursorPreviewLocked = false;
        _forceBrushOutlineCursor = false;
        InvalidateVisual();
    }

    public void CancelActiveTool()
    {
        if (_toolController.ActiveTool is TransformTool)
        {
            _transformTool.Cancel(_ctx);
        }
        else if (!_toolController.Cancel() && _ctx.Selection.HasSelection)
        {
            var before = _ctx.Selection.CaptureSnapshot();
            _ctx.Selection.Clear();
            _document.CommitSelectionMutation(before);
        }
        InvalidateVisual();
    }

    public void CommitActiveTool()
    {
        _toolController.ActiveTool.Commit(_ctx);
        InvalidateVisual();
    }

    public bool BeginSelectionTransform(IReadOnlyList<int>? layerIndices = null)
    {
        var previousTool = _toolController.ActiveTool;
        SetActiveTool(_transformTool);
        _transformTool.SetPreviousTool(previousTool);
        _transformTool.OnCompleted = EndSelectionTransform;
        if (_transformTool.BeginTransform(_ctx, layerIndices))
        {
            NotifySelectionChanged();
            return true;
        }

        _transformTool.OnCompleted = null;
        _transformTool.SetPreviousTool(null);
        SetActiveTool(previousTool);
        return false;
    }

    public void EndSelectionTransform(TransformCompletionKind completion)
    {
        var previous = _transformTool.GetPreviousTool();
        _transformTool.SetPreviousTool(null);
        _transformTool.OnCompleted = null;
        if (completion != TransformCompletionKind.Cancel)
            _ctx.Selection.Clear();
        if (previous != null)
            SetActiveTool(previous);
        _document.NotifySelectionChanged();
    }

    public void DeleteSelectionTransform()
    {
        _transformTool.Delete(_ctx);
        InvalidateVisual();
    }

    public void MergeSelectedLayers(IReadOnlyList<int> indices) => _document.MergeSelectedLayers(indices, _compositor);
    public void FlattenGroup(int groupIndex) => _document.FlattenGroup(groupIndex, _compositor);
    public void ApplyFilter(IReadOnlyList<int> layerIndices, Action<DrawingLayer> apply)
        => _document.ApplyFilterToLayers(layerIndices, apply);

    public void MergeDown(IReadOnlyList<int>? selectedIndices = null)
    {
        if (!_document.CanModifyActiveLayer) return;
        var active = _document.ActiveLayerIndex;
        if (selectedIndices is { Count: > 1 })
        {
            _document.MergeSelectedLayers(selectedIndices, _compositor);
            return;
        }
        var layer = _document.ActiveLayer;
        if (layer.IsGroup)
        {
            _document.FlattenGroup(active, _compositor);
            return;
        }
        // Merge with the next layer below in the flat list
        if (active + 1 < _document.Layers.Count)
            _document.MergeSelectedLayers([active, active + 1], _compositor);
    }

    public void ResizeCanvas(int newW, int newH, int offsetX, int offsetY)
    {
        _document.ResizeCanvas(newW, newH, offsetX, offsetY);
        InvalidateVisual();
    }

    public void SelectAll()
    {
        var before = _ctx.Selection.CaptureSnapshot();
        _ctx.Selection.SetFromRect(0, 0, _document.Width, _document.Height);
        _document.CommitSelectionMutation(before);
        InvalidateVisual();
    }

    public void Deselect()
    {
        var before = _ctx.Selection.CaptureSnapshot();
        _ctx.Selection.Clear();
        _document.CommitSelectionMutation(before);
        InvalidateVisual();
    }

    public void InvertSelection()
    {
        var before = _ctx.Selection.CaptureSnapshot();
        _ctx.Selection.Invert();
        _document.CommitSelectionMutation(before);
        InvalidateVisual();
    }

    public void ClearSelectionContent()
    {
        if (!_ctx.Selection.HasSelection) return;
        var layer = _ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked || !layer.IsVisible) return;

        var bounds = _ctx.Selection.GetMaskBounds();
        if (bounds == null) return;
        var b = bounds.Value;

        var layerBounds = new PixelRegion(
            b.Left - layer.OffsetX, b.Top - layer.OffsetY, b.Width, b.Height);
        if (layerBounds.IsEmpty) return;

        var beforeTiles = layer.Pixels.CaptureTiles(layerBounds);

        for (int docY = b.Top; docY < b.Bottom; docY++)
        {
            int layY = docY - layer.OffsetY;
            for (int docX = b.Left; docX < b.Right; docX++)
            {
                if (!_ctx.Selection.IsSelected(docX, docY)) continue;
                int layX = docX - layer.OffsetX;
                layer.Pixels.SetPixel(layX, layY, 0, 0, 0, 0);
            }
        }

        layer.MarkThumbnailDirty();
        _ctx.CommitMutation(_ctx.ActiveLayerIndex, beforeTiles, layerBounds.Translate(layer.OffsetX, layer.OffsetY));
        InvalidateVisual();
    }

    public void FillSelection()
    {
        if (!_ctx.Selection.HasSelection) return;
        var layer = _ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked || !layer.IsVisible) return;

        var bounds = _ctx.Selection.GetMaskBounds();
        if (bounds == null) return;
        var b = bounds.Value;

        var layerBounds = new PixelRegion(
            b.Left - layer.OffsetX, b.Top - layer.OffsetY, b.Width, b.Height);
        if (layerBounds.IsEmpty) return;

        var beforeTiles = layer.Pixels.CaptureTiles(layerBounds);
        var c = _paintColor;
        var changed = false;

        for (int docY = b.Top; docY < b.Bottom; docY++)
        {
            int layY = docY - layer.OffsetY;
            for (int docX = b.Left; docX < b.Right; docX++)
            {
                if (!_ctx.Selection.IsSelected(docX, docY)) continue;
                int layX = docX - layer.OffsetX;
                if (layer.IsAlphaLocked)
                {
                    layer.Pixels.GetPixel(layX, layY, out _, out _, out _, out var a);
                    if (a == 0) continue;
                }
                layer.Pixels.SetPixel(layX, layY, c.B, c.G, c.R, c.A);
                changed = true;
            }
        }

        if (!changed) return;
        layer.MarkThumbnailDirty();
        _ctx.CommitMutation(_ctx.ActiveLayerIndex, beforeTiles, layerBounds.Translate(layer.OffsetX, layer.OffsetY));
        InvalidateVisual();
    }

    public void FlipCanvas(bool horizontal)
    {
        var doc = _document;
        doc.BeginDocumentMutation();
        for (var li = 0; li < doc.Layers.Count; li++)
        {
            var layer = doc.Layers[li];
            if (layer.IsGroup || layer.IsLocked) continue;

            var lw = layer.Width;
            var lh = layer.Height;
            var pixels = layer.CapturePixels();
            if (pixels.Length == 0 || pixels.Length != lw * lh * 4) continue;

            // Preserve layer position relative to document center after flip.
            // A layer at offset (ox, oy) must land at (docW - ox - lw, oy)
            // for a horizontal flip (and similarly for vertical).
            if (horizontal)
            {
                layer.OffsetX = doc.Width - layer.OffsetX - lw;

                for (var y = 0; y < lh; y++)
                {
                    var rowStart = y * lw * 4;
                    for (var x = 0; x < lw / 2; x++)
                    {
                        var left = rowStart + x * 4;
                        var right = rowStart + (lw - 1 - x) * 4;
                        (pixels[left], pixels[right]) = (pixels[right], pixels[left]);
                        (pixels[left + 1], pixels[right + 1]) = (pixels[right + 1], pixels[left + 1]);
                        (pixels[left + 2], pixels[right + 2]) = (pixels[right + 2], pixels[left + 2]);
                        (pixels[left + 3], pixels[right + 3]) = (pixels[right + 3], pixels[left + 3]);
                    }
                }
            }
            else
            {
                layer.OffsetY = doc.Height - layer.OffsetY - lh;

                var rowSize = lw * 4;
                var flipped = new byte[pixels.Length];
                for (var y = 0; y < lh; y++)
                    System.Buffer.BlockCopy(pixels, y * rowSize, flipped, (lh - 1 - y) * rowSize, rowSize);
                pixels = flipped;
            }

            layer.RestorePixels(pixels);
        }
        doc.NotifyChanged();
        InvalidateVisual();
    }

    public void RotateCanvas90Clockwise()
    {
        var doc = _document;
        var oldW = doc.Width;
        var oldH = doc.Height;
        doc.BeginDocumentMutation();

        foreach (var layer in doc.Layers)
        {
            if (layer.IsGroup || layer.IsLocked) continue;

            var lw = layer.Width;
            var lh = layer.Height;
            var pixels = layer.CapturePixels();
            if (pixels.Length == 0 || pixels.Length != lw * lh * 4) continue;

            var ox = layer.OffsetX;
            var oy = layer.OffsetY;

            var rotated = new byte[pixels.Length];
            var newW = lh;
            var newH = lw;

            for (var y = 0; y < lh; y++)
            {
                for (var x = 0; x < lw; x++)
                {
                    var src = (y * lw + x) * 4;
                    var dst = (x * newW + (newW - 1 - y)) * 4;
                    rotated[dst] = pixels[src];
                    rotated[dst + 1] = pixels[src + 1];
                    rotated[dst + 2] = pixels[src + 2];
                    rotated[dst + 3] = pixels[src + 3];
                }
            }

            layer.Pixels.Resize(newW, newH);
            layer.Pixels.CopyFromBgra(rotated, newW, newH);
            layer.OffsetX = oldH - oy - lh;
            layer.OffsetY = ox;
            layer.MarkThumbnailDirty();
        }

        doc.SwapDimensions();
        _ctx.Selection.Resize(doc.Width, doc.Height);
        _document.NotifySelectionChanged();
        doc.NotifyChanged();
        InvalidateVisual();
    }

    public void RotateCanvas90CounterClockwise()
    {
        var doc = _document;
        var oldW = doc.Width;
        var oldH = doc.Height;
        doc.BeginDocumentMutation();

        foreach (var layer in doc.Layers)
        {
            if (layer.IsGroup || layer.IsLocked) continue;

            var lw = layer.Width;
            var lh = layer.Height;
            var pixels = layer.CapturePixels();
            if (pixels.Length == 0 || pixels.Length != lw * lh * 4) continue;

            var ox = layer.OffsetX;
            var oy = layer.OffsetY;

            var rotated = new byte[pixels.Length];
            var newW = lh;
            var newH = lw;

            for (var y = 0; y < lh; y++)
            {
                for (var x = 0; x < lw; x++)
                {
                    var src = (y * lw + x) * 4;
                    var dst = ((newH - 1 - x) * newW + y) * 4;
                    rotated[dst] = pixels[src];
                    rotated[dst + 1] = pixels[src + 1];
                    rotated[dst + 2] = pixels[src + 2];
                    rotated[dst + 3] = pixels[src + 3];
                }
            }

            layer.Pixels.Resize(newW, newH);
            layer.Pixels.CopyFromBgra(rotated, newW, newH);
            layer.OffsetX = oy;
            layer.OffsetY = oldW - ox - lw;
            layer.MarkThumbnailDirty();
        }

        doc.SwapDimensions();
        _ctx.Selection.Resize(doc.Width, doc.Height);
        _document.NotifySelectionChanged();
        doc.NotifyChanged();
        InvalidateVisual();
    }

    public void RotateCanvas180()
    {
        var doc = _document;
        doc.BeginDocumentMutation();

        foreach (var layer in doc.Layers)
        {
            if (layer.IsGroup || layer.IsLocked) continue;

            var lw = layer.Width;
            var lh = layer.Height;
            var pixels = layer.CapturePixels();
            if (pixels.Length == 0 || pixels.Length != lw * lh * 4) continue;

            var rotated = new byte[pixels.Length];

            for (var y = 0; y < lh; y++)
            {
                for (var x = 0; x < lw; x++)
                {
                    var src = (y * lw + x) * 4;
                    var dst = ((lh - 1 - y) * lw + (lw - 1 - x)) * 4;
                    rotated[dst] = pixels[src];
                    rotated[dst + 1] = pixels[src + 1];
                    rotated[dst + 2] = pixels[src + 2];
                    rotated[dst + 3] = pixels[src + 3];
                }
            }

            layer.OffsetX = doc.Width - layer.OffsetX - lw;
            layer.OffsetY = doc.Height - layer.OffsetY - lh;
            layer.RestorePixels(rotated);
            layer.MarkThumbnailDirty();
        }

        doc.NotifyChanged();
        InvalidateVisual();
    }

    // Pixel clipboard (for Copy/Paste from canvas) — instance-scoped so
    // multi-window apps don't share state and memory can be reclaimed.
    private byte[]? _clipboardPixels;
    private int _clipboardW, _clipboardH;

    // Layer clipboard (for layer panel Copy/Paste)
    private DrawingLayer? _layerClipboard;

    public void CopyToClipboard()
    {
        var composite = _compositor.CompositeToBgra(_document.Layers, _document.Width, _document.Height);
        if (composite.Length == 0) return;
        _clipboardPixels = composite;
        _clipboardW = _document.Width;
        _clipboardH = _document.Height;
    }

    public void CopySelectionAndPaste()
    {
        if (!CopySelectionToClipboard()) return;
        PasteFromClipboard();
    }

    public void CutSelectionAndPaste()
    {
        var layer = _ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || layer.IsLocked || !layer.IsVisible) return;
        if (!CopySelectionToClipboard()) return;
        ClearSelectionContent();
        PasteFromClipboard();
    }

    private bool CopySelectionToClipboard()
    {
        if (!_ctx.Selection.HasSelection) return false;
        var layer = _ctx.ActiveLayer;
        if (layer == null || layer.IsGroup || !layer.IsVisible) return false;

        var bounds = _ctx.Selection.GetMaskBounds();
        if (bounds == null) return false;

        var pixels = new byte[_document.Width * _document.Height * 4];
        var b = bounds.Value;
        var copied = false;

        for (int docY = b.Top; docY < b.Bottom; docY++)
        {
            int layY = docY - layer.OffsetY;
            for (int docX = b.Left; docX < b.Right; docX++)
            {
                if (!_ctx.Selection.IsSelected(docX, docY)) continue;
                int layX = docX - layer.OffsetX;
                layer.Pixels.GetPixel(layX, layY, out var pb, out var pg, out var pr, out var pa);
                if (pa == 0) continue;
                var dst = (docY * _document.Width + docX) * 4;
                pixels[dst] = pb;
                pixels[dst + 1] = pg;
                pixels[dst + 2] = pr;
                pixels[dst + 3] = pa;
                copied = true;
            }
        }

        if (!copied) return false;
        _clipboardPixels = pixels;
        _clipboardW = _document.Width;
        _clipboardH = _document.Height;
        return true;
    }

    public bool CanPaste => _clipboardPixels != null && _clipboardW > 0 && _clipboardH > 0;

    public void PasteFromClipboard()
    {
        if (!CanPaste) return;
        var pixels = _clipboardPixels!;
        var w = _clipboardW;
        var h = _clipboardH;

        _document.BeginDocumentMutation();
        var layer = new DrawingLayer("Pasted", _document.Width, _document.Height);
        layer.Pixels.CopyFromBgra(pixels, w, h);
        var insertIdx = Math.Min(_document.ActiveLayerIndex + 1, _document.Layers.Count);
        _document.InsertAndSelectLayer(layer, insertIdx);
        InvalidateVisual();
    }

    public async Task<bool> PasteFromOSClipboardAsync()
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null) return false;
        var clipboard = topLevel.Clipboard;
        if (clipboard == null) return false;

        var dataTransfer = await clipboard.TryGetDataAsync();
        if (dataTransfer == null) return false;
        var items = dataTransfer.Items;
        if (items.Count == 0) return false;

        var item = items[0];

        try
        {
            var obj = await item.TryGetRawAsync(DataFormat.Bitmap);
            if (obj is Bitmap bitmap)
                return PasteBitmap(bitmap);
        }
        catch { }

        try
        {
            var obj = await item.TryGetRawAsync(DataFormat.File);
            if (obj is IEnumerable<IStorageFile> files)
            {
                foreach (var file in files)
                    if (await TryPasteStorageFileAsync(file)) return true;
            }
            else if (obj is IStorageFile singleFile)
                return await TryPasteStorageFileAsync(singleFile);
        }
        catch { }

        return false;
    }

    private async Task<bool> TryPasteStorageFileAsync(IStorageFile file)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var skBitmap = SKBitmap.Decode(stream);
            if (skBitmap != null)
            {
                PasteSKBitmap(skBitmap, Path.GetFileNameWithoutExtension(file.Name));
                return true;
            }
        }
        catch { }
        return false;
    }

    private bool PasteBitmap(Bitmap bitmap)
    {
        using var ms = new MemoryStream();
        bitmap.Save(ms);
        ms.Position = 0;
        using var skBitmap = SKBitmap.Decode(ms);
        if (skBitmap == null) return false;
        return PasteSKBitmap(skBitmap, "Pasted");
    }

    private bool PasteSKBitmap(SKBitmap skBitmap, string name)
    {
        var w = skBitmap.Width;
        var h = skBitmap.Height;

        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = skBitmap.GetPixel(x, y);
                var offset = (y * w + x) * 4;
                pixels[offset] = c.Blue;
                pixels[offset + 1] = c.Green;
                pixels[offset + 2] = c.Red;
                pixels[offset + 3] = c.Alpha;
            }
        }

        _document.BeginDocumentMutation();
        var layer = new DrawingLayer(name, _document.Width, _document.Height);
        layer.Pixels.CopyFromBgra(pixels, w, h);
        layer.MarkThumbnailDirty();
        var insertIdx = Math.Min(_document.ActiveLayerIndex + 1, _document.Layers.Count);
        _document.InsertAndSelectLayer(layer, insertIdx);
        InvalidateVisual();
        BeginSelectionTransform();
        return true;
    }

    public bool IsTransformActive => _toolController.ActiveTool is TransformTool tt && tt.HasPendingOperation;

    private void NotifySelectionChanged()
    {
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        InvalidateVisual();
    }

    public void Clear(bool pushHistory = true)
    {
        if (!_document.CanPaintActiveLayer) return;
        _document.ClearActiveLayer(pushHistory);
    }
    public void Undo() => _document.Undo();
    public void Redo() => _document.Redo();
    public void AddLayer() => _document.AddLayer();
    public void AddGroupLayer() => _document.AddGroupLayer();
    public void AddBackgroundLayer() => _document.AddBackgroundLayer();
    public void GroupSelectedLayers(IReadOnlyList<int> indices) => _document.GroupSelectedLayers(indices);
    public void DuplicateLayer() => _document.DuplicateActiveLayer();
    public void DeleteLayer()
    {
        if (!_document.CanModifyActiveLayer) return;
        _document.DeleteActiveLayer();
    }
    public void SelectLayer(int index) => _document.SelectLayer(index);
    public void ToggleLayerVisibility(int index) => _document.ToggleLayerVisibility(index);
    public void ToggleLayerLock(int index) => _document.ToggleLayerLock(index);
    public void ToggleLayerAlphaLock(int index) => _document.ToggleLayerAlphaLock(index);
    public void ToggleLayerReference(int index) => _document.ToggleLayerReference(index);
    public void ToggleLayerClipping(int index) => _document.ToggleLayerClipping(index);
    public void ToggleLayerOpen(int index) => _document.ToggleLayerOpen(index);
    public bool CanMoveLayer(int sourceIndex, int targetIndex, LayerDropPlacement placement) => _document.CanMoveLayer(sourceIndex, targetIndex, placement);
    public void MoveLayer(int sourceIndex, int targetIndex, LayerDropPlacement placement) => _document.MoveLayer(sourceIndex, targetIndex, placement);
    public void MoveActiveLayer(int delta) => _document.MoveActiveLayer(delta);
    public void SetActiveLayerOpacity(double opacity) => _document.SetActiveLayerOpacity(opacity);
    public void SetActiveLayerBlendMode(string blendMode) => _document.SetActiveLayerBlendMode(blendMode);
    public void SetActiveLayerName(string name) => _document.SetActiveLayerName(name);
    public void SetActiveLayerColor(Avalonia.Media.Color? color) => _document.SetActiveLayerColor(color);

    public void CopyLayer(int index)
    {
        if (index < 0 || index >= _document.Layers.Count) return;
        _layerClipboard = DrawingDocument.CloneLayerTree(_document.Layers[index]);
    }

    public bool CanPasteLayer => _layerClipboard != null;

    public void PasteLayer(int targetIndex)
    {
        if (_layerClipboard == null) return;
        if (targetIndex < 0 || targetIndex >= _document.Layers.Count) return;
        _document.PasteLayer(_layerClipboard, targetIndex);
    }

    private bool IsPaintBlockedByLock =>
        _toolController.AlternateTool == null
        && IsPaintTool(_toolController.ActiveTool)
        && !_document.CanPaintActiveLayer;

    private static bool IsPaintTool(ITool? tool) => tool is CompositeTool ct && ct.Output.IsPaintOutput;

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Sync cursor to current state so layer lock changes take effect immediately.
        if (_isPointerOver && !(_toolController.ActiveTool is TransformTool { HasPendingOperation: true }))
            Cursor = IsPaintBlockedByLock ? CursorNo : CursorNone;

        _compositor.Composite(_document.Layers, _document.Width, _document.Height);
        var target = new Rect(Bounds.Size);
        using (context.PushClip(new RoundedRect(target)))
        using (context.PushRenderOptions(new RenderOptions
        {
            BitmapInterpolationMode = BitmapInterpolationMode.None,
            EdgeMode = EdgeMode.Aliased
        }))
        {
            context.DrawImage(_compositor.Bitmap, target);
        }

        _toolController.RenderOverlay(context, CanvasZoom);
        if (_toolController.ActiveTool is not Floss.App.Tools.TransformTool)
            _ctx.Selection.RenderOverlay(context, CanvasZoom);

        if (_isLayerPickDrag)
        {
            var rx = Math.Min(_layerPickAnchor.X, _layerPickCurrent.X);
            var ry = Math.Min(_layerPickAnchor.Y, _layerPickCurrent.Y);
            var rw = Math.Abs(_layerPickCurrent.X - _layerPickAnchor.X);
            var rh = Math.Abs(_layerPickCurrent.Y - _layerPickAnchor.Y);
            var t = Math.Max(0.5, 1.0 / CanvasZoom);
            var dash1 = new Avalonia.Media.DashStyle([4 / CanvasZoom, 4 / CanvasZoom], 0);
            var dash2 = new Avalonia.Media.DashStyle([4 / CanvasZoom, 4 / CanvasZoom], 4 / CanvasZoom);
            context.DrawRectangle(null, new Pen(CursorOuterBrush, t * 2, dash1), new Rect(rx, ry, rw, rh));
            context.DrawRectangle(null, new Pen(CursorInnerBrush, t, dash2), new Rect(rx, ry, rw, rh));
        }

        if ((_isPointerOver || _isCursorPreviewLocked) && !IsPaintBlockedByLock)
        {
            var mode = App.Config.BrushCursorMode;
            var pos = _isCursorPreviewLocked ? _lockedPointerPos : _pointerPos;
            var t = Math.Max(0.5, 1.5 / CanvasZoom);
            bool isBrushLike = _toolController.ActiveTool is CompositeTool ct && ct.Input.HasBrushCursor;
            if (_forceBrushOutlineCursor || (isBrushLike && mode is BrushCursorMode.Outline or BrushCursorMode.DotAndOutline))
            {
                var r = ActiveToolCursorSize() * 0.5;
                context.DrawEllipse(null, new Pen(CursorOuterBrush, t * 3), pos, r, r);
                context.DrawEllipse(null, new Pen(CursorInnerBrush, t), pos, r, r);
            }

            if (!isBrushLike || mode is BrushCursorMode.Dot or BrushCursorMode.DotAndOutline)
            {
                var r = Math.Max(2.5 / CanvasZoom, t * 2);
                context.DrawEllipse(CursorOuterBrush, null, pos, r, r);
                context.DrawEllipse(CursorInnerBrush, null, pos, Math.Max(0.5 / CanvasZoom, r * 0.45), Math.Max(0.5 / CanvasZoom, r * 0.45));
            }

            if (_toolController.AlternateTool is CompositeTool altCt && altCt.Output is EyedropperOutput
                || _ctx.ActivePreset?.OutputProcess == Floss.App.OutputProcessType.Eyedropper)
            {
                var swatchR = 10.0 / CanvasZoom;
                var swatchPos = new Point(pos.X + swatchR * 1.6, pos.Y - swatchR * 1.6);
                var colorBrush = new SolidColorBrush(_paintColor);
                context.DrawEllipse(colorBrush, new Pen(CursorOuterBrush, t * 2), swatchPos, swatchR, swatchR);
                context.DrawEllipse(null, new Pen(CursorInnerBrush, t), swatchPos, swatchR, swatchR);
            }
        }
    }

    private double ActiveToolCursorSize()
        => _ctx.ActivePreset?.OutputProcess switch
        {
            OutputProcessType.Liquify => _ctx.ActivePreset.LiquifySize,
            _ => _brush.Size
        };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);

        // Wayland / tablet: pen proximity fires PointerPressed with IsLeftButtonPressed=true
        // but Pressure=0.  Block these phantom events before any tool logic runs.
        if ((point.Pointer.Type == PointerType.Pen || point.Properties.IsEraser) &&
            point.Properties.Pressure < PenPressureThreshold)
            return;

        // Ctrl+Shift+Left: start a layer-find drag (release decides point-pick vs rect-pick).
        if (point.Properties.IsLeftButtonPressed &&
            e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _isLayerPickDrag = true;
            _layerPickAnchor = point.Position;
            _layerPickCurrent = point.Position;
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (PaintInputSuspended && _toolController.AlternateTool == null) return;
        if (IsPaintBlockedByLock) return;

        if (!IsPaintInput(point)) return;

        Focus();
        var sample = MakeSample(point, CanvasInputPhase.Down);

        // Auto-switch to eraser when pen eraser tip is used with a paint tool.
        if (sample.Source == CanvasInputSource.Eraser && _brush.BlendMode != SkiaSharp.SKBlendMode.DstOut)
        {
            _brush = _brush with { BlendMode = SkiaSharp.SKBlendMode.DstOut };
            _ctx.Brush = _brush;
            InvalidateVisual();
        }

        _activePointerId = point.Pointer.Id;
        _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Down, sample));

        if (e.ClickCount > 1 && CanCommitActiveToolFromClick())
        {
            CommitActiveTool();
            _activePointerId = -1;
        }
        else
        {
            e.Pointer.Capture(this);
        }

        e.Handled = true;
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        Cursor = IsPaintBlockedByLock ? CursorNo : CursorNone;
        _pointerPos = e.GetCurrentPoint(this).Position;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        if (!_isCursorPreviewLocked)
            _pointerPos = point.Position;

        if (_isLayerPickDrag)
        {
            _layerPickCurrent = point.Position;
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        // Transform cursor override
        if (_toolController.ActiveTool is TransformTool tt && tt.HasPendingOperation)
        {
            var canvasPos = new Point(
                point.Position.X / Math.Max(Bounds.Width, 1) * _document.Width,
                point.Position.Y / Math.Max(Bounds.Height, 1) * _document.Height);
            var cursor = tt.CursorFor(canvasPos, CanvasZoom);
            if (cursor.HasValue)
                Cursor = new Cursor(cursor.Value);
        }

        if (PaintInputSuspended && _toolController.AlternateTool == null)
        {
            if (_activePointerId >= 0)
            {
                _activePointerId = -1;
                _toolController.Cancel();
                e.Pointer.Capture(null);
            }
            InvalidateVisual();
            return;
        }

        if (point.Pointer.Id == _activePointerId)
        {
            if (IsPaintInput(point))
            {
                _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Move, MakeSample(point, CanvasInputPhase.Move)));
            }
            else
            {
                _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Up, MakeSample(point, CanvasInputPhase.Up)));
                _activePointerId = -1;
                e.Pointer.Capture(null);
            }

            e.Handled = true;
        }
        else if (_activePointerId < 0)
        {
            _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Move, MakeSample(point, CanvasInputPhase.Move)));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var point = e.GetCurrentPoint(this);

        if (_isLayerPickDrag)
        {
            _isLayerPickDrag = false;
            e.Pointer.Capture(null);
            InvalidateVisual();

            var dx = _layerPickCurrent.X - _layerPickAnchor.X;
            var dy = _layerPickCurrent.Y - _layerPickAnchor.Y;
            var dragDist = Math.Sqrt(dx * dx + dy * dy);

            if (dragDist < 4)
            {
                // Small movement — treat as point pick.
                TryPickLayerAtPoint((int)_layerPickAnchor.X, (int)_layerPickAnchor.Y);
            }
            else
            {
                // Rect pick — find all layers with content in the rectangle.
                var rx = (int)Math.Min(_layerPickAnchor.X, _layerPickCurrent.X);
                var ry = (int)Math.Min(_layerPickAnchor.Y, _layerPickCurrent.Y);
                var rw = (int)Math.Abs(_layerPickCurrent.X - _layerPickAnchor.X) + 1;
                var rh = (int)Math.Abs(_layerPickCurrent.Y - _layerPickAnchor.Y) + 1;
                var found = FindLayersInRect(rx, ry, rw, rh);
                if (found.Count > 0)
                    LayersFoundByRect?.Invoke(found);
            }

            e.Handled = true;
            return;
        }

        if (point.Pointer.Id != _activePointerId) return;

        _toolController.Dispatch(new ToolInputEvent(ToolInputEventKind.Up, MakeSample(point, CanvasInputPhase.Up)));
        _activePointerId = -1;
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        if (_isLayerPickDrag) { _isLayerPickDrag = false; InvalidateVisual(); }
        if (_activePointerId < 0) return;
        _activePointerId = -1;
        _toolController.Cancel();
    }

    private bool CanCommitActiveToolFromClick() =>
        _toolController.ActiveTool.CanCommitFromClick;

    private CanvasInputSample MakeSample(PointerPoint point, CanvasInputPhase phase)
        => CanvasInputSample.FromPointerPoint(
            point, Bounds.Size,
            _document.Width, _document.Height,
            phase);

    private void TryPickLayerAtPoint(int x, int y)
    {
        var layers = _document.Layers;
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (!layer.IsVisible || layer.IsGroup) continue;
            layer.Pixels.GetPixel(x - layer.OffsetX, y - layer.OffsetY, out _, out _, out _, out byte a);
            if (a > 0)
            {
                _document.SelectLayer(i);
                return;
            }
        }
    }

    private List<int> FindLayersInRect(int x, int y, int w, int h)
    {
        var found = new List<int>();
        var layers = _document.Layers;
        for (int i = layers.Count - 1; i >= 0; i--)
        {
            var layer = layers[i];
            if (!layer.IsVisible || layer.IsGroup) continue;
            var region = new PixelRegion(x - layer.OffsetX, y - layer.OffsetY, w, h);
            if (layer.Pixels.HasNonTransparentPixels(region))
                found.Add(i);
        }
        return found;
    }

    private Color? SampleDocumentColor(int x, int y, EyedropperSampleOptions options)
    {
        if ((uint)x >= (uint)_document.Width || (uint)y >= (uint)_document.Height) return null;

        if (options.Mode == EyedropperSampleMode.CurrentLayer)
        {
            var layer = _document.ActiveLayerIndex >= 0 && _document.ActiveLayerIndex < _document.Layers.Count
                ? _document.Layers[_document.ActiveLayerIndex]
                : null;
            if (layer == null || ShouldSkipSampleLayer(layer, options)) return null;
            return SampleSingleLayer(layer, x, y, applyOpacity: false);
        }

        // Composite sample from all visible layers (bottom to top).
        double accR = 0, accG = 0, accB = 0, accA = 0;
        bool any = false;

        foreach (var layer in _document.Layers)
        {
            if (ShouldSkipSampleLayer(layer, options)) continue;
            var color = SampleSingleLayer(layer, x, y, applyOpacity: true);
            if (color == null) continue;
            any = true;

            double srcA = color.Value.A / 255.0;
            if (srcA <= 0) continue;

            double dstA = accA;
            double outA = srcA + dstA * (1 - srcA);
            if (outA > 0)
            {
                accR = (color.Value.R * srcA + accR * dstA * (1 - srcA)) / outA;
                accG = (color.Value.G * srcA + accG * dstA * (1 - srcA)) / outA;
                accB = (color.Value.B * srcA + accB * dstA * (1 - srcA)) / outA;
                accA = outA;
            }
        }

        if (!any) return null;
        return Color.FromArgb((byte)Math.Clamp(accA * 255, 0, 255), (byte)Math.Clamp(accR, 0, 255), (byte)Math.Clamp(accG, 0, 255), (byte)Math.Clamp(accB, 0, 255));
    }

    private static Color? SampleSingleLayer(DrawingLayer layer, int x, int y, bool applyOpacity)
    {
        int lx = x - layer.OffsetX;
        int ly = y - layer.OffsetY;
        layer.Pixels.GetPixel(lx, ly, out byte b, out byte g, out byte r, out byte a);
        if (a == 0) return null;
        var alpha = applyOpacity ? (byte)Math.Clamp(a * layer.Opacity, 0, 255) : a;
        return Color.FromArgb(alpha, r, g, b);
    }

    private static bool ShouldSkipSampleLayer(DrawingLayer layer, EyedropperSampleOptions options)
    {
        if (!layer.IsVisible || layer.IsGroup) return true;
        if (options.ExcludeLockedLayers && IsLockedInTree(layer)) return true;
        if (options.ExcludeReferenceLayers && IsReferenceInTree(layer)) return true;
        return false;
    }

    private static bool IsLockedInTree(DrawingLayer layer)
    {
        for (var current = layer; current != null; current = current.Parent)
            if (current.IsLocked) return true;
        return false;
    }

    private static bool IsReferenceInTree(DrawingLayer layer)
    {
        for (var current = layer; current != null; current = current.Parent)
            if (current.IsReference) return true;
        return false;
    }

    private static bool IsPaintInput(PointerPoint point)
    {
        var properties = point.Properties;
        if (properties.IsEraser)
            return properties.IsLeftButtonPressed || properties.Pressure >= PenPressureThreshold;

        return point.Pointer.Type switch
        {
            PointerType.Pen => properties.IsLeftButtonPressed || properties.Pressure >= PenPressureThreshold,
            PointerType.Touch => properties.IsLeftButtonPressed || properties.Pressure >= PenPressureThreshold,
            PointerType.Mouse => properties.IsLeftButtonPressed,
            _ => false
        };
    }
}
