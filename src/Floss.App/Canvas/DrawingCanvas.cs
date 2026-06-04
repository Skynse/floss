using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Floss.App.Brushes;
using Floss.App.Document;
using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Tools;
using Floss.App.Canvas.Compositing;
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
    private readonly SolidColorBrush _paintColorBrush = new(Color.Parse("#111111"));
    private SolidColorBrush? _paperColorBrush;
    private long _activePointerId = -1;
    private Point _pointerPos;
    private Point _viewportPointerPos;
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
    private bool _hasViewportPointer;
    private bool _isCursorPreviewLocked;
    private bool _forceBrushOutlineCursor;
    private Point? _brushResizeEdgeCanvasPoint;
    private bool _deferredTileRenderQueued;
    private int _renderLod = -1;
    private double _lastRenderZoom = double.NaN;
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

    private static readonly WriteableBitmap _checkerboardBitmap;

    private double _checkerBrushZoom = double.NaN;
    private ImageBrush? _checkerBrush;

    static DrawingCanvas()
    {
        const int tileSize = 64;
        _checkerboardBitmap = new WriteableBitmap(
            new PixelSize(tileSize, tileSize),
            new Vector(96, 96),
            PixelFormat.Bgra8888,
            AlphaFormat.Unpremul);

        using var fb = _checkerboardBitmap.Lock();
        var info = new SKImageInfo(tileSize, tileSize, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        using var surface = SKSurface.Create(info, fb.Address, fb.RowBytes);
        var canvas = surface.Canvas;

        var dark = new SKColor(0x55, 0x55, 0x55);
        var light = new SKColor(0xAA, 0xAA, 0xAA);
        var h = tileSize / 2;
        using var darkPaint = new SKPaint { Color = dark, IsAntialias = false };
        using var lightPaint = new SKPaint { Color = light, IsAntialias = false };

        canvas.DrawRect(0, 0, h, h, darkPaint);
        canvas.DrawRect(h, h, h, h, darkPaint);
        canvas.DrawRect(h, 0, h, h, lightPaint);
        canvas.DrawRect(0, h, h, h, lightPaint);

        surface.Flush();
    }

    private ImageBrush GetCheckerBrush(double zoom)
    {
        if (_checkerBrush != null && Math.Abs(_checkerBrushZoom - zoom) < 1e-9)
            return _checkerBrush;

        // Keep checker squares at ~8 screen pixels by scaling the tile size
        // inversely with zoom. Without this, tiles are 8*zoom px — tiny at low
        // zoom (aliasing) and huge at high zoom.
        var tilePx = Math.Max(1.0, 16.0 / zoom);
        _checkerBrush = new ImageBrush(_checkerboardBitmap)
        {
            TileMode = TileMode.Tile,
            DestinationRect = new RelativeRect(0, 0, tilePx, tilePx, RelativeUnit.Absolute),
            SourceRect = new RelativeRect(0, 0, 1, 1, RelativeUnit.Relative)
        };
        _checkerBrushZoom = zoom;
        return _checkerBrush;
    }

    public BrushEngine BrushEngine { get; }

    internal event Action? CursorPreviewChanged;

    internal bool ShouldShowToolCursor =>
        IsBrushResizePreviewActive
        || ((_isPointerOver || _isCursorPreviewLocked || _toolController.HasPendingOperation) && !IsPaintBlockedByLock);

    internal bool IsCursorPreviewLocked => _isCursorPreviewLocked;

    internal bool IsBrushResizePreviewActive => _brushResizeEdgeCanvasPoint.HasValue;

    internal Point GetToolCursorViewportPosition(Visual viewport)
    {
        if (!_isCursorPreviewLocked && _hasViewportPointer)
            return _viewportPointerPos;

        var canvasPos = _isCursorPreviewLocked ? _lockedPointerPos : _pointerPos;
        var matrix = this.TransformToVisual(viewport);
        if (matrix.HasValue)
            return matrix.Value.Transform(canvasPos);

        var translated = this.TranslatePoint(canvasPos, viewport);
        return translated ?? _viewportPointerPos;
    }

    public DrawingCanvas()
    {
        _document.DirtyStateChanged += (_, _) => DirtyStateChanged?.Invoke(this, EventArgs.Empty);
        Focusable = true;
        ClipToBounds = false;
        Cursor = CursorNone;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);

        BrushEngine = new BrushEngine();
        _compositor = new LayerCompositor();
        _projectionScheduler = new ProjectionUpdateScheduler(InvalidateVisual);

        _ctx = new ToolContext(_document)
        {
            Brush = _brush,
            PaintColor = _paintColor,
            InvalidateRender = InvalidateVisual,
            InvalidateSelectionOverlay = InvalidateSelectionOutline,
            TransformEditChanged = () => TransformEditChanged?.Invoke(this, EventArgs.Empty),
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
            var viewport = ComputeVisibleViewport();
            _projectionScheduler.Invalidate(
                e.DirtyRegion, _document.Layers, e.LayerIndex, e.MetadataOnly, viewport);
            if (!e.MetadataOnly)
                StatsChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.HistoryChanged += (_, _) => HistoryChanged?.Invoke(this, EventArgs.Empty);
        _document.SelectionChanged += (_, _) => NotifySelectionChanged();
        _document.LayersChanged += (_, _) =>
        {
            LayersChanged?.Invoke(this, EventArgs.Empty);
        };
        _document.LayerRemoved += (_, layer) => _compositor.RemoveGroupCache(layer);
        _document.LayerMetadataChanged += (_, e) => LayerMetadataChanged?.Invoke(this, e);
        _document.StrokeSuspendBegan += (_, e) => _compositor.BeginStrokeSuspend(e.Region, e.LayerIndex);
        _document.StrokeSuspendExtended += (_, r) => _compositor.ExtendStrokeSuspend(r);
        _document.StrokeSuspendEnded += (_, _) => _compositor.EndStrokeSuspend();
    }

    public void InvalidateCompositor() => _projectionScheduler.Invalidate(null);
    private void InvalidateCompositor(PixelRegion region) =>
        _projectionScheduler.Invalidate(region.IsEmpty ? null : region);

    /// <summary>
    /// Call after replacing the document (open/import). Clears display LOD state so the
    /// next paint bootstraps compositor tiles at the zoom-appropriate LOD.
    /// </summary>
    public void ResetDisplayAfterDocumentLoad()
    {
        _renderLod = -1;
        _lastRenderZoom = double.NaN;
        InvalidateCompositor();
    }

    /// <summary>
    /// Synchronously fill viewport compositor tiles (used after open/import).
    /// Uses the LOD for the current zoom — not forced full resolution.
    /// </summary>
    public void EnsureDisplayCompositeSync()
    {
        if (!HasAnyLayerContent(_document.Layers)) return;

        var paperUint = _document.IsPaperBackgroundVisible ? ColorToBgraUint(_document.PaperColor) : 0u;
        var viewport = ComputeVisibleViewport();
        var zoom = CanvasZoom > 0 ? CanvasZoom : 1.0;

        using (_document.RenderLock.Read())
        {
            for (var pass = 0; pass < 64; pass++)
            {
                if (!_compositor.Composite(_document.Layers, _document.Width, _document.Height,
                        paperUint, viewport, zoom))
                    break;
                if (_compositor.PendingDirtyTileCount == 0)
                    break;
            }
        }

        _renderLod = _compositor.LastLod;
        _projectionScheduler.ApplyPending(_compositor);
        _compositor.DrainDisposalQueue();
        InvalidateVisual();
    }

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
    public event EventHandler? SelectionOutlineChanged;
    public event EventHandler? TransformEditChanged;

    public TransformEditSnapshot? TransformEdit =>
        _toolController.ActiveTool is TransformTool tt ? tt.EditSnapshot : null;

    public void UpdateTransformEdit(TransformEditSnapshot edit) => _transformTool.ApplyEdit(edit);

    public void ResetTransformEdit() => _transformTool.ResetEdit();

    public void FlipTransformHorizontal() => _transformTool.FlipHorizontal();

    public void FlipTransformVertical() => _transformTool.FlipVertical();

    public void EndTransformDragIfActive() => _transformTool.EndDrag(_ctx);

    internal void InvalidateSelectionOutline() => SelectionOutlineChanged?.Invoke(this, EventArgs.Empty);

    public int ActiveSampleCount => _toolController.ActiveTool.HasPendingOperation ? 1 : 0;
    public int CommittedStrokeCount => _document.CommittedStrokeCount;
    public bool IsStrokeProcessing => IsStrokeOutputPending();
    public bool CanUndo => !IsStrokeOutputPending() && _document.CanUndo;
    public bool CanRedo => !IsStrokeOutputPending() && _document.CanRedo;
    public bool CanDeleteLayer(IReadOnlyList<int>? indices = null) =>
        !IsStrokeOutputPending() && (indices is { Count: > 0 }
            ? _document.CanDeleteLayers(indices)
            : _document.CanDeleteLayer);

    public void DeleteLayer(IReadOnlyList<int>? indices = null)
    {
        if (IsStrokeOutputPending()) return;
        if (indices is { Count: > 0 })
            _document.DeleteLayers(indices);
        else
            _document.DeleteActiveLayer();
    }
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

        var sampleInfo = GetCanvasInputSampleInfo(point, phase);
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
            NotifyCursorPreviewChanged();
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
            Stopwatch.GetTimestamp() * 1_000_000 / Stopwatch.Frequency,
            point.Pointer.Id,
            sampleInfo.Source,
            phase);
        _toolController.Dispatch(new ToolInputEvent(kind, sample));
    }

    public void TrackViewportPointer(PointerPoint viewportPoint, PointerPoint canvasPoint)
    {
        _viewportPointerPos = viewportPoint.Position;
        _hasViewportPointer = true;
        if (!_isCursorPreviewLocked)
        {
            _prevPointerPos = _pointerPos;
            _pointerPos = canvasPoint.Position;
        }

        _isPointerOver = true;
        var props = viewportPoint.Properties;
        _pointerTiltX = (float)props.XTilt;
        _pointerTiltY = (float)props.YTilt;
        _pointerTwist = (float)props.Twist;
        Cursor = CursorNone;
        NotifyCursorPreviewChanged();
    }

    public void ClearViewportPointer()
    {
        if (_toolController.HasPendingOperation || _isCursorPreviewLocked)
            return;

        _isPointerOver = false;
        NotifyCursorPreviewChanged();
    }

    private void NotifyCursorPreviewChanged()
    {
        CursorPreviewChanged?.Invoke();
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
        var sampleInfo = GetCanvasInputSampleInfo(point, phase);

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

    private static (CanvasInputSource Source, double Pressure) GetCanvasInputSampleInfo(PointerPoint point, CanvasInputPhase phase)
    {
        var (source, pressure) = TabletInput.GetSampleInfo(point, phase);
        return (source, App.PenPressure.Evaluate(pressure));
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
        InvalidateSelectionOutline();
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
        ApplyBrushToStrokeInputs(_brush);
        InvalidateVisual();
    }

    public void SaveBrushEnginePreset() => _toolController.SaveEnginePreset();

    internal void SyncBrushFromContext(BrushPreset brush)
    {
        _brush = brush with { Color = _paintColor };
        _ctx.Brush = _brush;
        ApplyBrushToStrokeInputs(_brush);
        InvalidateVisual();
    }

    private void ApplyBrushToStrokeInputs(BrushPreset brush)
    {
        if (_brushTool.Input is BrushStrokeInputProcess brushInput)
        {
            brushInput.Stabilization = brush.Smoothing;
            brushInput.SpeedAdaptiveStabilizer = brush.SpeedAdaptiveStabilizer;
        }
        if (_eraserTool.Input is BrushStrokeInputProcess eraserInput)
        {
            eraserInput.Stabilization = brush.Smoothing;
            eraserInput.SpeedAdaptiveStabilizer = brush.SpeedAdaptiveStabilizer;
        }
    }

    public void SetPaintColor(Color color)
    {
        _paintColor = color;
        _paintColorBrush.Color = color;
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
        _brush = _brush with { Size = Math.Clamp(size, BrushSizeLimits.MinDiameterPx, BrushSizeLimits.AbsoluteHardCapPx) };
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
        ApplyBrushToStrokeInputs(_brush);
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
        NotifyCursorPreviewChanged();
    }

    public void SetBrushResizeEdgePreview(Point edgeCanvasPoint)
    {
        _brushResizeEdgeCanvasPoint = edgeCanvasPoint;
        NotifyCursorPreviewChanged();
    }

    public void ClearBrushResizePreview()
    {
        if (!_brushResizeEdgeCanvasPoint.HasValue) return;
        _brushResizeEdgeCanvasPoint = null;
        NotifyCursorPreviewChanged();
    }

    public void UnlockCursorPreview()
    {
        if (!_isCursorPreviewLocked && !_brushResizeEdgeCanvasPoint.HasValue) return;
        _isCursorPreviewLocked = false;
        _forceBrushOutlineCursor = false;
        _brushResizeEdgeCanvasPoint = null;
        NotifyCursorPreviewChanged();
    }

    /// <summary>
    /// Clears per-canvas input/render state that can get stuck on one document tab
    /// while other tabs (fresh canvases) keep working.
    /// </summary>
    public void RecoverInputState()
    {
        UnlockCursorPreview();
        PaintInputSuspended = false;
        SetAlternateActive(false);
        SetToolAuxMode(ToolAuxOperationType.None);

        if (_toolController.ActiveTool is TransformTool transform && transform.HasPendingOperation)
            transform.EndDrag(_ctx);
        else
            _toolController.Cancel();
        CancelDirectDrawOutput(_brushTool);
        CancelDirectDrawOutput(_eraserTool);
        if (_toolController.ActiveTool is CompositeTool active
            && !ReferenceEquals(active, _brushTool)
            && !ReferenceEquals(active, _eraserTool))
        {
            CancelDirectDrawOutput(active);
        }

        BrushEngine.EndStroke();
        _compositor.ResetStrokeSuspend();
        InvalidateCompositor();
        InvalidateVisual();
    }

    private static void CancelDirectDrawOutput(ITool tool)
    {
        if (tool is CompositeTool ct && ct.Output is DirectDrawOutput output)
            output.Cancel();
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
            TransformEditChanged?.Invoke(this, EventArgs.Empty);
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
        TransformEditChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteSelectionTransform()
    {
        _transformTool.Delete(_ctx);
        InvalidateVisual();
    }

    public void MergeSelectedLayers(IReadOnlyList<int> indices)
    {
        if (IsStrokeOutputPending()) return;
        _document.MergeSelectedLayers(indices, _compositor);
    }

    public void FlattenGroup(int groupIndex)
    {
        if (IsStrokeOutputPending()) return;
        _document.FlattenGroup(groupIndex, _compositor);
    }

    public void ApplyFilter(IReadOnlyList<int> layerIndices, Action<DrawingLayer> apply)
    {
        if (IsStrokeOutputPending()) return;
        _document.ApplyFilterToLayers(layerIndices, apply);
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

        const int ts = TiledPixelBuffer.TileSize;
        var firstTileX = layerBounds.X / ts;
        var firstTileY = layerBounds.Y / ts;
        var lastTileX = (layerBounds.Right - 1) / ts;
        var lastTileY = (layerBounds.Bottom - 1) / ts;

        for (var ty = firstTileY; ty <= lastTileY; ty++)
        {
            for (var tx = firstTileX; tx <= lastTileX; tx++)
            {
                var tile = layer.Pixels.GetTileOrNull(tx, ty);
                if (tile == null) continue;

                var tx0 = tx * ts;
                var ty0 = ty * ts;
                var clipLeft = Math.Max(layerBounds.X, tx0);
                var clipTop = Math.Max(layerBounds.Y, ty0);
                var clipRight = Math.Min(layerBounds.Right, tx0 + ts);
                var clipBottom = Math.Min(layerBounds.Bottom, ty0 + ts);

                for (var ly = clipTop; ly < clipBottom; ly++)
                {
                    var docY = ly + layer.OffsetY;
                    var tileRow = (ly - ty0) * ts * 4;
                    for (var lx = clipLeft; lx < clipRight; lx++)
                    {
                        var docX = lx + layer.OffsetX;
                        if (!_ctx.Selection.IsSelected(docX, docY)) continue;
                        var off = tileRow + (lx - tx0) * 4;
                        tile[off] = tile[off + 1] = tile[off + 2] = tile[off + 3] = 0;
                    }
                }
            }
        }

        layer.Pixels.PruneRegion(layerBounds);
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
                if (AlphaLockPixelOps.TryWriteColor(layer.Pixels, layX, layY, c.B, c.G, c.R, c.A, layer.IsAlphaLocked))
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
        var srcW = _clipboardW;
        var srcH = _clipboardH;

        int docMaxDim = Math.Max(_document.Width, _document.Height);
        float fitTarget = docMaxDim * 0.5f;
        float scale = Math.Min(1f, fitTarget / Math.Max(srcW, srcH));

        int w = srcW, h = srcH;
        byte[] scaledPixels;
        if (scale < 1f)
        {
            w = Math.Max(1, (int)(srcW * scale));
            h = Math.Max(1, (int)(srcH * scale));
            scaledPixels = new byte[w * h * 4];
            for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                int sx = (int)(x / scale);
                int sy = (int)(y / scale);
                int si = (sy * srcW + sx) * 4;
                int di = (y * w + x) * 4;
                scaledPixels[di] = pixels[si];
                scaledPixels[di + 1] = pixels[si + 1];
                scaledPixels[di + 2] = pixels[si + 2];
                scaledPixels[di + 3] = pixels[si + 3];
            }
        }
        else
        {
            scaledPixels = pixels;
        }

        _document.BeginDocumentMutation();
        var layer = new DrawingLayer("Pasted", _document.Width, _document.Height);
        layer.Pixels.CopyFromBgra(scaledPixels, w, h);
        var insertIdx = Math.Min(_document.ActiveLayerIndex + 1, _document.Layers.Count);
        _document.InsertAndSelectLayer(layer, insertIdx);
        InvalidateVisual();
        BeginSelectionTransform();
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

    internal bool PasteSKBitmap(SKBitmap skBitmap, string name)
    {
        int docMaxDim = Math.Max(_document.Width, _document.Height);
        float fitTarget = docMaxDim * 0.5f;
        float scale = Math.Min(1f, fitTarget / Math.Max(skBitmap.Width, skBitmap.Height));

        SKBitmap scaled;
        if (scale < 1f)
        {
            int sw = Math.Max(1, (int)(skBitmap.Width * scale));
            int sh = Math.Max(1, (int)(skBitmap.Height * scale));
            scaled = skBitmap.Resize(new SKImageInfo(sw, sh), new SKSamplingOptions(SKFilterMode.Linear));
        }
        else
        {
            scaled = skBitmap;
        }

        var w = scaled.Width;
        var h = scaled.Height;

        var pixels = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var c = scaled.GetPixel(x, y);
                var offset = (y * w + x) * 4;
                pixels[offset] = c.Blue;
                pixels[offset + 1] = c.Green;
                pixels[offset + 2] = c.Red;
                pixels[offset + 3] = c.Alpha;
            }
        }

        if (scaled != skBitmap)
            scaled.Dispose();

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
        InvalidateSelectionOutline();
    }

    public void Clear(bool pushHistory = true)
    {
        if (!_document.CanPaintActiveLayer) return;
        _document.ClearActiveLayer(pushHistory);
    }
    public void Undo()
    {
        EnsureStrokeOutputIdle();
        if (_toolController.HasPendingOperation && !(_toolController.ActiveTool is TransformTool))
            _toolController.Cancel();
        EnsureStrokeOutputIdle();
        _document.Undo();
        InvalidateAfterHistoryReplay();
        InvalidateVisual();
    }

    public void Redo()
    {
        EnsureStrokeOutputIdle();
        if (_toolController.HasPendingOperation && !(_toolController.ActiveTool is TransformTool))
            _toolController.Cancel();
        EnsureStrokeOutputIdle();
        _document.Redo();
        InvalidateAfterHistoryReplay();
        InvalidateVisual();
    }

    private void InvalidateAfterHistoryReplay()
    {
        if (!_document.LastHistoryAffectsVisual)
            return;
        InvalidateCompositor(_document.LastHistoryVisualDirtyRegion);
    }

    private bool IsStrokeOutputPending()
        => DirectDrawHasPendingWork(_brushTool)
           || DirectDrawHasPendingWork(_eraserTool)
           || (_toolController.ActiveTool is CompositeTool active
               && !ReferenceEquals(active, _brushTool)
               && !ReferenceEquals(active, _eraserTool)
               && DirectDrawHasPendingWork(active));

    internal bool IsActiveSelectionGesture()
        => _toolController.ActiveTool is CompositeTool ct
           && ct.Output is SelectionAreaOutput
           && ct.Input.IsActive;

    internal bool ShouldHideCommittedSelectionDuringGesture()
        => IsActiveSelectionGesture() && GetEffectiveSelectionOp() == SelectOp.Replace;

    internal SelectOp GetEffectiveSelectionOp()
    {
        if (_ctx.ActiveSelectionOp is { } locked)
            return locked;
        if (_toolController.ActiveTool is CompositeTool ct && ct.Output is SelectionAreaOutput sao)
            return SelectOpHelper.ResolveForSelection(sao.Operation, _ctx);
        return SelectOp.Replace;
    }

    private static bool DirectDrawHasPendingWork(ITool tool)
        => tool is CompositeTool ct && ct.Output is DirectDrawOutput dd && dd.HasPendingWork;

    private void EnsureStrokeOutputIdle()
    {
        if (_brushTool.Output is DirectDrawOutput brushDraw)
            brushDraw.WaitUntilIdle();
        if (_eraserTool.Output is DirectDrawOutput eraserDraw)
            eraserDraw.WaitUntilIdle();
        if (_toolController.ActiveTool is CompositeTool active
            && active.Output is DirectDrawOutput activeDraw
            && !ReferenceEquals(active, _brushTool)
            && !ReferenceEquals(active, _eraserTool))
        {
            activeDraw.WaitUntilIdle();
        }
    }
    public void AddLayer() { if (!IsStrokeOutputPending()) _document.AddLayer(); }
    public void AddGroupLayer() { if (!IsStrokeOutputPending()) _document.AddGroupLayer(); }
    public void AddBackgroundLayer() { if (!IsStrokeOutputPending()) _document.AddBackgroundLayer(); }
    public void AddAdjustmentLayer(AdjustmentKind kind) { if (!IsStrokeOutputPending()) _document.AddAdjustmentLayer(kind); }
    public void SetLayerAdjustmentParams(int layerIndex, AdjustmentLayerData newParams) { if (!IsStrokeOutputPending()) _document.SetLayerAdjustmentParams(layerIndex, newParams); }
    public void PreviewLayerAdjustmentParams(int layerIndex, AdjustmentLayerData newParams) { if (!IsStrokeOutputPending()) _document.PreviewLayerAdjustmentParams(layerIndex, newParams); }
    public void GroupSelectedLayers(IReadOnlyList<int> indices) { if (!IsStrokeOutputPending()) _document.GroupSelectedLayers(indices); }
    public void DuplicateLayer() { if (!IsStrokeOutputPending()) _document.DuplicateActiveLayer(); }
    public void SelectLayer(int index) { if (!IsStrokeOutputPending()) _document.SelectLayer(index); }
    public void ToggleLayerVisibility(int index) { if (!IsStrokeOutputPending()) _document.ToggleLayerVisibility(index); }
    public void ToggleLayerLock(int index) { if (!IsStrokeOutputPending()) _document.ToggleLayerLock(index); }
    public void ToggleLayerAlphaLock(int index) { if (!IsStrokeOutputPending()) _document.ToggleLayerAlphaLock(index); }
    public void ToggleLayerReference(int index) { if (!IsStrokeOutputPending()) _document.ToggleLayerReference(index); }
    public void ToggleLayerClipping(int index) { if (!IsStrokeOutputPending()) _document.ToggleLayerClipping(index); }
    public void CreateLayerMask(int index) { if (!IsStrokeOutputPending()) _document.CreateLayerMask(index); }
    public void ToggleLayerMask(int index) { if (!IsStrokeOutputPending()) _document.ToggleLayerMask(index); }
    public void ToggleLayerMaskEditing(int index) { if (!IsStrokeOutputPending()) _document.ToggleLayerMaskEditing(index); }
    public void SetLayerMaskEditing(int index, bool editing) { if (!IsStrokeOutputPending()) _document.SetLayerMaskEditing(index, editing); }
    public void SetLayerContentEditing(int index) { if (!IsStrokeOutputPending()) _document.SetLayerContentEditing(index); }
    public void DeleteLayerMask(int index) { if (!IsStrokeOutputPending()) _document.DeleteLayerMask(index); }
    public void ApplyLayerMask(int index) { if (!IsStrokeOutputPending()) _document.ApplyLayerMask(index); }
    public void ToggleLayerOpen(int index) { if (!IsStrokeOutputPending()) _document.ToggleLayerOpen(index); }
    public bool CanMoveLayer(int sourceIndex, int targetIndex, LayerDropPlacement placement) => !IsStrokeOutputPending() && _document.CanMoveLayer(sourceIndex, targetIndex, placement);
    public void MoveLayer(int sourceIndex, int targetIndex, LayerDropPlacement placement) { if (!IsStrokeOutputPending()) _document.MoveLayer(sourceIndex, targetIndex, placement); }
    public void MoveActiveLayer(int delta) { if (!IsStrokeOutputPending()) _document.MoveActiveLayer(delta); }
    public void SetActiveLayerOpacity(double opacity) { if (!IsStrokeOutputPending()) _document.SetActiveLayerOpacity(opacity); }
    public void BeginActiveLayerOpacityScrub() { if (!IsStrokeOutputPending()) _document.BeginActiveLayerOpacityScrub(); }
    public void PreviewActiveLayerOpacity(double opacity) { if (!IsStrokeOutputPending()) _document.PreviewActiveLayerOpacity(opacity); }
    public void CommitActiveLayerOpacityScrub() { if (!IsStrokeOutputPending()) _document.CommitActiveLayerOpacityScrub(); }
    public void SetActiveLayerBlendMode(BlendMode blendMode) { if (!IsStrokeOutputPending()) _document.SetActiveLayerBlendMode(blendMode); }
    public void SetActiveLayerName(string name) { if (!IsStrokeOutputPending()) _document.SetActiveLayerName(name); }
    public void SetActiveLayerColor(Avalonia.Media.Color? color) { if (!IsStrokeOutputPending()) _document.SetActiveLayerColor(color); }

    public void CopyLayer(int index)
    {
        if (index < 0 || index >= _document.Layers.Count) return;
        _layerClipboard = DrawingDocument.CloneLayerTree(_document.Layers[index]);
    }

    public bool CanPasteLayer => _layerClipboard != null && !IsStrokeOutputPending();

    public void PasteLayer(int targetIndex)
    {
        if (IsStrokeOutputPending()) return;
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

        var canvasBounds = new Rect(Bounds.Size);
        context.FillRectangle(CheckerboardOverlay.BackgroundBrush, canvasBounds);

        // Skip compositing on empty-canvas documents — avoids allocating a
        // full-canvas WriteableBitmap (900MB+ for 15k²) when nothing is drawn.
        var viewport = ComputeVisibleViewport();
        _projectionScheduler.ApplyPending(_compositor);
        _compositor.DrainDisposalQueue();
        {
            var paper = _document.PaperLayer;
            bool hasSolidPaper = _document.IsPaperBackgroundVisible && _document.PaperColor.A == 255;
            if (hasSolidPaper)
            {
                if (_paperColorBrush == null || _paperColorBrush.Color != _document.PaperColor)
                    _paperColorBrush = new SolidColorBrush(_document.PaperColor);
                context.FillRectangle(_paperColorBrush, canvasBounds);
            }
            else
            {
                using (context.PushRenderOptions(new RenderOptions
                {
                    BitmapInterpolationMode = BitmapInterpolationMode.None
                }))
                    context.FillRectangle(GetCheckerBrush(CanvasZoom), canvasBounds);
            }
        }
        if (HasAnyLayerContent(_document.Layers))
        {
            uint paperUint = _document.IsPaperBackgroundVisible ? ColorToBgraUint(_document.PaperColor) : 0u;

            // Composite runs on a background thread. UI thread draws the cached
            // tiles (kept visible until recomposited — no tile drops on partial
            // invalidation). First paint is synchronous so the canvas isn't blank.
            // Keep viewport navigation responsive: zoom/LOD changes draw the
            // previous cached tiles immediately and refresh the new LOD on the
            // background compositor. First paint still runs synchronously so a
            // freshly opened canvas is not blank.
            var zoom = CanvasZoom;
            var nextLod = _compositor.SelectLod(_document.Width, _document.Height, zoom);
            var lodTransition = _renderLod >= 0 && nextLod != _renderLod;
            var zoomTick = !double.IsNaN(_lastRenderZoom) && Math.Abs(_lastRenderZoom - zoom) > 1e-12;
            _lastRenderZoom = zoom;

            bool needSync = !_compositor.HasAnyTiles && !_compositor.IsCompositeActive;

            // Drawpile: UI draws tiles directly from cache (no all-or-nothing
            // frame), background threads composite continuously. The sync pass
            // here only fires on first paint — after that, DrawTiles uses LOD
            // fallback for any not-yet-ready tiles, and the background pass
            // catches up without blocking the UI thread.
            if (needSync && !_compositor.IsCompositeActive)
            {
                using (_document.RenderLock.Read())
                {
                    var passes = 0;
                    while (passes < 32)
                    {
                        if (!_compositor.Composite(_document.Layers, _document.Width, _document.Height,
                                paperUint, viewport, zoom))
                            break;
                        passes++;
                    }
                }
                _renderLod = _compositor.LastLod;
                if (_compositor.PendingDirtyTileCount > 0)
                    QueueDeferredTileRender();
            }
            else
            {
                ScheduleBackgroundComposite(paperUint, viewport, zoom);
            }

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
        else
        {
            // Empty document — draw paper color only when the paper layer is visible
            var c = _document.PaperColor;
            if (_document.IsPaperBackgroundVisible)
            {
                var target = new Rect(Bounds.Size);
                using (context.PushClip(new RoundedRect(target)))
                    context.DrawRectangle(new SolidColorBrush(c), null, target);
            }
        }

        _transformTool.ViewportFlipX = FlipX == 0 ? 1 : FlipX;
        _transformTool.ViewportFlipY = FlipY == 0 ? 1 : FlipY;
        _toolController.RenderOverlay(context, CanvasZoom);

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

        if (IsBrushResizePreviewActive)
            RenderBrushResizePreviewOnCanvas(context);
        else
            RenderToolCursorOnCanvas(context);
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
            $"comp {s.CompositeMs:0.0}ms dirty {s.CompositeDirtyTiles} miss {s.CompositeMissingTiles} q {s.PendingProjectionUpdates}";
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

    // ── Background compositor ────────────────────────────────────────────────
    // Composite() is dispatched to a worker thread so a heavy layer-stack
    // recomposite cannot stall the UI. Safety rules to avoid the prior
    // RenderLock deadlock:
    //   - Background thread takes RenderLock.Read ONLY (never Write).
    //   - Background thread NEVER calls back into the dispatcher synchronously.
    //   - Only ONE composite pass runs at a time (Interlocked sentinel).
    //   - If new dirty rects arrive during a pass, a follow-up pass is
    //     scheduled when this one finishes.
    private int _bgCompositeScheduled;
    private volatile bool _bgCompositeNeedsAnotherPass;

    private void ScheduleBackgroundComposite(uint paperColor, PixelRegion? viewport, double zoom)
    {
        if (Interlocked.CompareExchange(ref _bgCompositeScheduled, 1, 0) != 0)
        {
            _bgCompositeNeedsAnotherPass = true;
            return;
        }

        Task.Run(() => BackgroundCompositePass(paperColor, viewport, zoom));
    }

    private void BackgroundCompositePass(uint paperColor, PixelRegion? viewport, double zoom)
    {
        bool deferred = false;
        try
        {
            // Read lock is brief on the document — the heavy work is the
            // compositor pass itself. We never take Write here, so we cannot
            // be the holder that starves a UI-thread Read.
            using (_document.RenderLock.Read())
            {
                deferred = _compositor.Composite(_document.Layers, _document.Width, _document.Height,
                    paperColor, viewport, zoom);
            }
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "DrawingCanvas.BackgroundCompositePass");
        }
        finally
        {
            // Release the sentinel BEFORE posting the UI-thread continuation
            // so the next ScheduleBackgroundComposite from inside InvalidateVisual
            // can claim the slot cleanly.
            Volatile.Write(ref _bgCompositeScheduled, 0);
        }

        // Always invalidate so DrawTiles picks up the new bitmaps. If we
        // deferred work (DirtyTileBudget exhausted) or a new pass was requested
        // mid-flight, schedule another pass via the next Render() tick.
        var needsRerun = deferred || _bgCompositeNeedsAnotherPass;
        _bgCompositeNeedsAnotherPass = false;
        Dispatcher.UIThread.Post(() =>
        {
            _projectionScheduler.ApplyPending(_compositor);
            _renderLod = _compositor.LastLod;
            InvalidateVisual();
            var needMore = needsRerun
                || _compositor.PendingDirtyTileCount > 0
                || _projectionScheduler.PendingCount > 0;
            if (needMore) QueueDeferredTileRender();
        }, DispatcherPriority.Render);
    }

    /// <summary>Draw tool cursor in canvas-local coordinates (always visible over the document).</summary>
    internal void RenderToolCursorOnCanvas(DrawingContext context)
    {
        if (!ShouldShowToolCursor)
            return;

        var pos = _isCursorPreviewLocked ? _lockedPointerPos : _pointerPos;
        // Canvas-local coords; parent viewport scale applies zoom — do not multiply by CanvasZoom here.
        DrawToolCursorAt(context, pos, 1.0);
    }

    /// <summary>Draw tool cursor in <paramref name="viewportSpace"/> coordinates (full workspace viewport).</summary>
    internal void RenderToolCursorInViewportSpace(DrawingContext context, Visual viewportSpace)
    {
        if (!ShouldShowToolCursor)
            return;

        DrawToolCursorAt(context, GetToolCursorViewportPosition(viewportSpace), Math.Max(CanvasZoom, 0.001));
    }

    /// <summary>Radial brush-size gesture on the document (canvas-local coords; parent scale applies zoom).</summary>
    internal void RenderBrushResizePreviewOnCanvas(DrawingContext context)
    {
        if (!_brushResizeEdgeCanvasPoint.HasValue || !_isCursorPreviewLocked)
            return;

        DrawBrushResizePreviewAt(
            context,
            _lockedPointerPos,
            _brushResizeEdgeCanvasPoint.Value,
            ActiveToolCursorSize() * 0.5,
            penScale: Math.Max(0.5, 1.0 / Math.Max(CanvasZoom, 0.001)));
    }

    /// <summary>Same preview in viewport overlay space (checkerboard / margins outside the canvas).</summary>
    internal void RenderBrushResizePreviewInViewportSpace(DrawingContext context, Visual viewportSpace)
    {
        if (!_brushResizeEdgeCanvasPoint.HasValue || !_isCursorPreviewLocked)
            return;

        if (!TryCanvasPointToVisual(viewportSpace, _lockedPointerPos, out var centerVp)
            || !TryCanvasPointToVisual(viewportSpace, _brushResizeEdgeCanvasPoint.Value, out var edgeVp))
        {
            return;
        }

        var zoom = Math.Max(CanvasZoom, 0.001);
        DrawBrushResizePreviewAt(
            context,
            centerVp,
            edgeVp,
            ActiveToolCursorSize() * 0.5 * zoom,
            penScale: Math.Max(0.5, 1.5));
    }

    private bool TryCanvasPointToVisual(Visual target, Point canvasPoint, out Point targetPoint)
    {
        var matrix = this.TransformToVisual(target);
        if (matrix.HasValue)
        {
            targetPoint = matrix.Value.Transform(canvasPoint);
            return true;
        }

        var translated = this.TranslatePoint(canvasPoint, target);
        if (translated.HasValue)
        {
            targetPoint = translated.Value;
            return true;
        }

        targetPoint = default;
        return false;
    }

    private void DrawBrushResizePreviewAt(
        DrawingContext context,
        Point center,
        Point edge,
        double radius,
        double penScale)
    {
        var t = Math.Max(0.5, 1.5 * penScale);

        context.DrawEllipse(null, new Pen(CursorOuterBrush, t * 3), center, radius, radius);
        context.DrawEllipse(null, new Pen(CursorInnerBrush, t), center, radius, radius);

        var handleR = Math.Max(3.0, t * 2.5);
        context.DrawEllipse(CursorOuterBrush, null, edge, handleR, handleR);
        context.DrawEllipse(CursorInnerBrush, null, edge, Math.Max(1.0, handleR * 0.45), Math.Max(1.0, handleR * 0.45));
    }

    private void DrawToolCursorAt(DrawingContext context, Point pos, double radiusScale)
    {
        var t = Math.Max(0.5, 1.5);
        var mode = ActiveCursorMode();
        var isBrushLike = _toolController.ActiveTool is CompositeTool ct && ct.Input.HasBrushCursor;

        if (_forceBrushOutlineCursor || (isBrushLike && mode is BrushCursorMode.Outline or BrushCursorMode.DotAndOutline))
        {
            var r = ActiveToolCursorSize() * 0.5 * radiusScale;
            context.DrawEllipse(null, new Pen(CursorOuterBrush, t * 3), pos, r, r);
            context.DrawEllipse(null, new Pen(CursorInnerBrush, t), pos, r, r);
        }

        if (!_forceBrushOutlineCursor && isBrushLike && mode == BrushCursorMode.BrushShape)
            DrawBrushShapeCursor(context, pos, t, radiusScale);

        if (!isBrushLike || mode is BrushCursorMode.Dot or BrushCursorMode.DotAndOutline)
        {
            var r = Math.Max(2.5, t * 2);
            context.DrawEllipse(CursorOuterBrush, null, pos, r, r);
            context.DrawEllipse(CursorInnerBrush, null, pos, Math.Max(0.5, r * 0.45), Math.Max(0.5, r * 0.45));
        }

        if (_toolController.IsAlternateActive
            || _ctx.ActivePreset?.OutputProcess == OutputProcessType.Eyedropper)
        {
            var swatchR = 10.0;
            var swatchPos = new Point(pos.X + swatchR * 1.6, pos.Y - swatchR * 1.6);
            var colorBrush = new SolidColorBrush(_paintColor);
            context.DrawEllipse(colorBrush, new Pen(CursorOuterBrush, t * 2), swatchPos, swatchR, swatchR);
            context.DrawEllipse(null, new Pen(CursorInnerBrush, t), swatchPos, swatchR, swatchR);
        }
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

    private void DrawBrushShapeCursor(DrawingContext context, Point pos, double t, double radiusScale = 1.0)
    {
        var r = ActiveToolCursorSize() * 0.5 * radiusScale;
        if (r < 0.5) return;
        var tip = _brush.Tip;
        var useTipMask = _ctx.ActivePreset?.InputProcess == InputProcessType.Brush;

        SKBitmap? tipBitmap = null;
        if (useTipMask)
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
        Cursor = CursorNone;
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
        Cursor = CursorNone;
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
        NotifyCursorPreviewChanged();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _isPointerOver = false;
        NotifyCursorPreviewChanged();
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

        Cursor = CursorNone;

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
        {
            HandlePointerInput(ToolInputEventKind.Move, point);
            // Only invalidate for hover preview if the cursor actually moved.
            if (_pointerPos != _prevPointerPos)
                InvalidateVisual();
        }
        // During an active stroke, brush output will trigger its own invalidation
        // via FlushPreviewDirty — no need to redundantly invalidate here.
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isPointerOver) Cursor = CursorNone;
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
        if (_toolController.ActiveTool is TransformTool tt && tt.HasPendingOperation)
        {
            tt.EndDrag(_ctx);
            return;
        }

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

        var paperUint = _document.IsPaperBackgroundVisible ? ColorToBgraUint(_document.PaperColor) : 0u;
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
        => TabletInput.IsPaintContact(point);

    private static uint ColorToBgraUint(Avalonia.Media.Color c)
        => (uint)(c.B | (c.G << 8) | (c.R << 16) | (c.A << 24));
}
