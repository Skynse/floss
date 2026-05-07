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
        if (_activeGesture == GestureMode.None)
        {
            var (gesture, gestureBinding) = DetectGesture(Key.None, e.KeyModifiers, App.Shortcuts);
            if (gesture != GestureMode.None && gestureBinding?.IsModifierOnly == true)
            {
                BeginGesture(gesture, Key.None, gestureBinding);
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
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {

        var focused = FocusManager.GetFocusedElement();
        if (focused is TextBox or ComboBox)
        {
            // Let the input control handle the key normally
            return;
        }
        var key = e.Key;
        var mods = Floss.App.Input.KeyBinding.ModifiersWithKeyDown(key, e.KeyModifiers);
        var sc = App.Shortcuts;

        // Gestures must be checked before other shortcuts since they
        // consume modifier-only key-downs that shouldn't trigger tool shortcuts.
        var (gesture, gestureBinding) = DetectGesture(key, mods, sc);
        if (gesture != GestureMode.None)
        {
            BeginGesture(gesture, key, gestureBinding);
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

        if (sc.Undo.Matches(key, mods)) { _canvas.Undo(); e.Handled = true; }
        else if (sc.Redo.Matches(key, mods)) { _canvas.Redo(); e.Handled = true; }
        else if (sc.RedoAlt.Matches(key, mods)) { _canvas.Redo(); e.Handled = true; }
        else if (sc.ToggleCanvasOnly.Matches(key, mods)) { ToggleCanvasOnly(); e.Handled = true; }
        else if (sc.ToggleRulers.Matches(key, mods)) { ToggleRulers(); e.Handled = true; }
        else if (sc.FileNew.Matches(key, mods)) { _ = NewDocumentAsync(); e.Handled = true; }
        else if (sc.FileSave.Matches(key, mods)) { _ = SaveDocumentAsync(); e.Handled = true; }
        else if (sc.FileSaveAs.Matches(key, mods)) { _ = SaveDocumentAsAsync(); e.Handled = true; }
        else if (sc.FileOpen.Matches(key, mods)) { _ = OpenDocumentAsync(); e.Handled = true; }
        else if (sc.LayerNew.Matches(key, mods)) { _canvas.AddLayer(); e.Handled = true; }
        else if (sc.LayerDuplicate.Matches(key, mods)) { _canvas.DuplicateLayer(); e.Handled = true; }
        else if (sc.LayerDelete.Matches(key, mods)) { _canvas.DeleteLayer(); e.Handled = true; }
        else if (sc.LayerMoveUp.Matches(key, mods)) { _canvas.MoveActiveLayer(1); e.Handled = true; }
        else if (sc.LayerMoveDown.Matches(key, mods)) { _canvas.MoveActiveLayer(-1); e.Handled = true; }
        else if (sc.LayerMerge.Matches(key, mods)) { _canvas.MergeDown(_selectedLayerIndices.Count > 1 ? _selectedLayerIndices.OrderBy(x => x).ToList() : null); e.Handled = true; }
        else if (sc.LayerToggleColor.Matches(key, mods)) { ToggleActiveLayerColor(); e.Handled = true; }
        else if (sc.ZoomReset.Matches(key, mods)) { ResetView(); e.Handled = true; }
        else if (sc.ZoomFit.Matches(key, mods)) { SyncCanvasFrameToDocument(fitToViewport: true); e.Handled = true; }
        else if (sc.ZoomIn.Matches(key, mods) || sc.ZoomInAlt.Matches(key, mods))
        { SetZoom(_zoom * sc.ZoomKeyFactor, null); e.Handled = true; }
        else if (sc.ZoomOut.Matches(key, mods))
        { SetZoom(_zoom / sc.ZoomKeyFactor, null); e.Handled = true; }
        else if (sc.RotateReset.Matches(key, mods)) { SetRotation(0); e.Handled = true; }
        else if (sc.RotateLeft.Matches(key, mods)) { SetRotation(_rotation - sc.RotateKeyStep); e.Handled = true; }
        else if (sc.RotateRight.Matches(key, mods)) { SetRotation(_rotation + sc.RotateKeyStep); e.Handled = true; }
        else if (sc.RotateCanvas90Cw.Matches(key, mods))
        { _canvas.RotateCanvas90Clockwise(); SyncCanvasFrameToDocument(false); ClampCanvasPan(); _rulerOverlay?.InvalidateVisual(); e.Handled = true; }
        else if (sc.RotateCanvas90Ccw.Matches(key, mods))
        { _canvas.RotateCanvas90CounterClockwise(); SyncCanvasFrameToDocument(false); ClampCanvasPan(); _rulerOverlay?.InvalidateVisual(); e.Handled = true; }
        else if (sc.RotateCanvas180.Matches(key, mods)) { _canvas.RotateCanvas180(); e.Handled = true; }
        else if (sc.SelectAll.Matches(key, mods)) { _canvas.SelectAll(); e.Handled = true; }
        else if (sc.Deselect.Matches(key, mods)) { _canvas.Deselect(); e.Handled = true; }
        else if (sc.InvertSelect.Matches(key, mods)) { _canvas.InvertSelection(); e.Handled = true; }
        else if (sc.Transform.Matches(key, mods))
        {
            if (_canvas.IsTransformActive) { _canvas.CommitActiveTool(); }
            else
            {
                _canvas.BeginSelectionTransform(
                    _selectedLayerIndices.Count > 1 ? _selectedLayerIndices.ToList() : null);
            }
            e.Handled = true;
        }
        else if (key == Key.Back && mods == KeyModifiers.None)
        {
            if (_canvas.ActiveTool is TransformTool) _canvas.DeleteSelectionTransform();
            else _canvas.ClearSelectionContent();
            e.Handled = true;
        }
        else if (EffectiveAltInvocation().Matches(key, mods))
        {
            if (_canvas.AlternateTool == null)
                _canvas.SetAlternateTool(CreateAlternateEyedropperTool());
            e.Handled = true;
        }
        else if (key == Key.Escape)
        { _canvas.CancelActiveTool(); e.Handled = true; }
        else if ((key == Key.Return || key == Key.Enter) && _canvas.ActiveTool is TransformTool or CompositeTool { CanCommitFromClick: true })
        { _canvas.CommitActiveTool(); e.Handled = true; }
        else if (sc.ColorCycle.Matches(key, mods)) { CycleColor(); e.Handled = true; }
        else if (sc.ColorDefault.Matches(key, mods)) { SetColor(Color.Parse("#111111")); e.Handled = true; }
        else if (sc.Copy.Matches(key, mods)) { _canvas.CopyToClipboard(); e.Handled = true; }
        else if (sc.Paste.Matches(key, mods)) { _ = _canvas.PasteFromOSClipboardAsync(); e.Handled = true; }
        else if (sc.FlipHorizontal.Matches(key, mods)) { _canvas.FlipCanvas(horizontal: true); e.Handled = true; }
        else if (sc.FlipVertical.Matches(key, mods)) { _canvas.FlipCanvas(horizontal: false); e.Handled = true; }
        else if (sc.MirrorHorizontal.Matches(key, mods)) { _canvasFlip.ScaleX = -_canvasFlip.ScaleX; _canvas.FlipX = (int)_canvasFlip.ScaleX; _rulerOverlay?.InvalidateVisual(); _checkerboardOverlay?.InvalidateVisual(); _resizeOverlay?.InvalidateVisual(); ClampCanvasPan(); UpdateStatus(); e.Handled = true; }
        else if (sc.MirrorVertical.Matches(key, mods)) { _canvasFlip.ScaleY = -_canvasFlip.ScaleY; _canvas.FlipY = (int)_canvasFlip.ScaleY; _rulerOverlay?.InvalidateVisual(); _checkerboardOverlay?.InvalidateVisual(); _resizeOverlay?.InvalidateVisual(); ClampCanvasPan(); UpdateStatus(); e.Handled = true; }
        else if (sc.DeleteSelection.Matches(key, mods))
        {
            if (_canvas.ActiveTool is TransformTool) _canvas.DeleteSelectionTransform();
            else _canvas.ClearSelectionContent();
            e.Handled = true;
        }
        else if (sc.OpenSettings.Matches(key, mods)) { OpenSettings(); e.Handled = true; }
        else if (sc.OpenBrushEditor.Matches(key, mods)) { OpenToolProperties(); e.Handled = true; }
        else if (sc.BrushSizeDecrease.Matches(key, mods))
        {
            NudgeBrushSize(-1, large: false);
            e.Handled = true;
        }
        else if (sc.BrushSizeIncrease.Matches(key, mods))
        {
            NudgeBrushSize(1, large: false);
            e.Handled = true;
        }
        else if (sc.BrushSizeDecreaseLarge.Matches(key, mods))
        {
            NudgeBrushSize(-1, large: true);
            e.Handled = true;
        }
        else if (sc.BrushSizeIncreaseLarge.Matches(key, mods))
        {
            NudgeBrushSize(1, large: true);
            e.Handled = true;
        }
        else if (sc.BrushOpacityDecrease.Matches(key, mods))
        {
            _opacitySlider.Value = Math.Max(_opacitySlider.Minimum, _opacitySlider.Value - sc.BrushOpacityStep);
            e.Handled = true;
        }
        else if (sc.BrushOpacityIncrease.Matches(key, mods))
        {
            _opacitySlider.Value = Math.Min(_opacitySlider.Maximum, _opacitySlider.Value + sc.BrushOpacityStep);
            e.Handled = true;
        }

        if (!e.Handled)
        {
            // Collect all groups that share this shortcut
            var matching = _toolGroupButtons
                .Where(p => !p.Group.Shortcut.IsEmpty && p.Group.Shortcut.Matches(key, mods))
                .ToList();

            if (matching.Count > 0)
            {
                var activeIdx = matching.FindIndex(p => p.Group == _activeToolGroup);
                // Single group already active → do nothing; otherwise advance to next group
                if (!(matching.Count == 1 && activeIdx >= 0))
                {
                    var next = matching[(activeIdx + 1) % matching.Count];
                    var preset = next.Group.ActivePreset ?? next.Group.Presets.FirstOrDefault();
                    if (preset != null) ActivatePreset(next.Group, preset);
                }
                e.Handled = true;
            }
        }
    }

    private void OnKeyUp(object? sender, KeyEventArgs e)
    {
        var mods = Floss.App.Input.KeyBinding.ModifiersAfterKeyUp(e.Key, e.KeyModifiers);
        if (_activeGesture != GestureMode.None &&
            (_gestureKey == Key.None
                ? (mods & _gestureModifiers) != _gestureModifiers
                : e.Key == _gestureKey))
        {
            var wasBrushSizeGesture = _activeGesture == GestureMode.BrushSize;
            _activeGesture = GestureMode.None;
            _gestureKey = Key.None;
            _gestureModifiers = KeyModifiers.None;
            _isPanning = false;
            if (wasBrushSizeGesture)
                FinishActiveToolSizeEdit();
            _canvas.UnlockCursorPreview();
            _canvas.PaintInputSuspended = false;
            Cursor = Cursor.Default;
            e.Handled = true;

            // Restore alternate tool if it was active before the gesture
            if (_altToolBeforeGesture != null)
            {
                _canvas.SetAlternateTool(_altToolBeforeGesture);
                _altToolBeforeGesture = null;
            }
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

        var altInvocation = EffectiveAltInvocation();
        if ((altInvocation.Key != Key.None && e.Key == altInvocation.Key) ||
            (altInvocation.IsModifierOnly && (mods & altInvocation.Modifiers) != altInvocation.Modifiers))
        {
            _canvas.SetAlternateTool(null);
            e.Handled = true;
        }
    }

    private static (GestureMode Mode, Floss.App.Input.KeyBinding? Binding) DetectGesture(Key key, KeyModifiers mods, ShortcutsConfig sc)
    {
        if (sc.GesturePan.Matches(key, mods)) return (GestureMode.Pan, sc.GesturePan);
        if (sc.GestureZoom.Matches(key, mods)) return (GestureMode.Zoom, sc.GestureZoom);
        if (sc.GestureRotate.Matches(key, mods)) return (GestureMode.Rotate, sc.GestureRotate);
        if (sc.GestureBrushSize.Matches(key, mods)) return (GestureMode.BrushSize, sc.GestureBrushSize);
        return (GestureMode.None, null);
    }

    private void BeginGesture(GestureMode gesture, Key key, Floss.App.Input.KeyBinding? binding)
    {
        _activeGesture = gesture;
        _gestureKey = binding?.IsModifierOnly == true ? Key.None : key;
        _gestureModifiers = binding?.Modifiers ?? KeyModifiers.None;
        _canvas.PaintInputSuspended = true;
        // Clear alternate tool during gestures so brush-size/zoom don't color-pick
        _altToolBeforeGesture = _canvas.AlternateTool;
        _canvas.SetAlternateTool(null);
        Cursor = gesture switch
        {
            GestureMode.Pan => CursorPan,
            GestureMode.BrushSize => CursorNone,
            _ => CursorArrow
        };
    }

    private void ResetTransientInputState()
    {
        if (_activeGesture != GestureMode.None || _canvas.AlternateTool != null || _altToolBeforeGesture != null)
        {
            var wasBrushSizeGesture = _activeGesture == GestureMode.BrushSize;
            _activeGesture = GestureMode.None;
            _gestureKey = Key.None;
            _gestureModifiers = KeyModifiers.None;
            _isPanning = false;
            if (wasBrushSizeGesture)
                FinishActiveToolSizeEdit();
            _canvas.PaintInputSuspended = false;
            _canvas.UnlockCursorPreview();
            _canvas.SetAlternateTool(null);
            _altToolBeforeGesture = null;
            Cursor = Cursor.Default;
        }
    }

    private ITool CreateAlternateEyedropperTool()
    {
        var preset = CurrentEyedropperPreset();
        var output = new EyedropperOutput();
        if (preset != null)
        {
            output.SampleMode = preset.EyedropperSampleMode;
            output.ExcludeLockedLayers = preset.EyedropperExcludeLockedLayers;
            output.ExcludeReferenceLayers = preset.EyedropperExcludeReferenceLayers;
        }
        return new CompositeTool(new ClickInputProcess(), output);
    }

    private ToolPreset? CurrentEyedropperPreset()
    {
        if (_activeToolGroup?.ActivePreset?.OutputProcess == OutputProcessType.Eyedropper)
            return _activeToolGroup.ActivePreset;

        return App.ToolGroups.Groups
            .SelectMany(g => g.Presets)
            .FirstOrDefault(p => p.OutputProcess == OutputProcessType.Eyedropper);
    }

    private Floss.App.Input.KeyBinding EffectiveAltInvocation()
    {
        var preset = _activeToolGroup?.ActivePreset?.AlternateInvocation;
        if (preset != null && !preset.IsEmpty) return preset;
        return App.Shortcuts.AlternateInvocation;
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null) { _settingsWindow.Activate(); return; }
        _settingsWindow = new SettingsWindow();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show(this);
    }
}
