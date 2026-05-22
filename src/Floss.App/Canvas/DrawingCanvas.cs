using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Tools;
using SkiaSharp;

namespace Floss.App.Canvas;

public sealed class DrawingCanvas : Control, IDisposable
{
    private const double CursorDirectionDeadZone = 1.25;
    private const float CursorAngleLerp = 0.35f;

    private readonly DrawingDocument _document = new();
    private readonly LayerCompositor _compositor;
    private readonly ProjectionUpdateScheduler _projectionScheduler;
    private readonly ToolContext _ctx;
    private readonly ToolController _toolController;

    private readonly CompositeTool _brushTool;
    private readonly CompositeTool _eraserTool;
    private readonly TransformTool _transformTool = new();

    private BrushPreset _brush = BrushPreset.Defaults[0];
    private Color _paintColor = Color.Parse("#111111");
    private long _activePointerId = -1;
    private Point _pointerPos;
    private Point _prevPointerPos;
    private Point _lastCursorDirectionPos;
    private bool _hasCursorDirectionPos;
    private bool _hasStableCursorDirection;
    private float _stableCursorDirectionDeg;
    private float _pointerTiltX;
    private float _pointerTiltY;
    private float _pointerTwist;
    private Point _lockedPointerPos;
    private bool _isPointerOver;
    private bool _isCursorPreviewLocked;
    private bool _forceBrushOutlineCursor;
    private bool _deferredTileRenderQueued;
    private (IBrushTip? Tip, SKBitmap? Outline) _cursorOutlineCache;

    // Ctrl+Shift layer-pick drag state
    private bool _isLayerPickDrag;
    private Point _layerPickAnchor;
    private Point _layerPickCurrent;

    private static readonly Cursor CursorNo = new(StandardCursorType.No);
    private static readonly Cursor CursorNone = new(StandardCursorType.None);
    private static readonly Cursor CursorDefault = new(StandardCursorType.Arrow);

    private static readonly IBrush CursorOuterBrush = new SolidColorBrush(Color.FromArgb(160, 0, 0, 0));
    private static readonly IBrush CursorInnerBrush = new SolidColorBrush(Colors.White);

    private static readonly IBrush CanvasCheckerBrush = new DrawingBrush
    {
        TileMode = TileMode.Tile,
        DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#555555")), Geometry = new RectangleGeometry(new Rect(0, 0, 8, 8)) },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#555555")), Geometry = new RectangleGeometry(new Rect(8, 8, 8, 8)) },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#aaaaaa")), Geometry = new RectangleGeometry(new Rect(8, 0, 8, 8)) },
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#aaaaaa")), Geometry = new RectangleGeometry(new Rect(0, 8, 8, 8)) },
            }
        }
    };

    public BrushEngine BrushEngine { get; }

    public DrawingCanvas()
    {
        _document.DirtyStateChanged += (_, _) => DirtyStateChanged?.Invoke(this, EventArgs.Empty);
        Focusable = true;
        ClipToBounds = false;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        BrushEngine = new BrushEngine();
        _compositor = new LayerCompositor();
        _projectionScheduler = new ProjectionUpdateScheduler(InvalidateVisual);

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
            SampleDocumentColor = SampleDocumentColor,
            OnSelectLayer = i => LayersChanged?.Invoke(this, EventArgs.Empty),
            OnSelectLayers = indices => LayersFoundByRect?.Invoke(indices)
        };

        _brushTool = new CompositeTool(new BrushStrokeInputProcess(), new DirectDrawOutput(BrushEngine, _document));
        _eraserTool = new CompositeTool(new BrushStrokeInputProcess(), new DirectDrawOutput(BrushEngine, _document));
        _toolController = new ToolController(_ctx, _brushTool);

        _document.Changed += (_, e) =>
        {
            _projectionScheduler.Invalidate(e.DirtyRegion, _document.Layers, e.LayerIndex);
            StatsChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
        _document.SelectionChanged += (_, _) => NotifySelectionChanged();
        _document.LayersChanged += (_, _) =>
        {
            _projectionScheduler.Invalidate(null);
            LayersChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.LayerRemoved += (_, layer) => _compositor.RemoveGroupCache(layer);
        _document.LayerMetadataChanged += (_, e) => LayerMetadataChanged?.Invoke(this, e);
    }

    public void InvalidateCompositor() => _projectionScheduler.Invalidate(null);

    public void SetCurrentModifiers(Avalonia.Input.KeyModifiers mods) => _ctx.CurrentModifiers = mods;
    public void SetToolAuxMode(ToolAuxOperationType mode)
    {
        if (_ctx.ToolAuxMode == mode) return;
        _ctx.ToolAuxMode = mode;
        if (_toolController.ActiveTool is Processes.CompositeTool ct)
            ct.Input.ToolAuxMode = mode;
        InvalidateVisual();
    }

    public void Dispose()
    {
        BrushEngine.Dispose();
        _compositor.Dispose();
        _document.Dispose();
        _layerClipboard = null;
        _clipboardPixels = null;
        _cursorOutlineCache.Outline?.Dispose();
        _cursorOutlineCache = default;
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
    public bool HasDocument => _document.Layers.Count > 0;

    public bool PaintInputSuspended { get; set; }
    private double _canvasZoom = 1.0;
    public double CanvasZoom
    {
        get => _canvasZoom;
        set
        {
            _canvasZoom = value;
            RenderOptions.SetBitmapInterpolationMode(this,
                value >= 1.0 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
        }
    }

    public void SetViewport(IViewportController vp, double width, double height)
    {
        _ctx.Viewport = vp;
        _ctx.ViewportSize = new Size(width, height);
    }

    public void HandlePointerInput(ToolInputEventKind kind, PointerPoint point)
    {
        var phase = kind switch
        {
            ToolInputEventKind.Down => CanvasInputPhase.Down,
            ToolInputEventKind.Move => CanvasInputPhase.Move,
            _ => CanvasInputPhase.Up
        };
        var w = Math.Max(1, Bounds.Width);
        var h = Math.Max(1, Bounds.Height);

        var sampleInfo = GetCanvasInputSampleInfo(point);
        var props = point.Properties;

        var docX = point.Position.X / w * _document.Width;
        var docY = point.Position.Y / h * _document.Height;

        // Let coordinates go outside document bounds — the brush engine clips stamps
        // to the document naturally. Clamping would draw a hard line along the edge.

        // Sync cursor position so custom cursor renders during captured strokes
        // even when OnPointerMoved is not firing on this control.
        if (!_isCursorPreviewLocked)
        {
            _prevPointerPos = _pointerPos;
            _pointerPos = point.Position;
        }
        _pointerTiltX = (float)props.XTilt;
        _pointerTiltY = (float)props.YTilt;
        _pointerTwist = (float)props.Twist;

        var sample = new CanvasInputSample(
            docX,
            docY,
            sampleInfo.Pressure,
            props.XTilt,
            props.YTilt,
            props.Twist,
            Environment.TickCount64 * 1000,
            point.Pointer.Id,
            sampleInfo.Source,
            phase);
        _toolController.Dispatch(new ToolInputEvent(kind, sample));
    }

    // Viewport tools (Hand, Rotate, Zoom) need viewport coordinates because
    // canvas-local coords change dynamically as zoom/pan change. Passing
    // viewport pixels keeps deltas stable.
    public void HandleViewportPointerInput(ToolInputEventKind kind, Point viewportPos, PointerPoint point)
    {
        var phase = kind switch
        {
            ToolInputEventKind.Down => CanvasInputPhase.Down,
            ToolInputEventKind.Move => CanvasInputPhase.Move,
            _ => CanvasInputPhase.Up
        };

        var props = point.Properties;
        var sampleInfo = GetCanvasInputSampleInfo(point);

        var sample = new CanvasInputSample(
            viewportPos.X,
            viewportPos.Y,
            sampleInfo.Pressure,
            props.XTilt,
            props.YTilt,
            props.Twist,
            Environment.TickCount64 * 1000,
            point.Pointer.Id,
            sampleInfo.Source,
            phase);
        _toolController.DispatchViewport(new ToolInputEvent(kind, sample));
    }

    private static (CanvasInputSource Source, double Pressure) GetCanvasInputSampleInfo(PointerPoint point)
    {
        var props = point.Properties;
        var source = SourceFromPointer(point);
        var pressure = props.Pressure;
        if (double.IsNaN(pressure)) pressure = 0;

        if (source == CanvasInputSource.Mouse && props.IsLeftButtonPressed)
        {
            var hasTabletData = pressure > 0 || props.XTilt != 0 || props.YTilt != 0 || props.Twist != 0;
            if (!hasTabletData && pressure <= 0)
                pressure = 1;
        }
        else if ((source is CanvasInputSource.Pen or CanvasInputSource.Eraser or CanvasInputSource.Touch) && pressure < 0)
        {
            pressure = 0;
        }

        return (source, Math.Clamp(pressure, 0, 1));
    }

    private static CanvasInputSource SourceFromPointer(PointerPoint point)
    {
        if (point.Properties.IsEraser)
            return CanvasInputSource.Eraser;

        // Some tablet drivers report the pen as a generic mouse but still send
        // pressure/tilt data. Detect that and treat it as a pen so dynamics work.
        if (point.Pointer.Type == PointerType.Mouse)
        {
            var p = point.Properties;
            if (p.Pressure > 0 || p.XTilt != 0 || p.YTilt != 0 || p.Twist != 0)
                return CanvasInputSource.Pen;
        }

        return point.Pointer.Type switch
        {
            PointerType.Pen => CanvasInputSource.Pen,
            PointerType.Touch => CanvasInputSource.Touch,
            PointerType.Mouse => CanvasInputSource.Mouse,
            _ => CanvasInputSource.Unknown
        };
    }

    public double PanOffsetX { get; set; }
    public double PanOffsetY { get; set; }
    public int FlipX { get; set; } = 1;
    public int FlipY { get; set; } = 1;
    public double CanvasRotation { get; set; }
    public double ViewportWidth { get; set; }
    public double ViewportHeight { get; set; }

    public void SetActiveTool(ITool tool, ToolPreset? preset = null)
    {
        _toolController.SetActiveTool(tool, preset);
        InvalidateVisual();
    }

    public bool HasViewportNavOverlay => _toolController.HasViewportNavOverlay;

    public bool PushViewportNavOverlay(ITool tool)
        => _toolController.PushViewportNavOverlay(tool);

    public void PopViewportNavOverlay()
        => _toolController.PopViewportNavOverlay();

    public ITool? AlternateTool => _toolController.IsAlternateActive ? _toolController.ActiveTool.Alternate : null;
    public void SetAlternateActive(bool active) => _toolController.SetAlternateActive(active);
    public bool IsAlternateActive => _toolController.IsAlternateActive;

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

        if (_brushTool.Input is BrushStrokeInputProcess brushInput)
            brushInput.Stabilization = brush.Smoothing;
        if (_eraserTool.Input is BrushStrokeInputProcess eraserInput)
            eraserInput.Stabilization = brush.Smoothing;

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

    public void SetBrushQuality(BrushQuality quality)
    {
        _brush = _brush with { Quality = quality };
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
        _prevPointerPos = _lockedPointerPos;
        _pointerPos = _lockedPointerPos;
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
        if (_document.ActiveLayer is not { } layer) return;
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
        var oldH = doc.Height;
        doc.BeginDocumentMutation();
        foreach (var layer in doc.Layers)
        {
            if (layer.IsGroup || layer.IsLocked) continue;
            var lw = layer.Width; var lh = layer.Height;
            var pixels = layer.CapturePixels();
            if (pixels.Length == 0 || pixels.Length != lw * lh * 4) continue;
            var ox = layer.OffsetX; var oy = layer.OffsetY;
            var rotated = RotatePixelBuffer(pixels, lw, lh, lh, lw,
                (x, y, nw) => (x * nw + (nw - 1 - y)) * 4);
            layer.Pixels.Resize(lh, lw);
            layer.Pixels.CopyFromBgra(rotated, lh, lw);
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
        doc.BeginDocumentMutation();
        foreach (var layer in doc.Layers)
        {
            if (layer.IsGroup || layer.IsLocked) continue;
            var lw = layer.Width; var lh = layer.Height;
            var pixels = layer.CapturePixels();
            if (pixels.Length == 0 || pixels.Length != lw * lh * 4) continue;
            var ox = layer.OffsetX; var oy = layer.OffsetY;
            var rotated = RotatePixelBuffer(pixels, lw, lh, lh, lw,
                (x, y, nw) => ((lw - 1 - x) * nw + y) * 4);
            layer.Pixels.Resize(lh, lw);
            layer.Pixels.CopyFromBgra(rotated, lh, lw);
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
            var lw = layer.Width; var lh = layer.Height;
            var pixels = layer.CapturePixels();
            if (pixels.Length == 0 || pixels.Length != lw * lh * 4) continue;
            var rotated = RotatePixelBuffer(pixels, lw, lh, lw, lh,
                (x, y, nw) => ((lh - 1 - y) * nw + (lw - 1 - x)) * 4);
            layer.OffsetX = doc.Width - layer.OffsetX - lw;
            layer.OffsetY = doc.Height - layer.OffsetY - lh;
            layer.RestorePixels(rotated);
            layer.MarkThumbnailDirty();
        }
        doc.NotifyChanged();
        InvalidateVisual();
    }

    private static byte[] RotatePixelBuffer(byte[] pixels, int lw, int lh, int newW, int newH,
        Func<int, int, int, int> dstIndex)
    {
        var rotated = new byte[newW * newH * 4];
        for (var y = 0; y < lh; y++)
        for (var x = 0; x < lw; x++)
        {
            var src = (y * lw + x) * 4;
            var dst = dstIndex(x, y, newW);
            rotated[dst] = pixels[src];
            rotated[dst + 1] = pixels[src + 1];
            rotated[dst + 2] = pixels[src + 2];
            rotated[dst + 3] = pixels[src + 3];
        }
        return rotated;
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
            }
        }

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
        catch (Exception ex) { CrashLog.Write(ex, "DrawingCanvas.PasteFromClipboard (bitmap)"); }

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
        catch (Exception ex) { CrashLog.Write(ex, "DrawingCanvas.PasteFromClipboard (file)"); }

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
        catch (Exception ex) { CrashLog.Write(ex, "DrawingCanvas.TryPasteStorageFileAsync"); }
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
    public void Undo()
    {
        if (_toolController.HasPendingOperation)
            _toolController.Cancel();
        _document.Undo();
    }

    public void Redo()
    {
        if (_toolController.HasPendingOperation)
            _toolController.Cancel();
        _document.Redo();
    }
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
        !_toolController.IsAlternateActive
        && IsPaintTool(_toolController.ActiveTool)
        && !_document.CanPaintActiveLayer;

    private static bool IsPaintTool(ITool? tool) => tool is CompositeTool ct && ct.Output.IsPaintOutput;

    private static bool HasAnyLayerContent(IReadOnlyList<DrawingLayer> layers)
    {
        foreach (var l in layers)
        {
            if (!l.IsVisible) continue;
            if (l.Pixels.TileCount > 0) return true;
            if (l.IsGroup && HasAnyLayerContent(l.Children)) return true;
        }
        return false;
    }

    private PixelRegion? ComputeVisibleViewport()
    {
        if (ViewportWidth <= 0 || ViewportHeight <= 0 || CanvasZoom <= 0)
            return null;

        var zoom = CanvasZoom;
        var docW = _document.Width;
        var docH = _document.Height;

        var angle = CanvasRotation * Math.PI / 180.0;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var viewportCenterX = ViewportWidth * 0.5;
        var viewportCenterY = ViewportHeight * 0.5;
        var docCenterX = docW * 0.5;
        var docCenterY = docH * 0.5;
        var flipX = FlipX == 0 ? 1 : FlipX;
        var flipY = FlipY == 0 ? 1 : FlipY;

        var minX = double.PositiveInfinity;
        var minY = double.PositiveInfinity;
        var maxX = double.NegativeInfinity;
        var maxY = double.NegativeInfinity;

        foreach (var corner in new[]
                 {
                     new Point(0, 0),
                     new Point(ViewportWidth, 0),
                     new Point(ViewportWidth, ViewportHeight),
                     new Point(0, ViewportHeight)
                 })
        {
            var sx = corner.X - viewportCenterX - PanOffsetX;
            var sy = corner.Y - viewportCenterY - PanOffsetY;

            // Invert TransformGroup order around RenderTransformOrigin:
            // flip -> zoom -> rotate -> pan.
            var unrotatedX = sx * cos + sy * sin;
            var unrotatedY = -sx * sin + sy * cos;
            var docX = docCenterX + unrotatedX / (zoom * flipX);
            var docY = docCenterY + unrotatedY / (zoom * flipY);

            minX = Math.Min(minX, docX);
            minY = Math.Min(minY, docY);
            maxX = Math.Max(maxX, docX);
            maxY = Math.Max(maxY, docY);
        }

        const int margin = 4;
        var left = (int)Math.Max(0, Math.Floor(minX) - margin);
        var top = (int)Math.Max(0, Math.Floor(minY) - margin);
        var right = (int)Math.Min(docW, Math.Ceiling(maxX) + margin);
        var bottom = (int)Math.Min(docH, Math.Ceiling(maxY) + margin);
        var w = Math.Max(1, right - left);
        var h = Math.Max(1, bottom - top);

        return new PixelRegion(left, top, w, h);
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        // Skip compositing on empty-canvas documents — avoids allocating a
        // full-canvas WriteableBitmap (900MB+ for 15k²) when nothing is drawn.
        var viewport = ComputeVisibleViewport();
        var canvasBounds = new Rect(Bounds.Size);
        _projectionScheduler.ApplyPending(_compositor);
        {
            var paper = _document.PaperLayer;
            bool hasSolidPaper = paper is { IsVisible: true } && _document.PaperColor.A == 255;
            if (hasSolidPaper)
                context.FillRectangle(new SolidColorBrush(_document.PaperColor), canvasBounds);
            else
                context.FillRectangle(CanvasCheckerBrush, canvasBounds);
        }
        if (HasAnyLayerContent(_document.Layers))
        {
            var paper = _document.PaperLayer;
            uint paperUint = paper is { IsVisible: true } ? ColorToBgraUint(_document.PaperColor) : 0u;
            using (_document.RenderLock.Read())
            {
                if (_compositor.Composite(_document.Layers, _document.Width, _document.Height, paperUint, viewport, CanvasZoom))
                    QueueDeferredTileRender();
                using (context.PushClip(new RoundedRect(canvasBounds)))
                using (context.PushRenderOptions(new RenderOptions
                {
                    BitmapInterpolationMode = CanvasZoom >= 1.0 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality,
                    EdgeMode = EdgeMode.Aliased
                }))
                {
                    _compositor.DrawTiles(context, canvasBounds, viewport);
                }
            }
        }
        else
        {
            // Empty document — draw paper color so the canvas isn't transparent
            var c = _document.PaperColor;
            if (c.A > 0)
            {
                var target = new Rect(Bounds.Size);
                using (context.PushClip(new RoundedRect(target)))
                    context.DrawRectangle(new SolidColorBrush(c), null, target);
            }
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

        if ((_isPointerOver || _isCursorPreviewLocked || _toolController.HasPendingOperation) && !IsPaintBlockedByLock)
        {
            var mode = ActiveCursorMode();
            var pos = _isCursorPreviewLocked ? _lockedPointerPos : _pointerPos;
            var t = Math.Max(0.5, 1.5 / CanvasZoom);
            bool isBrushLike = _toolController.ActiveTool is CompositeTool ct && ct.Input.HasBrushCursor;
            if (_forceBrushOutlineCursor || (isBrushLike && mode is BrushCursorMode.Outline or BrushCursorMode.DotAndOutline))
            {
                var r = ActiveToolCursorSize() * 0.5;
                context.DrawEllipse(null, new Pen(CursorOuterBrush, t * 3), pos, r, r);
                context.DrawEllipse(null, new Pen(CursorInnerBrush, t), pos, r, r);
            }

            if (!_forceBrushOutlineCursor && isBrushLike && mode == BrushCursorMode.BrushShape)
                DrawBrushShapeCursor(context, pos, t);

            if (!isBrushLike || mode is BrushCursorMode.Dot or BrushCursorMode.DotAndOutline)
            {
                var r = Math.Max(2.5 / CanvasZoom, t * 2);
                context.DrawEllipse(CursorOuterBrush, null, pos, r, r);
                context.DrawEllipse(CursorInnerBrush, null, pos, Math.Max(0.5 / CanvasZoom, r * 0.45), Math.Max(0.5 / CanvasZoom, r * 0.45));
            }

            if (_toolController.IsAlternateActive
                || _ctx.ActivePreset?.OutputProcess == Floss.App.OutputProcessType.Eyedropper)
            {
                var swatchR = 10.0 / CanvasZoom;
                var swatchPos = new Point(pos.X + swatchR * 1.6, pos.Y - swatchR * 1.6);
                var colorBrush = new SolidColorBrush(_paintColor);
                context.DrawEllipse(colorBrush, new Pen(CursorOuterBrush, t * 2), swatchPos, swatchR, swatchR);
                context.DrawEllipse(null, new Pen(CursorInnerBrush, t), swatchPos, swatchR, swatchR);
            }
        }

        DrawTelemetryOverlay(context);
    }

    private static readonly IBrush TelemetryOverlayBrush = new SolidColorBrush(Color.FromArgb(150, 18, 18, 18));
    private static readonly IBrush TelemetryTextBrush = new SolidColorBrush(Color.FromArgb(230, 235, 238, 242));

    private static void DrawTelemetryOverlay(DrawingContext context)
    {
        if (!App.Config.ShowRenderTelemetry) return;

        var s = RenderTelemetry.Snapshot;
        if (s.BrushMs <= 0 && s.CompositeMs <= 0) return;

        var text =
            $"brush {s.BrushMs:0.0}ms {s.BrushPath} dabs {s.BrushCachedDabs}/{s.BrushStamps}\n" +
            $"comp {s.CompositeMs:0.0}ms dirty {s.CompositeDirtyTiles} miss {s.CompositeMissingTiles} lod {s.Lod} q {s.PendingProjectionUpdates}";
        var ft = new FormattedText(text, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, Typeface.Default, 11, TelemetryTextBrush);
        var rect = new Rect(8, 8, ft.Width + 12, ft.Height + 8);
        context.FillRectangle(TelemetryOverlayBrush, rect);
        context.DrawText(ft, new Point(rect.X + 6, rect.Y + 4));
    }

    private void QueueDeferredTileRender()
    {
        if (_deferredTileRenderQueued) return;
        _deferredTileRenderQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _deferredTileRenderQueued = false;
            InvalidateVisual();
        }, DispatcherPriority.Background);
    }

    private BrushCursorMode ActiveCursorMode()
    {
        var cfg = App.Config;
        return _ctx.ActivePreset?.InputProcess switch
        {
            InputProcessType.Pen => cfg.PenCursorMode,
            InputProcessType.Eraser => cfg.EraserCursorMode,
            InputProcessType.Smudge => cfg.SmudgeCursorMode,
            _ => cfg.BrushCursorMode
        };
    }

    private double ActiveToolCursorSize()
        => _ctx.ActivePreset?.OutputProcess switch
        {
            OutputProcessType.Liquify => _ctx.ActivePreset.LiquifySize,
            _ => _brush.Size
        };

    private void DrawBrushShapeCursor(DrawingContext context, Point pos, double t)
    {
        var r = ActiveToolCursorSize() * 0.5;
        if (r < 0.5) return;
        var tip = _brush.Tip;

        SKBitmap? tipBitmap = null;
        if (tip is ImageBrushTip or NodeBrushTip { IsDirectImageSampler: true })
            tipBitmap = GetOrBuildCursorOutline(tip);

        var angle = ComputeLiveCursorAngle();
        _cursorAngle = angle;

        context.Custom(new BrushShapeInvertOp(
            pos, r, t, tip, tipBitmap,
            Math.Clamp((float)_brush.TipThickness, 0.01f, 1f),
            _brush.TipDirection,
            angle));
    }

    private SKBitmap GetOrBuildCursorOutline(IBrushTip tip)
    {
        if (ReferenceEquals(_cursorOutlineCache.Tip, tip) && _cursorOutlineCache.Outline != null)
            return _cursorOutlineCache.Outline;

        _cursorOutlineCache.Outline?.Dispose();
        // Always use 256 so GenerateMask hits the brush engine's existing cache entry
        // regardless of current cursor radius, avoiding a cache miss on every slider tick.
        var outline = OutlineBitmapFromMask(tip.GenerateMask(256, 1.0f));
        _cursorOutlineCache = (tip, outline);
        return outline;
    }

    private float _cursorAngle;

    private float ComputeLiveCursorAngle()
    {
        var directionContrib = _brush.BaseAngleSource switch
        {
            BrushDynamics.AngleSource.DirectionOfLine => DirectionDeg(),
            BrushDynamics.AngleSource.PenTilt => MathF.Atan2(_pointerTiltX, _pointerTiltY) * (180f / MathF.PI),
            BrushDynamics.AngleSource.PenTwist => _pointerTwist * (180f / MathF.PI),
            _ => 0f
        };

        var target = (float)_brush.Angle + directionContrib;
        _cursorAngle = LerpAngleDeg(_cursorAngle, target, CursorAngleLerp);
        return _cursorAngle;
    }

    private float DirectionDeg()
    {
        if (!_hasCursorDirectionPos)
        {
            _lastCursorDirectionPos = _pointerPos;
            _hasCursorDirectionPos = true;
            return _hasStableCursorDirection ? _stableCursorDirectionDeg : _cursorAngle;
        }

        var dx = _pointerPos.X - _lastCursorDirectionPos.X;
        var dy = _pointerPos.Y - _lastCursorDirectionPos.Y;
        var distanceSquared = dx * dx + dy * dy;
        var threshold = Math.Max(CursorDirectionDeadZone, 0.75 / Math.Max(CanvasZoom, 0.001));
        if (distanceSquared < threshold * threshold)
            return _hasStableCursorDirection ? _stableCursorDirectionDeg : _cursorAngle;

        _lastCursorDirectionPos = _pointerPos;
        _stableCursorDirectionDeg = MathF.Atan2((float)dy, (float)dx) * (180f / MathF.PI);
        _hasStableCursorDirection = true;
        return _stableCursorDirectionDeg;
    }

    private static float LerpAngleDeg(float a, float b, float t)
    {
        var delta = b - a;
        if (delta > 180f) delta -= 360f;
        else if (delta < -180f) delta += 360f;
        return a + delta * t;
    }

    private static unsafe SKBitmap OutlineBitmapFromMask(SKBitmap mask)
    {
        var w = mask.Width;
        var h = mask.Height;
        var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul));
        var src = (byte*)mask.GetPixels().ToPointer();
        var dst = (byte*)bmp.GetPixels().ToPointer();
        const byte threshold = 20;

        for (var y = 0; y < h; y++)
        {
            var sRow = src + y * mask.RowBytes;
            var dRow = dst + y * bmp.RowBytes;
            for (var x = 0; x < w; x++)
            {
                if (sRow[x] <= threshold)
                {
                    dRow[x * 4 + 3] = 0;
                    continue;
                }

                var isEdge = false;
                for (var dy = -1; dy <= 1 && !isEdge; dy++)
                {
                    var ny = y + dy;
                    if (ny < 0 || ny >= h) { isEdge = true; continue; }
                    var nsRow = src + ny * mask.RowBytes;
                    for (var dx = -1; dx <= 1 && !isEdge; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        var nx = x + dx;
                        if (nx < 0 || nx >= w) { isEdge = true; break; }
                        if (nsRow[nx] <= threshold) { isEdge = true; break; }
                    }
                }

                if (isEdge)
                {
                    dRow[x * 4 + 0] = 255;
                    dRow[x * 4 + 1] = 255;
                    dRow[x * 4 + 2] = 255;
                    dRow[x * 4 + 3] = 255;
                }
                else
                {
                    dRow[x * 4 + 3] = 0;
                }
            }
        }
        return bmp;
    }

    private sealed class BrushShapeInvertOp : ICustomDrawOperation
    {
        private readonly Point _pos;
        private readonly float _r;
        private readonly float _t;
        private readonly IBrushTip _tip;
        private readonly SKBitmap? _bitmap;
        private readonly float _thickness;
        private readonly BrushTipDirection _direction;
        private readonly float _angle;

        public Rect Bounds { get; }

        public BrushShapeInvertOp(
            Point pos,
            double r,
            double t,
            IBrushTip tip,
            SKBitmap? bitmap,
            float thickness,
            BrushTipDirection direction,
            float angle)
        {
            _pos = pos;
            _r = (float)r;
            _t = (float)t;
            _tip = tip;
            _bitmap = bitmap;
            _thickness = thickness;
            _direction = direction;
            _angle = angle;
            var pad = r + t * 2;
            Bounds = new Rect(pos.X - pad, pos.Y - pad, pad * 2, pad * 2);
        }

        public bool HitTest(Point p) => false;
        public bool Equals(ICustomDrawOperation? other) => false;
        public void Dispose() { }

        public void Render(ImmediateDrawingContext context)
        {
            var lease = context.TryGetFeature<Avalonia.Skia.ISkiaSharpApiLeaseFeature>()?.Lease();
            if (lease == null) return;
            using (lease)
            {
                if (_r < 0.5f) return;
                var canvas = lease.SkCanvas;
                using var paint = new SKPaint
                {
                    Color = SKColors.White,
                    BlendMode = SKBlendMode.Difference,
                    IsAntialias = true,
                    Style = SKPaintStyle.Stroke,
                    StrokeWidth = _t
                };

                var cx = (float)_pos.X;
                var cy = (float)_pos.Y;
                var scaleX = _direction == BrushTipDirection.Vertical ? _thickness : 1f;
                var scaleY = _direction == BrushTipDirection.Horizontal ? _thickness : 1f;

                canvas.Save();
                canvas.Translate(cx, cy);
                if (Math.Abs(_angle) > 0.001f)
                    canvas.RotateDegrees(_angle);
                canvas.Scale(scaleX, scaleY);

                if (_bitmap != null)
                {
                    paint.Style = SKPaintStyle.Fill;
                    canvas.DrawBitmap(_bitmap, new SKRect(-_r, -_r, _r, _r), paint);
                    canvas.Restore();
                    return;
                }

                switch (_tip)
                {
                    case ProceduralBrushTip { Shape: BrushTipShape.Ellipse } proc:
                        {
                            var asp = Math.Clamp(proc.AspectRatio, 0.05f, 20f);
                            var rx = asp >= 1 ? _r : _r * asp;
                            var ry = asp >= 1 ? _r / asp : _r;
                            canvas.DrawOval(0, 0, rx, ry, paint);
                            break;
                        }
                    case ProceduralBrushTip { Shape: BrushTipShape.Flat or BrushTipShape.Rectangle } proc:
                        {
                            var asp = proc.Shape == BrushTipShape.Flat ? 4f : Math.Clamp(proc.AspectRatio, 0.05f, 20f);
                            var rw = asp >= 1 ? _r : _r * asp;
                            var rh = asp >= 1 ? _r / asp : _r;
                            canvas.DrawRect(-rw, -rh, rw * 2, rh * 2, paint);
                            break;
                        }
                    default:
                        canvas.DrawCircle(0, 0, _r, paint);
                        break;
                }
                canvas.Restore();
            }
        }
    }

    public bool IsLayerPickDrag => _isLayerPickDrag;
    public void StartLayerPickDrag(Point pos)
    {
        _isLayerPickDrag = true;
        _layerPickAnchor = pos;
        _layerPickCurrent = pos;
        InvalidateVisual();
    }

    public void UpdateLayerPickDrag(Point pos)
    {
        if (!_isLayerPickDrag) return;
        _layerPickCurrent = pos;
        InvalidateVisual();
    }

    public void EndLayerPickDrag(Point pos)
    {
        if (!_isLayerPickDrag) return;
        _isLayerPickDrag = false;
        InvalidateVisual();

        var dx = pos.X - _layerPickAnchor.X;
        var dy = pos.Y - _layerPickAnchor.Y;
        var dragDist = Math.Sqrt(dx * dx + dy * dy);

        if (dragDist < 4)
        {
            TryPickLayerAtPoint((int)_layerPickAnchor.X, (int)_layerPickAnchor.Y);
        }
        else
        {
            var rx = (int)Math.Min(_layerPickAnchor.X, pos.X);
            var ry = (int)Math.Min(_layerPickAnchor.Y, pos.Y);
            var rw = (int)Math.Abs(pos.X - _layerPickAnchor.X) + 1;
            var rh = (int)Math.Abs(pos.Y - _layerPickAnchor.Y) + 1;
            var found = FindLayersInRect(rx, ry, rw, rh);
            if (found.Count > 0)
                LayersFoundByRect?.Invoke(found);
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);

        // Ctrl+Shift+Left: start a layer-find drag.
        if (IsPaintInput(point) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            StartLayerPickDrag(point.Position);
            e.Pointer.Capture(this);
            e.Handled = true;
            return;
        }

        if (PaintInputSuspended && !_toolController.IsAlternateActive) return;
        if (IsPaintBlockedByLock) return;
        if (!IsPaintInput(point)) return;

        // Tool dispatch handled by workspace viewport — canvas only tracks cursor.
        _activePointerId = point.Pointer.Id;
        InvalidateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _isPointerOver = true;
        var pt = e.GetCurrentPoint(this);
        _pointerPos = pt.Position;
        _prevPointerPos = pt.Position;
        _lastCursorDirectionPos = pt.Position;
        _hasCursorDirectionPos = true;
        _hasStableCursorDirection = false;
        _pointerTiltX = (float)pt.Properties.XTilt;
        _pointerTiltY = (float)pt.Properties.YTilt;
        _pointerTwist = (float)pt.Properties.Twist;
        InvalidateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var point = e.GetCurrentPoint(this);
        if (!_isCursorPreviewLocked)
        {
            _prevPointerPos = _pointerPos;
            _pointerPos = point.Position;
        }
        _pointerTiltX = (float)point.Properties.XTilt;
        _pointerTiltY = (float)point.Properties.YTilt;
        _pointerTwist = (float)point.Properties.Twist;

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

        if (PaintInputSuspended && !_toolController.IsAlternateActive)
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

        // Keep tool's hover position current even when no stroke is active,
        // so the shift-line preview tracks the cursor through panning.
        if (_activePointerId < 0)
            HandlePointerInput(ToolInputEventKind.Move, point);

        InvalidateVisual();
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
                TryPickLayerAtPoint((int)_layerPickAnchor.X, (int)_layerPickAnchor.Y);
            }
            else
            {
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

        _activePointerId = -1;
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

        var paper = _document.PaperLayer;
        var paperUint = paper is { IsVisible: true } ? ColorToBgraUint(_document.PaperColor) : 0u;
        return _compositor.SampleCompositePixel(_document.Layers, _document.Width, _document.Height, x, y, paperUint);
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
            return properties.Pressure > 0;

        return point.Pointer.Type switch
        {
            PointerType.Pen => properties.Pressure > 0,
            PointerType.Touch => properties.IsLeftButtonPressed || properties.Pressure > 0,
            PointerType.Mouse => properties.IsLeftButtonPressed || properties.Pressure > 0,
            _ => false
        };
    }

    private static uint ColorToBgraUint(Avalonia.Media.Color c)
        => (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));
}
