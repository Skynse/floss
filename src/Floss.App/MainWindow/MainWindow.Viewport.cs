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

public partial class MainWindow
{
    // ── Viewport ──────────────────────────────────────────────────────────────
    private void SyncCanvasViewport()
    {
        if (_canvas == null || _workspaceViewport == null) return;
        _canvas.ViewportWidth = _workspaceViewport.Bounds.Width;
        _canvas.ViewportHeight = _workspaceViewport.Bounds.Height;
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
            _checkerboardOverlay?.InvalidateVisual(); _resizeOverlay?.InvalidateVisual();
        UpdateSelectionActionBar();
    }

    private void Workspace_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var f = App.Shortcuts.ZoomScrollFactor;
        var factor = e.Delta.Y > 0 ? f : 1.0 / f;
        SetZoom(_zoom * factor, e.GetPosition(_workspaceViewport));
        e.Handled = true;
    }

    private void Workspace_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _isPanning = false;
        var pt = e.GetCurrentPoint(_workspaceViewport);

        // Ignore phantom pen events (hover without contact)
        if ((pt.Pointer.Type == PointerType.Pen || pt.Properties.IsEraser) &&
            pt.Properties.Pressure < 0.02f)
            return;

        // Resize mode — handle drags before normal gesture logic
        if (TryBeginResizeDrag(pt.Position, pt.Properties.IsLeftButtonPressed))
        {
            _isPanning = true;
            _lastPanPoint = pt.Position;
            e.Pointer.Capture(_workspaceViewport);
            e.Handled = true;
            return;
        }

        var middle = pt.Properties.IsMiddleButtonPressed;
        if (_activeGesture == GestureMode.None && _activeModifierAction == ModifierAction.ChangeBrushSize)
        {
            _activeGesture = GestureMode.BrushSize;
            _gestureKey = Key.None;
            _gestureModifiers = _activeModifierCombo;
            _canvas.PaintInputSuspended = true;
            _hadAlternateBeforeGesture = _canvas.IsAlternateActive;
            _canvas.SetAlternateActive(false);
        }
        if (!middle && _activeGesture == GestureMode.None && pt.Properties.IsLeftButtonPressed)
        {
            var viewMode = _activeToolGroup?.ActivePreset?.OutputProcess switch
            {
                OutputProcessType.Pan => GestureMode.Pan,
                OutputProcessType.Zoom or OutputProcessType.ZoomOut => GestureMode.Zoom,
                OutputProcessType.Rotate => GestureMode.Rotate,
                _ => GestureMode.None
            };
            if (viewMode != GestureMode.None)
            {
                _activeGesture = viewMode;
                _canvas.PaintInputSuspended = true;
                _hadAlternateBeforeGesture = _canvas.IsAlternateActive;
                _canvas.SetAlternateActive(false);
                Cursor = viewMode == GestureMode.Pan ? CursorPan : CursorArrow;
            }
        }
        if (!middle && _activeGesture == GestureMode.None) return;
        _isPanning = true;
        _lastPanPoint = e.GetPosition(_workspaceViewport);
        _gestureStartPoint = _lastPanPoint;
        if (_activeGesture == GestureMode.BrushSize)
        {
            _brushSizeGestureStartCanvasPoint = e.GetPosition(_canvas);
            _brushSizeGestureStartSize = GetActiveToolSize();
            if (_brushSizeHasLastDir)
            {
                // Re-anchor center using the saved direction so the cursor lands
                // on the same side of the circle as the last resize ended on.
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
        }
        e.Pointer.Capture(_workspaceViewport);
        e.Handled = true;
    }

    private void Workspace_OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_isPanning) return;

        if (_activeGesture == GestureMode.None && !IsResizeDragging)
        {
            _isPanning = false;
            return;
        }

        var pt = e.GetPosition(_workspaceViewport);

        // Resize drag takes priority over pan/gesture
        if (IsResizeDragging)
        {
            UpdateResizeDrag(pt);
            e.Handled = true;
            return;
        }

        if (_activeGesture == GestureMode.BrushSize)
        {
            var canvasPoint = e.GetPosition(_canvas);
            if (!_brushSizeGestureHasCenter)
            {
                var startDx = canvasPoint.X - _brushSizeGestureStartCanvasPoint.X;
                var startDy = canvasPoint.Y - _brushSizeGestureStartCanvasPoint.Y;
                var startDistance = Math.Sqrt(startDx * startDx + startDy * startDy);
                // Require ~10 viewport pixels of deliberate movement before locking the
                // center direction so tablet jitter can't randomly flip which side you're on.
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
                    // Dead zone: hold the starting size, don't move the preview yet.
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
            _canvas.LockCursorPreview(
                _brushSizeGestureCenterCanvasPoint,
                forceBrushOutline: true);
            e.Handled = true;
            return;
        }

        var d = pt - _lastPanPoint;
        _lastPanPoint = pt;
        var sc = App.Shortcuts;

        switch (_activeGesture)
        {
            case GestureMode.Pan:
            case GestureMode.None:
                _canvasPan.X += d.X;
                _canvasPan.Y += d.Y;
                _canvas.PanOffsetX = _canvasPan.X;
                _canvas.PanOffsetY = _canvasPan.Y;
                _rulerOverlay?.InvalidateVisual();
                _checkerboardOverlay?.InvalidateVisual(); _resizeOverlay?.InvalidateVisual();
                ClampCanvasPan();
                UpdateSelectionActionBar();
                break;
            case GestureMode.Zoom:
                var axisDelta = sc.GestureZoomAxis == GestureAxis.Horizontal ? d.X : -d.Y;
                SetZoom(_zoom * Math.Pow(sc.GestureZoomSensitivity, axisDelta), _gestureStartPoint);
                break;
            case GestureMode.Rotate:
                {
                    // Arc-based rotation around the viewport center.
                    // Using viewport center (not gesture start) as pivot avoids the
                    // cursor ever passing through the pivot and flipping the angle.
                    var vpCenter = new Point(
                        _workspaceViewport.Bounds.Width * 0.5,
                        _workspaceViewport.Bounds.Height * 0.5);
                    var prevPt = pt - d;
                    var fromVec = prevPt - vpCenter;
                    var toVec = pt - vpCenter;
                    var fromDistSq = fromVec.X * fromVec.X + fromVec.Y * fromVec.Y;
                    var toDistSq = toVec.X * toVec.X + toVec.Y * toVec.Y;
                    if (fromDistSq > 400 && toDistSq > 400) // >20 px from center
                    {
                        var deltaRad = Math.Atan2(toVec.Y, toVec.X) - Math.Atan2(fromVec.Y, fromVec.X);
                        if (deltaRad > Math.PI) deltaRad -= 2 * Math.PI;
                        if (deltaRad < -Math.PI) deltaRad += 2 * Math.PI;
                        SetRotation(_rotation + deltaRad * 180.0 / Math.PI);
                    }
                    break;
                }
        }
        e.Handled = true;
    }

    private void Workspace_OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_isPanning) return;
        if (IsResizeDragging) EndResizeDrag();
        _isPanning = false;
        if (_activeGesture == GestureMode.BrushSize)
            FinishActiveToolSizeEdit();
        _canvas.UnlockCursorPreview();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    private void Workspace_OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (IsResizeDragging) EndResizeDrag();
        _isPanning = false;
        if (_activeGesture == GestureMode.BrushSize)
            FinishActiveToolSizeEdit();
        _canvas.UnlockCursorPreview();
        if (_activeGesture != GestureMode.None)
        {
            _canvas.PaintInputSuspended = false;
            _activeGesture = GestureMode.None;
            _gestureKey = Key.None;
            _gestureModifiers = KeyModifiers.None;
            Cursor = Cursor.Default;
        }
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

        _canvas.PanOffsetX = _canvasPan.X;
        _canvas.PanOffsetY = _canvasPan.Y;
        _rulerOverlay?.InvalidateVisual();
        _checkerboardOverlay?.InvalidateVisual(); _resizeOverlay?.InvalidateVisual();
        ClampCanvasPan();
        _zoomDisplay.Text = $"{Math.Round(_zoom * 100)}%";
        UpdateStatus();
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
            _canvas.PanOffsetX = _canvasPan.X;
            _canvas.PanOffsetY = _canvasPan.Y;
        }
        _rotation = newRotation;
        _canvasRotate.Angle = _rotation;
        _canvas.CanvasRotation = _rotation;
        _checkerboardOverlay?.InvalidateVisual(); _resizeOverlay?.InvalidateVisual();
        ClampCanvasPan();
        _rotDisplay.Text = $"{Math.Round(_rotation)}°";
        UpdateStatus();
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
        _canvas.CanvasZoom = _zoom;
        _canvasPan.X = 0;
        _canvasPan.Y = 0;
        _canvas.PanOffsetX = 0;
        _canvas.PanOffsetY = 0;
        _rulerOverlay?.InvalidateVisual();
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

    // ── Keyboard ──────────────────────────────────────────────────────────────
    private void OnKeyDownTunnel(object? sender, KeyEventArgs e)
    {
        var focused = FocusManager.GetFocusedElement();
        if (focused is TextBox or ComboBox)
            return;

        var key = e.Key;
        var mods = Input.KeyBinding.ModifiersWithKeyDown(key, e.KeyModifiers);
        _canvas.SetCurrentModifiers(mods);

        // Modifier key settings — view operations, tool aux, alternate tools, etc.
        if (TryApplyModifierKeyAction(key, mods))
        {
            e.Handled = true;
            return;
        }

        // Tool group shortcut recording
        if (_recordingToolGroup != null)
        {
            if (e.Key == Key.Escape) { CancelToolGroupShortcutRecording(); e.Handled = true; return; }
            if (e.Key is Key.Back or Key.Delete) { CommitToolGroupShortcut(Input.KeyBinding.Empty); e.Handled = true; return; }
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin) return;
            CommitToolGroupShortcut(new Input.KeyBinding(e.Key, mods));
            e.Handled = true;
            return;
        }

        // Preset alternate invocation recording
        if (_recordingPresetAltInvocation != null)
        {
            if (e.Key == Key.Escape) { CancelPresetAltInvocationRecording(); e.Handled = true; return; }
            if (e.Key is Key.Back or Key.Delete) { CommitPresetAltInvocation(Input.KeyBinding.Empty); e.Handled = true; return; }
            if (e.Key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            {
                _recordingPresetPendingMods = mods;
                return;
            }
            _recordingPresetPendingMods = KeyModifiers.None;
            CommitPresetAltInvocation(new Input.KeyBinding(e.Key, mods));
            e.Handled = true;
            return;
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var mods = Floss.App.Input.KeyBinding.ModifiersAfterKeyUp(e.Key, e.KeyModifiers);
        _canvas.SetCurrentModifiers(mods);
        if (_activeGesture != GestureMode.None &&
            (_gestureKey == Key.None
                ? (mods & _gestureModifiers) != _gestureModifiers
                : e.Key == _gestureKey))
        {
            EndGesture();
            e.Handled = true;
        }

        // Commit pending modifier-only key for preset alt invocation recording
        if (_recordingPresetAltInvocation != null && _recordingPresetPendingMods != KeyModifiers.None)
        {
            var pending = _recordingPresetPendingMods;
            _recordingPresetPendingMods = KeyModifiers.None;
            if (pending != KeyModifiers.None)
            {
                CommitPresetAltInvocation(new Input.KeyBinding(Key.None, pending));
                e.Handled = true;
                return;
            }
        }

        // Re-evaluate modifier key action on key-up
        EvaluateModifierKeyAction(mods);
    }

    private void ResetTransientInputState()
    {
        var hadGesture = _activeGesture != GestureMode.None;
        _isPanning = false;
        if (hadGesture)
            EndGesture();
        if (_canvas.IsAlternateActive || _hadAlternateBeforeGesture)
        {
            _canvas.SetAlternateActive(false);
            _hadAlternateBeforeGesture = false;
        }
    }

    private void EvaluateModifierKeyAction(KeyModifiers mods)
    {
        var activePreset = _activeToolGroup?.ActivePreset;
        if (activePreset == null) return;

        var inputType = (int)activePreset.InputProcess;
        var outputType = (int)activePreset.OutputProcess;
        var assignment = App.ModifierKeys.Resolve(inputType, outputType, null, mods);

        if (assignment?.Modifiers == _activeModifierCombo && assignment?.Action == _activeModifierAction)
            return;

        // Deactivate previous action
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
                if (_activeGesture == GestureMode.BrushSize)
                    EndGesture();
                break;
            case ModifierAction.ViewOperation:
                if (_activeGesture != GestureMode.None)
                    EndGesture();
                break;
        }

        _activeModifierAction = assignment?.Action ?? ModifierAction.None;
        _activeModifierCombo = assignment?.Modifiers ?? KeyModifiers.None;
        _activeModifierKey = assignment?.Key;

        // Activate new action
        switch (assignment?.Action)
        {
            case ModifierAction.ToolAux:
                _canvas.SetToolAuxMode(assignment.ToolAuxOper);
                break;
            case ModifierAction.ChangeToolTemporarily:
                if (!string.IsNullOrEmpty(assignment.TemporaryToolPresetId))
                    PushTemporaryPreset(assignment.TemporaryToolPresetId);
                else
                    _canvas.SetAlternateActive(true);
                break;
            case ModifierAction.ChangeBrushSize:
                // Gesture starts on pointer press, not here
                break;
            case ModifierAction.ViewOperation:
                BeginGestureFromAssignment(assignment);
                break;
        }
    }

    private bool TryApplyModifierKeyAction(Avalonia.Input.Key key, KeyModifiers mods)
    {
        var activePreset = _activeToolGroup?.ActivePreset;
        if (activePreset == null) return false;

        var inputType = (int)activePreset.InputProcess;
        var outputType = (int)activePreset.OutputProcess;
        var assignment = App.ModifierKeys.Resolve(inputType, outputType, key, mods);

        if (assignment == null) return false;

        // Skip if already active with same combo
        if (assignment.Modifiers == _activeModifierCombo && assignment.Action == _activeModifierAction && assignment.Key == _activeModifierKey)
            return false;

        // Deactivate previous
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
                if (_activeGesture == GestureMode.BrushSize)
                    EndGesture();
                break;
            case ModifierAction.ViewOperation:
                if (_activeGesture != GestureMode.None)
                    EndGesture();
                break;
        }

        _activeModifierAction = assignment.Action;
        _activeModifierCombo = assignment.Modifiers;
        _activeModifierKey = assignment.Key;

        // Activate
        switch (assignment.Action)
        {
            case ModifierAction.ToolAux:
                _canvas.SetToolAuxMode(assignment.ToolAuxOper);
                return true;
            case ModifierAction.ChangeToolTemporarily:
                if (!string.IsNullOrEmpty(assignment.TemporaryToolPresetId))
                    PushTemporaryPreset(assignment.TemporaryToolPresetId);
                else
                    _canvas.SetAlternateActive(true);
                return true;
            case ModifierAction.ChangeBrushSize:
                return true;
            case ModifierAction.ViewOperation:
                BeginGestureFromAssignment(assignment);
                return true;
        }

        return false;
    }

    private void BeginGestureFromAssignment(ModifierKeyAssignment assignment)
    {
        var mode = assignment.ViewOper switch
        {
            ViewOperationType.Pan => GestureMode.Pan,
            ViewOperationType.Zoom => GestureMode.Zoom,
            ViewOperationType.Rotate => GestureMode.Rotate,
            _ => GestureMode.None
        };
        if (mode == GestureMode.None) return;

        _activeGesture = mode;
        _gestureKey = assignment.Key ?? Key.None;
        _gestureModifiers = assignment.Modifiers;
        _canvas.PaintInputSuspended = true;
        _hadAlternateBeforeGesture = _canvas.IsAlternateActive;
        _canvas.SetAlternateActive(false);
        Cursor = mode switch
        {
            GestureMode.Pan => CursorPan,
            GestureMode.BrushSize => CursorNone,
            _ => CursorArrow
        };
    }

    private void EndGesture()
    {
        var wasBrushSize = _activeGesture == GestureMode.BrushSize;
        _activeGesture = GestureMode.None;
        _gestureKey = Key.None;
        _gestureModifiers = KeyModifiers.None;
        _isPanning = false;
        if (wasBrushSize)
            FinishActiveToolSizeEdit();
        _canvas.UnlockCursorPreview();
        _canvas.PaintInputSuspended = false;
        Cursor = Cursor.Default;
        if (_hadAlternateBeforeGesture)
        {
            _canvas.SetAlternateActive(true);
            _hadAlternateBeforeGesture = false;
        }
    }

    private static bool IsModifierKey(Avalonia.Input.Key key) => key switch
    {
        Avalonia.Input.Key.LeftShift or Avalonia.Input.Key.RightShift or
        Avalonia.Input.Key.LeftCtrl or Avalonia.Input.Key.RightCtrl or
        Avalonia.Input.Key.LeftAlt or Avalonia.Input.Key.RightAlt or
        Avalonia.Input.Key.Space => true,
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
