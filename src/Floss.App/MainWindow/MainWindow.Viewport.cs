using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Canvas;
using Floss.App.Document;
using Floss.App.Processes.Input;
using Floss.App.Processes.Output;
using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Tools;

namespace Floss.App;

using static Floss.App.AppColors;

public partial class MainWindow
{
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

    private void Workspace_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var f = App.Shortcuts.ZoomScrollFactor;
        var factor = e.Delta.Y > 0 ? f : 1.0 / f;
        SetZoom(_zoom * factor, e.GetPosition(_workspaceViewport));
        e.Handled = true;
    }

    private static bool IsViewportTool(ITool? tool)
    {
        if (tool is not CompositeTool ct) return false;
        return ct.Output is HandOutput || ct.Output is RotateOutput || ct.Output is ZoomOutput;
    }

    private void Workspace_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isPanning = false;
        var pt = e.GetCurrentPoint(_workspaceViewport);

        // Pen PointerPressed with IsLeftButtonPressed=False means the XI2 button mask
        // doesn't reflect actual tip contact — OTD sends phantom XI_ButtonPress events
        // that don't go through BTN_TOUCH. Real contact always has IsLeftButtonPressed=True.
        if ((pt.Pointer.Type == PointerType.Pen || pt.Properties.IsEraser) &&
            !pt.Properties.IsLeftButtonPressed)
            return;

        if (TryBeginResizeDrag(pt.Position, pt.Properties.IsLeftButtonPressed))
        {
            _isPanning = true;
            _lastPanPoint = pt.Position;
            e.Pointer.Capture(_workspaceViewport);
            e.Handled = true;
            return;
        }

        if (_activeModifierAction == ModifierAction.ChangeBrushSize)
        {
            _isBrushSizeActive = true;
            _canvas.PaintInputSuspended = true;
            _hadAlternateBeforeBrushSize = _canvas.IsAlternateActive;
            _canvas.SetAlternateActive(false);
            _brushSizeGestureStartCanvasPoint = e.GetPosition(_canvas);
            _brushSizeGestureStartSize = GetActiveToolSize();
            if (_brushSizeHasLastDir)
            {
                var startRadius = _brushSizeGestureStartSize * 0.5;
                _brushSizeGestureCenterCanvasPoint = new Point(
                    _brushSizeGestureStartCanvasPoint.X - _brushSizeLastDirX * startRadius,
                    _brushSizeGestureStartCanvasPoint.Y - _brushSizeLastDirY * startRadius);
                _brushSizeGestureHasCenter = true;
            }
            else
            {
                _brushSizeGestureCenterCanvasPoint = _brushSizeGestureStartCanvasPoint;
                _brushSizeGestureHasCenter = false;
            }
            _canvas.LockCursorPreview(_brushSizeGestureCenterCanvasPoint, forceBrushOutline: true);
            _isPanning = true;
            _lastPanPoint = pt.Position;
            e.Pointer.Capture(_workspaceViewport);
            e.Handled = true;
            return;
        }

        // Ctrl+Shift+Left: layer-pick drag.
        if (pt.Properties.IsLeftButtonPressed &&
            e.KeyModifiers.HasFlag(KeyModifiers.Control) &&
            e.KeyModifiers.HasFlag(KeyModifiers.Shift))
        {
            _canvas.StartLayerPickDrag(pt.Position);
            _isPanning = true;
            _lastPanPoint = pt.Position;
            e.Pointer.Capture(_workspaceViewport);
            e.Handled = true;
            return;
        }

        bool isPenPress = (pt.Pointer.Type == PointerType.Pen || pt.Properties.IsEraser)
            && pt.Properties.Pressure >= 0.02f;
        if (pt.Properties.IsLeftButtonPressed || isPenPress)
        {
            // Don't capture or dispatch when clicking a UI overlay (resize panel buttons etc).
            if (_resizeFloatingPanel is { IsVisible: true } floating
                && floating.Bounds.Contains(pt.Position))
                return;

            if (_selectionActionBar is { IsVisible: true } selBar
                && selBar.Bounds.Contains(pt.Position))
                return;

            if (IsViewportTool(_canvas.ActiveTool))
                _canvas.HandleViewportPointerInput(ToolInputEventKind.Down, pt.Position, pt);
            else
            {
                _canvas.HandlePointerInput(ToolInputEventKind.Down, e.GetCurrentPoint(_canvas));
                this.Cursor = new Cursor(StandardCursorType.None);
            }
            _isToolDispatchActive = true;
            _isPanning = true;
            _lastPanPoint = pt.Position;
            e.Pointer.Capture(_workspaceViewport);
            e.Handled = true;
            return;
        }

        if (pt.Properties.IsMiddleButtonPressed)
        {
            _isMiddleButtonPanning = true;
            _isPanning = true;
            _lastPanPoint = e.GetPosition(_workspaceViewport);
            e.Pointer.Capture(_workspaceViewport);
            e.Handled = true;
        }
    }

    private void Workspace_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;

        if (!_isBrushSizeActive && !IsResizeDragging && !_isToolDispatchActive && !_isMiddleButtonPanning && !_canvas.IsLayerPickDrag)
        {
            _isPanning = false;
            return;
        }

        var pt = e.GetPosition(_workspaceViewport);

        // Layer-pick drag update
        if (_canvas.IsLayerPickDrag)
        {
            _canvas.UpdateLayerPickDrag(pt);
            e.Handled = true;
            return;
        }

        if (IsResizeDragging)
        {
            UpdateResizeDrag(pt);
            e.Handled = true;
            return;
        }

        if (_isToolDispatchActive)
        {
            if (IsViewportTool(_canvas.ActiveTool))
                _canvas.HandleViewportPointerInput(ToolInputEventKind.Move, pt, e.GetCurrentPoint(_workspaceViewport));
            else
                _canvas.HandlePointerInput(ToolInputEventKind.Move, e.GetCurrentPoint(_canvas));
            _lastPanPoint = pt;
            e.Handled = true;
            return;
        }

        if (_isBrushSizeActive)
        {
            var canvasPoint = e.GetPosition(_canvas);
            if (!_brushSizeGestureHasCenter)
            {
                var startDx = canvasPoint.X - _brushSizeGestureStartCanvasPoint.X;
                var startDy = canvasPoint.Y - _brushSizeGestureStartCanvasPoint.Y;
                var startDistance = Math.Sqrt(startDx * startDx + startDy * startDy);
                if (startDistance > 10.0 / _zoom)
                {
                    var startRadius = _brushSizeGestureStartSize * 0.5;
                    _brushSizeGestureCenterCanvasPoint = new Point(
                        _brushSizeGestureStartCanvasPoint.X - startDx / startDistance * startRadius,
                        _brushSizeGestureStartCanvasPoint.Y - startDy / startDistance * startRadius);
                    _brushSizeGestureHasCenter = true;
                }
                else
                {
                    _lastPanPoint = pt;
                    _canvas.LockCursorPreview(_brushSizeGestureCenterCanvasPoint, forceBrushOutline: true);
                    e.Handled = true;
                    return;
                }
            }

            var dx = canvasPoint.X - _brushSizeGestureCenterCanvasPoint.X;
            var dy = canvasPoint.Y - _brushSizeGestureCenterCanvasPoint.Y;
            var radiusDistance = Math.Sqrt(dx * dx + dy * dy);
            if (radiusDistance > 0.001)
            {
                _brushSizeLastDirX = dx / radiusDistance;
                _brushSizeLastDirY = dy / radiusDistance;
                _brushSizeHasLastDir = true;
            }
            SetActiveToolSize(BrushSizeAdjustment.FromRadiusDistance(
                radiusDistance,
                GetActiveToolSizeMinimum(),
                GetActiveToolSizeMaximum()));
            _lastPanPoint = pt;
            _canvas.LockCursorPreview(_brushSizeGestureCenterCanvasPoint, forceBrushOutline: true);
            e.Handled = true;
            return;
        }

        var d = pt - _lastPanPoint;
        _lastPanPoint = pt;
        _canvasPan.X += d.X;
        _canvasPan.Y += d.Y;
        SyncViewportStateToCanvas();
        InvalidateViewport();
        ClampCanvasPan();
        UpdateSelectionActionBar();
        e.Handled = true;
    }

    private void Workspace_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        if (IsResizeDragging) EndResizeDrag();
        _isPanning = false;
        _isMiddleButtonPanning = false;

        // Layer-pick release
        if (_canvas.IsLayerPickDrag)
        {
            var pt = e.GetPosition(_workspaceViewport);
            _canvas.EndLayerPickDrag(pt);
        }
        else if (_isToolDispatchActive)
        {
            _isToolDispatchActive = false;
            var pt = e.GetCurrentPoint(_workspaceViewport);
            if (IsViewportTool(_canvas.ActiveTool))
                _canvas.HandleViewportPointerInput(ToolInputEventKind.Up, pt.Position, pt);
            else
                _canvas.HandlePointerInput(ToolInputEventKind.Up, e.GetCurrentPoint(_canvas));
            this.Cursor = null; // restore default hit-test cursor
        }
        if (_isBrushSizeActive)
        {
            _isBrushSizeActive = false;
            FinishActiveToolSizeEdit();
        }
        _canvas.UnlockCursorPreview();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Workspace_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (IsResizeDragging) EndResizeDrag();
        _isPanning = false;
        _isMiddleButtonPanning = false;
        if (_isToolDispatchActive)
        {
            _isToolDispatchActive = false;
            _canvas.CancelActiveTool();
        }
        if (_isBrushSizeActive)
        {
            _isBrushSizeActive = false;
            FinishActiveToolSizeEdit();
        }
        _canvas.UnlockCursorPreview();
        _canvas.PaintInputSuspended = false;
        this.Cursor = null;
    }

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
    {
        var preset = _activeToolGroup?.ActivePreset;
        return preset?.OutputProcess == OutputProcessType.Liquify ? 500 : _sizeSlider.Maximum;
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

        _sizeSlider.Value = clamped;
    }

    private void FinishActiveToolSizeEdit()
    {
        if (_activeToolGroup?.ActivePreset?.OutputProcess == OutputProcessType.Liquify)
        {
            RefreshToolProperties();
            App.ToolGroups.Save();
        }
    }

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
        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        _rotDisplay.Text = "0°";
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
            // Rotate the pan vector by the delta so the visual viewport center stays fixed.
            // In Y-down screen coords a positive angle is CW, which corresponds to the
            // standard CCW (Y-up) rotation matrix applied to the pan offset vector.
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
        _rotDisplay.Text = $"{Math.Round(_rotation)}°";
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

        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        _rotDisplay.Text = "0°";
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

    private void ToggleCanvasOnly()
    {
        _canvasOnly = !_canvasOnly;

        // Krita-style: hide only the left/right dockers, keep
        // menu bar, top toolbar, status bar and footer visible.
        if (_leftRail != null) _leftRail.IsVisible = !_canvasOnly;
        if (_leftSplitter != null) _leftSplitter.IsVisible = !_canvasOnly;
        if (_rightPanel != null) _rightPanel.IsVisible = !_canvasOnly;
        if (_splitterControl != null) _splitterControl.IsVisible = !_canvasOnly;

        // Collapse / restore layout grid columns so the canvas expands into
        // the space freed by the hidden side panels.
        if (_rootGrid != null && _rootColumnWidths != null)
        {
            for (var i = 0; i < _rootGrid.ColumnDefinitions.Count; i++)
            {
                var col = _rootGrid.ColumnDefinitions[i];
                if (_canvasOnly)
                {
                    col.Width = i == 2
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

        if (_canvasOnly)
        {
            _workspaceViewport.Focus();
        }
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
    }

    // Centralized invalidation after ANY viewport mutation (pan, zoom, rotate, flip, reset).
    // Prevents the classic bug where a refactor drops one of the six InvalidateVisual() calls
    // and tiles go stale when zooming / rotating.
    private void InvalidateViewport()
    {
        _canvasFrame?.InvalidateVisual();
        _canvas?.InvalidateVisual();
        _rulerOverlay?.InvalidateVisual();
        _checkerboardOverlay?.InvalidateVisual();
        _resizeOverlay?.InvalidateVisual();
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

    // ── Keyboard ──────────────────────────────────────────────────────────────
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement();
        if (focused is TextBox or ComboBox)
            return;

        if (_recordingToolGroup != null)
        {
            HandleShortcutRecording(e);
            return;
        }

        var key = e.Key;
        var mods = Input.KeyBinding.ModifiersWithKeyDown(key, e.KeyModifiers);
        _canvas.SetCurrentModifiers(mods);

        if (!IsModifierKey(key))
            _heldBaseKeys.Add(key);

        ReevaluateModifierState(mods);

        // When a tool dispatch (stroke) is active, suppress modifier key events
        // to prevent them from reaching the in-window Menu control. Without this,
        // pressing Alt activates the menu bar (access keys), which shifts focus
        // and releases pointer capture — cancelling the stroke.
        // CSP-style: modifier state is tracked but the stroke continues uninterrupted.
        if (_isToolDispatchActive && IsModifierKey(key))
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

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        _heldBaseKeys.Remove(e.Key);
        var mods = Floss.App.Input.KeyBinding.ModifiersAfterKeyUp(e.Key, e.KeyModifiers);
        _canvas.SetCurrentModifiers(mods);
        ReevaluateModifierState(mods);
    }

    private void ResetTransientInputState()
    {
        _heldBaseKeys.Clear();
        _isPanning = false;
        _isMiddleButtonPanning = false;
        _isToolDispatchActive = false;
        if (_isBrushSizeActive)
        {
            _isBrushSizeActive = false;
            _canvas.UnlockCursorPreview();
            _canvas.PaintInputSuspended = false;
        }
        switch (_activeModifierAction)
        {
            case ModifierAction.ToolAux:
                _canvas.SetToolAuxMode(Floss.App.Input.ToolAuxOperationType.None);
                break;
            case ModifierAction.ChangeToolTemporarily:
                if (_temporaryPresetActive)
                    PopTemporaryPreset();
                else
                    _canvas.SetAlternateActive(false);
                break;
        }
        _hadAlternateBeforeBrushSize = false;
        _activeModifierAction = ModifierAction.None;
        _activeModifierCombo = KeyModifiers.None;
        _activeModifierKey = null;
        if (_workspaceViewport != null)
            this.Cursor = null;
    }

    // Single source of truth for modifier key state.
    // Resolution priority:
    //   1. Key-specific (ExactKeyMatch) for any held base key + current mods
    //   2. Modifier-only (AnyKeyMatch) for current mods — ONLY when no base keys are held,
    //      so that shortcut keypresses (Ctrl+Z, Alt+[, etc.) never transiently activate
    //      a modifier-only action.
    private void ReevaluateModifierState(KeyModifiers mods)
    {
        var activePreset = _activeToolGroup?.ActivePreset;
        if (activePreset == null) return;

        var inputType = (int)activePreset.InputProcess;
        var outputType = (int)activePreset.OutputProcess;

        ModifierKeyAssignment? assignment = null;

        // Key-specific (ExactKeyMatch) assignments take priority.
        // Resolve() tries both ExactKeyMatch and AnyKeyMatch internally; we only accept
        // results that matched on the key itself (assignment.Key.HasValue), so that
        // shortcut keypresses (e.g. Z while Ctrl held) don't pick up the modifier-only
        // Ctrl→MoveLayer entry via AnyKeyMatch.
        foreach (var heldKey in _heldBaseKeys)
        {
            var a = App.ModifierKeys.Resolve(inputType, outputType, heldKey, mods);
            if (a != null && a.Key.HasValue) { assignment = a; break; }
        }

        // Modifier-only assignments only apply when no base key is held.
        if (assignment == null && _heldBaseKeys.Count == 0)
            assignment = App.ModifierKeys.Resolve(inputType, outputType, null, mods);

        // Early-out if nothing changed.
        bool unchanged = (assignment == null && _activeModifierAction == ModifierAction.None)
                      || (assignment != null
                          && assignment.Modifiers == _activeModifierCombo
                          && assignment.Action == _activeModifierAction
                          && assignment.Key == _activeModifierKey);
        if (unchanged) return;

        // Deactivate previous action.
        switch (_activeModifierAction)
        {
            case ModifierAction.ToolAux:
                _canvas.SetToolAuxMode(Floss.App.Input.ToolAuxOperationType.None);
                break;
            case ModifierAction.ChangeToolTemporarily:
                if (_temporaryPresetActive)
                    PopTemporaryPreset();
                else
                    _canvas.SetAlternateActive(false);
                break;
            case ModifierAction.ChangeBrushSize:
                if (_isBrushSizeActive)
                {
                    _isBrushSizeActive = false;
                    FinishActiveToolSizeEdit();
                    _canvas.UnlockCursorPreview();
                    _canvas.PaintInputSuspended = false;
                }
                break;
        }

        _activeModifierAction = assignment?.Action ?? ModifierAction.None;
        _activeModifierCombo = assignment?.Modifiers ?? KeyModifiers.None;
        _activeModifierKey = assignment?.Key;

        // Activate new action. Don't switch tools mid-stroke — that would cancel the stroke.
        switch (assignment?.Action)
        {
            case ModifierAction.ToolAux:
                _canvas.SetToolAuxMode(assignment.ToolAuxOper);
                break;
            case ModifierAction.ChangeToolTemporarily:
                if (_isToolDispatchActive) break;
                if (!string.IsNullOrEmpty(assignment.TemporaryToolPresetId))
                    PushTemporaryPreset(assignment.TemporaryToolPresetId);
                else
                    _canvas.SetAlternateActive(true);
                break;
        }
    }

    private static bool IsModifierKey(Avalonia.Input.Key key) => key switch
    {
        Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift or
        Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl or
        Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt => true,
        _ => false
    };

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }

    private void OpenModifierKeySettings()
    {
        if (_modifierKeySettingsWindow != null) { _modifierKeySettingsWindow.Activate(); return; }
        _modifierKeySettingsWindow = new Floss.App.Windows.ModifierKeySettingsWindow();
        _modifierKeySettingsWindow.Closed += (_, _) => _modifierKeySettingsWindow = null;
        _modifierKeySettingsWindow.Show(this);
    }
}
