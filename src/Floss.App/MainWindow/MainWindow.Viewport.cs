using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Brushes;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Tools;

namespace Floss.App;

using static Floss.App.Config.AppColors;

public partial class MainWindow : ICanvasInputHost
{
    private CanvasInputRouter _inputRouter = null!;
    private readonly KeyboardInputScope _keyboardInputScope = new();

    private void ActivateCanvasKeyboardRegion()
        => _keyboardInputScope.Activate(KeyboardInputRegion.Canvas);

    private void WireKeyboardRegionTracking()
    {
        KeyboardSurface.Wire(_workspaceViewport, _keyboardInputScope, KeyboardInputRegion.Canvas);
        WireNodeGraphKeyboardSurface();

        void TrackPointer(object? _, PointerEventArgs e)
            => _keyboardInputScope.UpdatePointerVisual(e.Source as Visual);

        AddHandler(PointerMovedEvent, TrackPointer, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
        AddHandler(PointerPressedEvent, TrackPointer, RoutingStrategies.Tunnel | RoutingStrategies.Bubble);
    }

    private void WireNodeGraphKeyboardSurface()
    {
        if (_nodeGraphEditor == null)
            return;

        KeyboardSurface.Wire(_nodeGraphEditor, _keyboardInputScope, KeyboardInputRegion.NodeGraph);
        KeyboardSurface.Wire(_nodeGraphEditor.GraphView, _keyboardInputScope, KeyboardInputRegion.NodeGraph);
    }

    // ── Viewport ──────────────────────────────────────────────────────────────
    private void SyncCanvasViewport()
    {
        var vpW = _workspaceViewport.Bounds.Width;
        var vpH = _workspaceViewport.Bounds.Height;
        _canvas.ViewportWidth = vpW;
        _canvas.ViewportHeight = vpH;
        _canvas.SetViewport(this, vpW, vpH);
    }

    private void SyncCanvasFrameToDocument(bool fitToViewport)
    {
        var w = Math.Max(1, _canvas.Document.Width);
        var h = Math.Max(1, _canvas.Document.Height);
        _canvasFrame.Width = w;
        _canvasFrame.Height = h;

        if (fitToViewport)
            ResetView();
        else
            InvalidateViewport();
        UpdateSelectionActionBar();
    }

    private Avalonia.Input.IPointer? _capturedPointer;

    private void InitInputRouter()
    {
        _inputRouter = new CanvasInputRouter(this);
    }

    // ── Pointer event handlers (thin delegation to router) ────────────────────

    private void Workspace_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        ActivateCanvasKeyboardRegion();
        _workspaceViewport.Focus();
        UpdateViewportPointerFromEvent(e);
        ApplyViewportOsCursorHidden();
        _inputRouter.PointerPressed(e);
        RefreshViewportCursorAfterInput();
    }

    private void Workspace_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        var canvasPoint = e.GetCurrentPoint(_canvas);
        _canvas.TrackViewportPointer(
            e.GetCurrentPoint(_workspaceViewport!),
            canvasPoint);
        SyncViewportOsCursor();

        if (_inputRouter.State != RouterState.Running)
            _canvas.HandlePointerInput(ToolInputEventKind.Move, canvasPoint);

        _inputRouter.PointerMoved(e);
        RefreshViewportCursorAfterInput();
    }

    private void Workspace_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        ActivateCanvasKeyboardRegion();
        _inputRouter.PointerReleased(e);
    }

    private void Workspace_OnPointerExited(object? sender, PointerEventArgs e)
    {
        _canvas.ClearViewportPointer();
        SyncViewportOsCursor();
    }

    private void Workspace_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        _inputRouter.CaptureLost();
    }

    private void Workspace_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var f = App.Shortcuts.ZoomScrollFactor;
        var dy = e.Delta.Y;
        var dx = e.Delta.X;
        var delta = Math.Abs(dy) >= Math.Abs(dx) ? dy : dx;
        var factor = delta > 0 ? f : 1.0 / f;
        SetZoom(_zoom * factor, e.GetPosition(_workspaceViewport));
        e.Handled = true;
    }

    // ── Viewport state ────────────────────────────────────────────────────────

    // pan_new = (cursor - vpCenter) * (1 - ratio) + pan_old * ratio
    private void SetZoom(double newZoom, Point? cursor)
    {
        var oldZoom = _zoom;
        _zoom = Math.Clamp(newZoom, 0.05, 16.0);
        _canvasScale.ScaleX = _zoom;
        _canvasScale.ScaleY = _zoom;
        _canvas.CanvasZoom = _zoom;

        if (cursor.HasValue && oldZoom > 0)
        {
            var ratio = _zoom / oldZoom;
            var vpW = _workspaceViewport.Bounds.Width;
            var vpH = _workspaceViewport.Bounds.Height;
            var c = cursor.Value;
            _canvasPan.X = (c.X - vpW * 0.5) * (1 - ratio) + _canvasPan.X * ratio;
            _canvasPan.Y = (c.Y - vpH * 0.5) * (1 - ratio) + _canvasPan.Y * ratio;
        }

        SyncViewportStateToCanvas();
        InvalidateViewport();
        ClampCanvasPan();
        if (_zoomDisplay != null) _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        if (_rotDisplay != null) _rotDisplay.Text = "0°";
        PostUpdateStatus();
        UpdateSelectionActionBar();
    }

    private void SetRotation(double degrees)
    {
        var oldRotation = _rotation;
        var newRotation = degrees % 360;
        var deltaRad = (newRotation - oldRotation) * Math.PI / 180.0;
        if (Math.Abs(deltaRad) > 1e-9)
        {
            var cos = Math.Cos(deltaRad);
            var sin = Math.Sin(deltaRad);
            var px = _canvasPan.X;
            var py = _canvasPan.Y;
            _canvasPan.X = px * cos - py * sin;
            _canvasPan.Y = px * sin + py * cos;
        }
        _rotation = newRotation;
        _canvasRotate.Angle = _rotation;
        SyncViewportStateToCanvas();
        InvalidateViewport();
        ClampCanvasPan();
        if (_rotDisplay != null) _rotDisplay.Text = $"{Math.Round(_rotation)}°";
        PostUpdateStatus();
        UpdateSelectionActionBar();
    }

    private void ResetView()
    {
        _rotation = 0;
        _canvasRotate.Angle = 0;
        _canvas.CanvasRotation = 0;
        _canvasFlip.ScaleX = 1;
        _canvasFlip.ScaleY = 1;
        _canvas.FlipX = 1;
        _canvas.FlipY = 1;

        var w = Math.Max(1, _canvas.Document.Width);
        var h = Math.Max(1, _canvas.Document.Height);
        var vpW = Math.Max(1, _workspaceViewport.Bounds.Width);
        var vpH = Math.Max(1, _workspaceViewport.Bounds.Height);
        var outset = Math.Min(ResetViewOutset, Math.Min(vpW, vpH) * 0.2);
        var availableW = Math.Max(1, vpW - outset * 2);
        var availableH = Math.Max(1, vpH - outset * 2);

        _zoom = Math.Clamp(Math.Min(availableW / w, availableH / h), 0.05, 16.0);
        _canvasScale.ScaleX = _zoom;
        _canvasScale.ScaleY = _zoom;
        _canvasPan.X = 0;
        _canvasPan.Y = 0;
        SyncViewportStateToCanvas();
        InvalidateViewport();
        ClampCanvasPan();

        if (_zoomDisplay != null) _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        if (_rotDisplay != null) _rotDisplay.Text = "0°";
        UpdateStatus();
        UpdateSelectionActionBar();
    }

    private void UpdateSelectionActionBar()
    {
        if (_selectionActionBar == null || _workspaceViewport == null || _canvas == null)
            return;

        var visible = _canvasFrame.IsVisible && _canvas.HasSelection && !_canvas.IsTransformActive;
        _selectionActionBar.IsVisible = visible;
        if (!visible) return;

        var x = Math.Max(12, (_workspaceViewport.Bounds.Width - 180) * 0.5);
        var y = 12.0;
        var bounds = _canvas.Selection.GetMaskBounds();
        if (bounds is { } b)
        {
            var anchor = _canvas.TranslatePoint(
                new Point(b.Left + b.Width * 0.5, b.Top),
                _workspaceViewport);
            if (anchor.HasValue)
            {
                var barW = Math.Max(180, _selectionActionBar.Bounds.Width);
                var barH = Math.Max(34, _selectionActionBar.Bounds.Height);
                x = anchor.Value.X - barW * 0.5;
                y = anchor.Value.Y - barH - 12;
            }
        }

        var maxX = Math.Max(12, _workspaceViewport.Bounds.Width - Math.Max(180, _selectionActionBar.Bounds.Width) - 12);
        var maxY = Math.Max(12, _workspaceViewport.Bounds.Height - Math.Max(34, _selectionActionBar.Bounds.Height) - 12);
        x = Math.Clamp(x, 12, maxX);
        y = Math.Clamp(y, 12, maxY);
        _selectionActionBar.Margin = new Thickness(Math.Round(x), Math.Round(y), 0, 0);
    }

    private void CaptureRootColumnWidths()
    {
        if (_rootGrid == null || _canvasOnly)
            return;

        _rootColumnWidths = [.. _rootGrid.ColumnDefinitions.Select(c => c.Width)];
    }

    private void ApplyCanvasOnlyLayout(bool canvasOnly)
    {
        if (_rightPanel != null) _rightPanel.IsVisible = !canvasOnly;

        if (_rootGrid == null || _rootColumnWidths == null)
            return;

        for (var i = 0; i < _rootGrid.ColumnDefinitions.Count; i++)
        {
            var col = _rootGrid.ColumnDefinitions[i];
            if (canvasOnly)
            {
                col.Width = i == 2 // RootColCenter — see notes/wide-dockable-layout.md
                    ? new GridLength(1, GridUnitType.Star)
                    : new GridLength(0);
                col.MinWidth = 0;
                col.MaxWidth = double.PositiveInfinity;
            }
            else
            {
                col.Width = _rootColumnWidths[i];
                col.MinWidth = i == 2 ? 320 : 0;
                col.MaxWidth = double.PositiveInfinity;
            }
        }
    }

    private void EnterCanvasOnlyMode()
    {
        if (!_canvasOnly)
            CaptureRootColumnWidths();

        _canvasOnly = true;
        ApplyCanvasOnlyLayout(true);
        _workspaceViewport.Focus();
    }

    private void ExitCanvasOnlyMode()
    {
        if (!_canvasOnly)
            return;

        _canvasOnly = false;
        ApplyCanvasOnlyLayout(false);
    }

    private void ToggleCanvasOnly()
    {
        if (_canvasOnly)
            ExitCanvasOnlyMode();
        else
            EnterCanvasOnlyMode();
    }

    private void ToggleRulers()
    {
        _showRulers = !_showRulers;
        if (_rulerOverlay != null) _rulerOverlay.IsVisible = _showRulers;
        App.Config.ShowRulers = _showRulers;
        App.Config.Save();
    }

    private void SyncViewportStateToCanvas()
    {
        _canvas.PanOffsetX = _canvasPan.X;
        _canvas.PanOffsetY = _canvasPan.Y;
        _canvas.CanvasZoom = _zoom;
        _canvas.CanvasRotation = _rotation;
        _canvas.FlipX = (int)_canvasFlip.ScaleX;
        _canvas.FlipY = (int)_canvasFlip.ScaleY;
        _selectionOutlineOverlay?.InvalidateVisual();
    }

    private void InvalidateViewport()
    {
        _canvasFrame?.InvalidateVisual();
        _canvas?.InvalidateVisual();
        _rulerOverlay?.InvalidateVisual();
        _checkerboardOverlay?.InvalidateVisual();
        _resizeOverlay?.InvalidateVisual();
        _selectionOutlineOverlay?.InvalidateVisual();
        _viewportCursorOverlay?.InvalidateVisual();
    }

    private void ClampCanvasPan()
    {
        var vpW = Math.Max(1, _workspaceViewport.Bounds.Width);
        var vpH = Math.Max(1, _workspaceViewport.Bounds.Height);
        if (vpW <= 1 || vpH <= 1) return;

        var angle = Math.Abs(_rotation % 180) * Math.PI / 180.0;
        var cos = Math.Abs(Math.Cos(angle));
        var sin = Math.Abs(Math.Sin(angle));
        var docW = Math.Max(1, _canvas.Document.Width) * _zoom;
        var docH = Math.Max(1, _canvas.Document.Height) * _zoom;
        var rotatedW = docW * cos + docH * sin;
        var rotatedH = docW * sin + docH * cos;

        var maxX = Math.Max(100.0, vpW * 0.5 + rotatedW * 0.5 - 80);
        var maxY = Math.Max(100.0, vpH * 0.5 + rotatedH * 0.5 - 80);

        _canvasPan.X = Math.Clamp(_canvasPan.X, -maxX, maxX);
        _canvasPan.Y = Math.Clamp(_canvasPan.Y, -maxY, maxY);
    }

    private void PostUpdateStatus()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateStatus, Avalonia.Threading.DispatcherPriority.Background);
    }

    private void PostUpdateTitle()
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(UpdateTitle, Avalonia.Threading.DispatcherPriority.Background);
    }

    // ── IViewportController ────────────────────────────────────────────────────
    double Tools.IViewportController.Zoom => _zoom;
    double Tools.IViewportController.PanOffsetX => _canvasPan.X;
    double Tools.IViewportController.PanOffsetY => _canvasPan.Y;

    void Tools.IViewportController.PanBy(double dx, double dy)
    {
        _canvasPan.X += dx;
        _canvasPan.Y += dy;
        SyncViewportStateToCanvas();
        InvalidateViewport();
        ClampCanvasPan();
        PostUpdateStatus();
        UpdateSelectionActionBar();
    }

    void Tools.IViewportController.ZoomBy(double factor, Point viewportCenter)
    {
        SetZoom(_zoom * factor, viewportCenter);
    }

    void Tools.IViewportController.RotateBy(double degrees)
    {
        SetRotation(_rotation + degrees);
    }

    // ── ICanvasInputHost implementation ──

    PointerPoint ICanvasInputHost.GetViewportPointerPoint(PointerEventArgs e)
        => e.GetCurrentPoint(_workspaceViewport);

    PointerPoint ICanvasInputHost.GetCanvasPointerPoint(PointerEventArgs e)
        => e.GetCurrentPoint(_canvas);

    Point ICanvasInputHost.GetViewportPosition(PointerEventArgs e)
        => e.GetPosition(_workspaceViewport);

    Point ICanvasInputHost.GetCanvasPosition(PointerEventArgs e)
        => e.GetPosition(_canvas);

    void ICanvasInputHost.DispatchViewportPointerInput(ToolInputEventKind kind, Point viewportPos, PointerPoint point)
        => _canvas.HandleViewportPointerInput(kind, viewportPos, point);

    bool ICanvasInputHost.HasViewportNavOverlay => _canvas.HasViewportNavOverlay;

    void ICanvasInputHost.DispatchPointerInput(ToolInputEventKind kind, PointerPoint canvasPoint)
        => _canvas.HandlePointerInput(kind, canvasPoint);

    bool ICanvasInputHost.PushTemporaryPreset(string presetId)
        => PushTemporaryPreset(presetId);

    void ICanvasInputHost.PopTemporaryPreset()
        => PopTemporaryPreset();

    void ICanvasInputHost.SetAlternateActive(bool active)
        => _canvas.SetAlternateActive(active);

    bool ICanvasInputHost.IsAlternateActive => _canvas.IsAlternateActive;

    bool ICanvasInputHost.HasActiveToolAlternate =>
        _canvas.ActiveTool is CompositeTool { Alternate: not null };

    void ICanvasInputHost.SetCanvasModifiers(KeyModifiers mods)
        => _canvas.SetCurrentModifiers(mods);

    void ICanvasInputHost.SetToolAuxMode(ToolAuxOperationType mode)
        => _canvas.SetToolAuxMode(mode);

    void ICanvasInputHost.CancelActiveTool()
        => _canvas.CancelActiveTool();

    void ICanvasInputHost.CommitActiveTool()
        => _canvas.CommitActiveTool();

    bool ICanvasInputHost.IsTransformActive => _canvas.IsTransformActive;

    void ICanvasInputHost.EndTransformDragIfActive()
        => _canvas.EndTransformDragIfActive();

    ITool? ICanvasInputHost.ActiveTool => _canvas.ActiveTool;

    (int, int) ICanvasInputHost.GetActiveToolTypes()
    {
        var preset = _activeToolGroup?.ActivePreset;
        if (preset == null) return (0, 0);
        return ((int)preset.InputProcess, (int)preset.OutputProcess);
    }

    bool ICanvasInputHost.IsLayerPickDrag => _canvas.IsLayerPickDrag;

    void ICanvasInputHost.StartLayerPickDrag(Point pos)
        => _canvas.StartLayerPickDrag(pos);

    void ICanvasInputHost.UpdateLayerPickDrag(Point pos)
        => _canvas.UpdateLayerPickDrag(pos);

    void ICanvasInputHost.EndLayerPickDrag(Point pos)
        => _canvas.EndLayerPickDrag(pos);

    void ICanvasInputHost.LockCursorPreview(Point center, bool forceBrushOutline)
        => _canvas.LockCursorPreview(center, forceBrushOutline);

    void ICanvasInputHost.UnlockCursorPreview()
        => _canvas.UnlockCursorPreview();

    void ICanvasInputHost.SetBrushResizeEdgePreview(Point edgeCanvasPoint)
        => _canvas.SetBrushResizeEdgePreview(edgeCanvasPoint);

    void ICanvasInputHost.ClearBrushResizePreview()
        => _canvas.ClearBrushResizePreview();

    void ICanvasInputHost.RefreshCursorAfterInput()
        => RefreshViewportCursorAfterInput();

    bool ICanvasInputHost.TryCanvasPointToScreen(Point canvasPoint, out PixelPoint screen)
    {
        screen = default;
        if (_canvas == null)
            return false;

        try
        {
            screen = _canvas.PointToScreen(canvasPoint);
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    bool ICanvasInputHost.TryWarpCursorToCanvasPoint(Point canvasPoint)
    {
        if (!((ICanvasInputHost)this).TryCanvasPointToScreen(canvasPoint, out var screen))
            return false;

        return PlatformCursorWarp.TrySet(screen);
    }

    double ICanvasInputHost.GetActiveToolSize() => GetActiveToolSize();

    double ICanvasInputHost.GetActiveToolSizeMin() => GetActiveToolSizeMinimum();

    double ICanvasInputHost.GetActiveToolSizeMax() => GetActiveToolSizeMaximum();

    void ICanvasInputHost.SetActiveToolSize(double size) => SetActiveToolSize(size);

    void ICanvasInputHost.FinishActiveToolSizeEdit() => FinishActiveToolSizeEdit();

    double ICanvasInputHost.Zoom => _zoom;

    IViewportController? ICanvasInputHost.ViewportController => this;

    void ICanvasInputHost.InvalidateViewport() => InvalidateViewport();

    bool ICanvasInputHost.TryBeginResizeDrag(Point pos, bool isPrimary)
    {
        // Check resize overlay — if clicked on resize floating panel buttons, don't capture.
        if (_resizeFloatingPanel is { IsVisible: true } floating
            && floating.Bounds.Contains(pos))
            return false;
        if (_selectionActionBar is { IsVisible: true } selBar
            && selBar.Bounds.Contains(pos))
            return false;
        return TryBeginResizeDrag(pos, isPrimary);
    }

    bool ICanvasInputHost.IsResizeDragging => IsResizeDragging;

    void ICanvasInputHost.EndResizeDrag() => EndResizeDrag();

    void ICanvasInputHost.UpdateResizeDrag(Point pt) => UpdateResizeDrag(pt);

    void ICanvasInputHost.CapturePointer(IPointer pointer)
    {
        _capturedPointer = pointer;
        pointer.Capture(_workspaceViewport);
    }

    void ICanvasInputHost.ReleasePointerCapture()
    {
        _capturedPointer?.Capture(null);
        _capturedPointer = null;
    }

    bool ICanvasInputHost.PaintInputSuspended
    {
        get => _canvas.PaintInputSuspended;
        set => _canvas.PaintInputSuspended = value;
    }

    bool ICanvasInputHost.IsOverCanvasUi(Point viewportPos)
    {
        if (_resizeFloatingPanel is { IsVisible: true } floating
            && floating.Bounds.Contains(viewportPos))
            return true;
        if (_selectionActionBar is { IsVisible: true } selBar
            && selBar.Bounds.Contains(viewportPos))
            return true;
        return false;
    }

    void ICanvasInputHost.SetCursorNone() => ApplyViewportOsCursorHidden();

    void ICanvasInputHost.ResetCursor() => SyncViewportOsCursor();

    // ── Keyboard ──────────────────────────────────────────────────────────────
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        if (_recordingToolGroup != null)
        {
            HandleShortcutRecording(e);
            return;
        }

        var focused = FocusManager.GetFocusedElement() as IInputElement;

        if (_keyboardInputScope.ShouldRouteToNodeGraph(focused))
        {
            if (_nodeGraphEditor?.TryHandleKeyDown(e) == true)
                e.Handled = true;
            return;
        }

        if (!_keyboardInputScope.ShouldRouteToCanvas(focused))
            return;

        _inputRouter.KeyDown(e);
        if (ShouldReserveCanvasModifierKey(e))
            e.Handled = true;
    }

    private void HandleShortcutRecording(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { CancelToolGroupShortcutRecording(); e.Handled = true; return; }
        if (e.Key is Key.Back or Key.Delete) { CommitToolGroupShortcut(Input.KeyBinding.Empty); e.Handled = true; return; }
        if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt) return;
        var mods = Input.KeyBinding.ModifiersWithKeyDown(e.Key, e.KeyModifiers);
        CommitToolGroupShortcut(new Input.KeyBinding(e.Key, mods));
        e.Handled = true;
    }

    private void OnKeyUpTunnel(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement() as IInputElement;

        if (_keyboardInputScope.ShouldRouteToNodeGraph(focused))
        {
            if (_nodeGraphEditor?.TryHandleKeyUp(e) == true)
                e.Handled = true;
            return;
        }

        if (!_keyboardInputScope.ShouldRouteToCanvas(focused))
            return;

        _inputRouter.KeyUp(e);
        if (ShouldReserveCanvasModifierKey(e))
            e.Handled = true;
    }

    private bool ShouldReserveCanvasModifierKey(KeyEventArgs e)
    {
        if (!_canvas.HasDocument) return false;

        // Avalonia/Linux treats bare Alt as menu activation. In a drawing
        // viewport Alt is a canvas modifier first, otherwise eyedropper and
        // Ctrl+Alt brush-size alternate every other press as the menu takes
        // focus.
        if (e.Key is Key.LeftAlt or Key.RightAlt)
            return true;

        return false;
    }

    private void ResetTransientInputState()
    {
        if (_canvas.IsTransformActive)
            return;

        if (_temporaryPresetActive)
            PopTemporaryPreset();
        _inputRouter.ResetAllState();
        _canvas.RecoverInputState();
        SyncViewportOsCursor();
        ActivateCanvasKeyboardRegion();
    }

    // ── Brush size ────────────────────────────────────────────────────────────

    private void NudgeBrushSize(int direction, bool large)
    {
        var sc = App.Shortcuts;
        var configuredStep = large ? sc.BrushSizeStepLarge : sc.BrushSizeStep;
        SetActiveToolSize(BrushSizeAdjustment.Nudge(
            GetActiveToolSize(),
            direction,
            configuredStep,
            GetActiveToolSizeMinimum(),
            GetActiveToolSizeMaximum()));
        FinishActiveToolSizeEdit();
    }

    private double GetActiveToolSize()
    {
        var preset = _activeToolGroup?.ActivePreset;
        return preset?.OutputProcess switch
        {
            OutputProcessType.Liquify => preset.LiquifySize,
            OutputProcessType.DirectDraw => _activePreset?.Size ?? _canvas.Brush.Size,
            _ => _sizeSlider.Value
        };
    }

    private double GetActiveToolSizeMinimum()
    {
        var preset = _activeToolGroup?.ActivePreset;
        return preset?.OutputProcess == OutputProcessType.Liquify ? 10 : _sizeSlider.Minimum;
    }

    private double GetActiveToolSizeMaximum()
        => ResolveBrushSizeMaximum();

    private double ResolveBrushSizeMaximum()
    {
        var preset = _activeToolGroup?.ActivePreset;
        if (preset?.OutputProcess == OutputProcessType.Liquify)
            return 500;

        var brushPreset = _activePreset ?? _canvas?.Brush;
        var maxPercent = brushPreset?.MaxSizePercent ?? BrushSizeLimits.DefaultMaxSizePercent;
        if (_canvas?.HasDocument == true)
        {
            return BrushSizeLimits.EffectiveMaximum(
                _canvas.Document.Width,
                _canvas.Document.Height,
                maxPercent);
        }

        return BrushSizeLimits.FallbackMaxDiameterPx;
    }

    internal void SyncBrushSizeLimits()
    {
        var max = ResolveBrushSizeMaximum();
        _sizeSlider.Maximum = max;

        var current = GetActiveToolSize();
        if (current > max)
            SetActiveToolSize(max);
    }

    private void SetActiveToolSize(double size)
    {
        var preset = _activeToolGroup?.ActivePreset;
        var clamped = Math.Clamp(size, GetActiveToolSizeMinimum(), GetActiveToolSizeMaximum());
        if (preset?.OutputProcess == OutputProcessType.Liquify)
        {
            preset.LiquifySize = clamped;
            _canvas.InvalidateVisual();
            return;
        }

        if (preset?.InputProcess.IsBrushFamily() == true &&
            preset.OutputProcess == OutputProcessType.DirectDraw)
        {
            SetBrushSizeLive(clamped);
            return;
        }

        _sizeSlider.Value = clamped;
    }

    private void SetBrushSizeLive(double size)
    {
        _activePreset ??= _canvas.Brush;
        var updated = _activePreset with { Size = size };
        _activePreset = updated;

        var activeToolPreset = _activeToolGroup?.ActivePreset;
        if (activeToolPreset?.InputProcess.IsBrushFamily() == true &&
            activeToolPreset.OutputProcess == OutputProcessType.DirectDraw)
            activeToolPreset.CaptureFromBrushPreset(updated);

        ScheduleBrushPresetAutosave();

        var applied = updated with { Color = _canvas.PaintColor };
        _canvas.SetBrush(applied);
        _strokePreview.Brush = applied;

        _syncingBrushUi = true;
        try
        {
            _sizeSlider.Value = Math.Clamp(size, _sizeSlider.Minimum, _sizeSlider.Maximum);
        }
        finally
        {
            _syncingBrushUi = false;
        }
    }

    private void FinishActiveToolSizeEdit()
    {
        if (_activeToolGroup?.ActivePreset?.OutputProcess == OutputProcessType.Liquify)
        {
            RefreshToolProperties();
            App.ToolGroups.Save();
            return;
        }

        var preset = _activeToolGroup?.ActivePreset;
        if (preset?.InputProcess.IsBrushFamily() == true &&
            preset.OutputProcess == OutputProcessType.DirectDraw)
        {
            RefreshToolProperties();
            App.ToolGroups.Save();
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow { OnShortcutsChanged = ReloadShortcuts };
        _settingsWindow.Closed += (_, _) =>
        {
            _settingsWindow = null;
            ReloadShortcuts();
        };
        _settingsWindow.Show(this);
    }

    private void OpenModifierKeySettings()
    {
        if (_modifierKeySettingsWindow != null) { _modifierKeySettingsWindow.Activate(); return; }
        _modifierKeySettingsWindow = new Floss.App.Windows.ModifierKeySettingsWindow();
        _modifierKeySettingsWindow.Closed += (_, _) => _modifierKeySettingsWindow = null;
        _modifierKeySettingsWindow.Show(this);
    }

    private void OpenPenPressureSettings()
    {
        if (_penPressureSettingsWindow != null) { _penPressureSettingsWindow.Activate(); return; }
        _penPressureSettingsWindow = new Floss.App.Windows.PenPressureSettingsWindow();
        _penPressureSettingsWindow.Closed += (_, _) => _penPressureSettingsWindow = null;
        _penPressureSettingsWindow.Show(this);
    }
}
