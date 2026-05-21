using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Floss.App.Processes;
using Floss.App.Processes.Output;
using Floss.App.Tools;

namespace Floss.App.Input;

/// <summary>
/// Central state machine for canvas input.
///
/// Owns all transient input state (held keys, held buttons, active pointer, ready/running action)
/// and provides a clean lifecycle: ActivateReady / DeactivateReady / Begin / Update / End / Cancel.
///
/// <see cref="MainWindow.Viewport"/> forwards pointer and key events here; this class delegates
/// side effects back through <see cref="ICanvasInputHost"/>.
/// </summary>
public enum RouterState
{
    Idle,
    Ready,
    Running,
    Suppressed,
}

public interface ICanvasInputHost
{
    // ── Coordinate helpers ──
    PointerPoint GetViewportPointerPoint(PointerEventArgs e);
    PointerPoint GetCanvasPointerPoint(PointerEventArgs e);
    Point GetViewportPosition(PointerEventArgs e);
    Point GetCanvasPosition(PointerEventArgs e);

    // ── Tool dispatch ──
    void DispatchViewportPointerInput(ToolInputEventKind kind, Point viewportPos, PointerPoint point);
    void DispatchPointerInput(ToolInputEventKind kind, PointerPoint canvasPoint);

    // ── Temporary preset / tool switching ──
    bool PushTemporaryPreset(string presetId);
    void PopTemporaryPreset();
    void SetAlternateActive(bool active);
    bool IsAlternateActive { get; }

    // ── Canvas modifier state ──
    void SetCanvasModifiers(KeyModifiers mods);
    void SetToolAuxMode(ToolAuxOperationType mode);

    // ── Tool lifecycle ──
    void CancelActiveTool();
    void CommitActiveTool();
    ITool? ActiveTool { get; }

    // ── Layer pick ──
    bool IsLayerPickDrag { get; }
    void StartLayerPickDrag(Point pos);
    void UpdateLayerPickDrag(Point pos);
    void EndLayerPickDrag(Point pos);

    // ── Cursor preview ──
    void LockCursorPreview(Point center, bool forceBrushOutline);
    void UnlockCursorPreview();

    // ── Brush size gesture ──
    double GetActiveToolSize();
    double GetActiveToolSizeMin();
    double GetActiveToolSizeMax();
    void SetActiveToolSize(double size);
    void FinishActiveToolSizeEdit();

    // ── Viewport ──
    double Zoom { get; }
    IViewportController? ViewportController { get; }
    void InvalidateViewport();

    // ── Resize overlay ──
    bool TryBeginResizeDrag(Point pos, bool isPrimary);
    bool IsResizeDragging { get; }
    void EndResizeDrag();
    void UpdateResizeDrag(Point pt);

    // ── Pointer capture ──
    void CapturePointer(IPointer pointer);
    void ReleasePointerCapture();

    // ── Paint suspension ──
    bool PaintInputSuspended { get; set; }

    // ── Cursor ──
    void SetCursorNone();
    void ResetCursor();

    // ── UI overlap ──
    bool IsOverCanvasUi(Point viewportPos);

    // ── Tool type info for modifier resolution ──
    (int InputProcessType, int OutputProcessType) GetActiveToolTypes();
}

/// <summary>
/// State machine for canvas input. Manages the transition between Idle/Ready/Running/Suppressed
/// and dispatches the appropriate lifecycle methods to the host.
///
/// Public event-handler methods take Avalonia event args. For testing, call the
/// Handle* methods directly with resolved values.
/// </summary>
public sealed class CanvasInputRouter
{
    private const double PenPressureThreshold = 0.02;

    private readonly ICanvasInputHost _host;

    // ── State machine ──
    private RouterState _state = RouterState.Idle;
    private CanvasAction _runningAction;
    private long _activePointerId = -1;
    private bool _canvasButtonPresetActive;

    // ── Physical input tracking ──
    private KeyModifiers _heldModifiers;
    private readonly HashSet<Key> _heldBaseKeys = [];
    private Point _lastViewportPos;

    // ── Ready / modifier state (only meaningful when not Running) ──
    private CanvasAction _readyAction;
    private ModifierAction _activeModifierAction;
    private KeyModifiers _activeModifierCombo;
    private Key? _activeModifierKey;
    private string? _activeModifierPresetId;
    private bool _modifierTemporaryPresetActive;
    private bool _modifierAlternateActive;

    // ── Brush-size gesture state ──
    private bool _brushSizeActive;
    private Point _brushSizeGestureStartCanvasPoint;
    private Point _brushSizeGestureCenterCanvasPoint;
    private double _brushSizeGestureStartSize;
    private bool _brushSizeGestureHasCenter;
    private double _brushSizeLastDirX;
    private double _brushSizeLastDirY;
    private bool _brushSizeHasLastDir;
    private bool _hadAlternateBeforeBrushSize;

    public CanvasInputRouter(ICanvasInputHost host)
    {
        _host = host;
    }

    // ── Public state queries ──

    public RouterState State => _state;
    public CanvasAction RunningAction => _runningAction;
    public CanvasAction ReadyAction => _readyAction;
    public bool IsTransactionActive => _state == RouterState.Running;

    // ── Pointer event handlers (thin wrappers extracting data from event args) ──

    public void PointerPressed(PointerPressedEventArgs e)
    {
        if (_state == RouterState.Suppressed) return;
        if (_state == RouterState.Running)
        {
            e.Handled = true;
            return;
        }

        var pt = _host.GetViewportPointerPoint(e);
        var action = ResolveButtonAction(pt);
        var isPrimaryDown = action == CanvasAction.PrimaryTool;

        HandlePointerPress(
            action, isPrimaryDown, pt.Pointer.Id, pt.Position,
            e, e.KeyModifiers.HasFlag(KeyModifiers.Control),
            e.KeyModifiers.HasFlag(KeyModifiers.Shift));
    }

    public void PointerMoved(PointerEventArgs e)
    {
        if (_state != RouterState.Running) return;
        var pt = _host.GetViewportPosition(e);
        HandlePointerMove(e, pt);
    }

    public void PointerReleased(PointerReleasedEventArgs e)
    {
        if (_state != RouterState.Running) return;
        if (e.Pointer.Id != _activePointerId)
            return;
        HandlePointerRelease(e);
    }

    public void CaptureLost()
    {
        if (_state != RouterState.Running) return;
        HandleCaptureLost();
    }

    // ── Core state machine (testable directly) ──

    /// <summary>
    /// Handle a pointer press event with resolved values.
    /// Does NOT handle resize, layer-pick, brush-size-gesture — those are checked
    /// before calling this in the normal flow.
    /// </summary>
    public void HandlePointerPress(
        CanvasAction action,
        bool isPrimaryDown,
        long pointerId,
        Point viewportPos,
        object? eventArgs,
        bool ctrlHeld,
        bool shiftHeld)
    {
        if (_state == RouterState.Running)
            return;

        // Resize overlay
        if (_host.TryBeginResizeDrag(viewportPos, isPrimaryDown))
        {
            EnterRunning(action, pointerId, false);
            _lastViewportPos = viewportPos;
            if (eventArgs is PointerPressedEventArgs e)
            {
                _host.CapturePointer(e.Pointer);
                e.Handled = true;
            }
            return;
        }

        // Don't dispatch or capture when clicking a UI overlay
        // (resize panel buttons, selection action bar, etc.)
        if (_host.IsOverCanvasUi(viewportPos))
            return;

        var isPointerActivation = IsPointerActivation(isPrimaryDown, eventArgs);

        // Brush-size gesture mode
        if (_activeModifierAction == ModifierAction.ChangeBrushSize)
        {
            if (!isPointerActivation) return;
            if (eventArgs is PointerPressedEventArgs e)
                BeginBrushSizeGesture(viewportPos, pointerId, e);
            return;
        }

        // Primary tool dispatch
        if (isPrimaryDown || (_activeModifierAction != ModifierAction.None && isPointerActivation))
        {
            if (eventArgs != null)
            {
                var e = (PointerPressedEventArgs)eventArgs;
                if (IsViewportTool())
                    _host.DispatchViewportPointerInput(ToolInputEventKind.Down, viewportPos, _host.GetViewportPointerPoint(e));
                else
                {
                    _host.DispatchPointerInput(ToolInputEventKind.Down, _host.GetCanvasPointerPoint(e));
                    _host.SetCursorNone();
                }
                _host.CapturePointer(e.Pointer);
                e.Handled = true;
            }
            EnterRunning(CanvasAction.PrimaryTool, pointerId, false);
            return;
        }

        // Canvas button action
        if (action != CanvasAction.None)
            BeginCanvasButtonAction(action, viewportPos, pointerId, eventArgs);
    }

    public void HandlePointerMove(PointerEventArgs e, Point viewportPos)
    {
        // Layer-pick drag
        if (_host.IsLayerPickDrag)
        {
            _host.UpdateLayerPickDrag(viewportPos);
            e.Handled = true;
            return;
        }

        // Resize drag
        if (_host.IsResizeDragging)
        {
            _host.UpdateResizeDrag(viewportPos);
            e.Handled = true;
            return;
        }

        // Brush size gesture
        if (_brushSizeActive)
        {
            UpdateBrushSizeGesture(e, viewportPos);
            return;
        }

        // Dispatch based on the currently active tool, not _runningAction.
        // Space+click pans with the Hand tool even though _runningAction is PrimaryTool.
        if (IsViewportTool())
        {
            _host.DispatchViewportPointerInput(ToolInputEventKind.Move, viewportPos, _host.GetViewportPointerPoint(e));
        }
        else
        {
            _host.DispatchPointerInput(ToolInputEventKind.Move, _host.GetCanvasPointerPoint(e));
        }
        _lastViewportPos = viewportPos;
        e.Handled = true;
    }

    public void HandlePointerRelease(object? eventArgs)
    {
        if (_host.IsResizeDragging)
            _host.EndResizeDrag();

        if (_runningAction == CanvasAction.LayerPick)
        {
            if (eventArgs is PointerReleasedEventArgs e)
            {
                var pt = _host.GetViewportPosition(e);
                _host.EndLayerPickDrag(pt);
            }
        }
        else if (_runningAction == CanvasAction.BrushSize)
        {
            _brushSizeActive = false;
            _host.FinishActiveToolSizeEdit();
            _host.UnlockCursorPreview();
        }
        else
        {
            // Dispatch Up based on the currently active tool, not _runningAction.
            if (IsViewportTool())
            {
                if (eventArgs is PointerReleasedEventArgs e)
                {
                    var pt = _host.GetViewportPointerPoint(e);
                    _host.DispatchViewportPointerInput(ToolInputEventKind.Up, pt.Position, pt);
                }
            }
            else if (eventArgs is PointerReleasedEventArgs e)
            {
                _host.DispatchPointerInput(ToolInputEventKind.Up, _host.GetCanvasPointerPoint(e));
            }
            PopTemporaryPresetIfNeeded();
        }

        ExitRunning(eventArgs);
    }

    public void HandleCaptureLost()
    {
        if (_host.IsResizeDragging) _host.EndResizeDrag();

        // Krita-style forced end: finish the running transaction instead of
        // destructively cancelling it. DirectDrawOutput.Cancel restores
        // pre-stroke tiles, so capture loss during modifier/tool switching must
        // commit the live stroke before any tool switch can deactivate it.
        if (!IsViewportTool() && _runningAction != CanvasAction.LayerPick && _runningAction != CanvasAction.BrushSize)
            _host.CommitActiveTool();

        PopTemporaryPresetIfNeeded();

        if (_runningAction == CanvasAction.BrushSize)
        {
            _brushSizeActive = false;
            _host.FinishActiveToolSizeEdit();
            _host.UnlockCursorPreview();
        }

        _state = RouterState.Idle;
        _runningAction = CanvasAction.None;
        _activePointerId = -1;
        _canvasButtonPresetActive = false;
        _host.ReleasePointerCapture();
        _host.PaintInputSuspended = false;
        _host.ResetCursor();

        ReevaluateModifierState();
    }

    // ── Keyboard event handlers (thin wrappers) ──

    public void KeyDown(KeyEventArgs e)
    {
        HandleKeyDown(e.Key, e.KeyModifiers);
    }

    public void KeyUp(KeyEventArgs e)
    {
        HandleKeyUp(e.Key, e.KeyModifiers);
    }

    public void HandleKeyDown(Key key, KeyModifiers modifiers)
    {
        var mods = KeyBinding.ModifiersWithKeyDown(key, modifiers);
        _heldModifiers = mods;
        _host.SetCanvasModifiers(mods);

        if (!IsModifierKey(key))
            _heldBaseKeys.Add(key);

        ReevaluateModifierState();
    }

    public void HandleKeyUp(Key key, KeyModifiers modifiers)
    {
        _heldBaseKeys.Remove(key);
        var mods = KeyBinding.ModifiersAfterKeyUp(key, modifiers);
        _heldModifiers = mods;
        _host.SetCanvasModifiers(mods);

        ReevaluateModifierState();
    }

    // ── Reset / Suppression ──

    public void EnterSuppressed()
    {
        if (_state == RouterState.Running)
            HandleCaptureLost();
        _state = RouterState.Suppressed;
        ClearModifierState();
    }

    public void ExitSuppressed()
    {
        _state = RouterState.Idle;
    }

    public void ResetAllState()
    {
        if (_state == RouterState.Running)
            HandleCaptureLost();

        _heldBaseKeys.Clear();
        _heldModifiers = KeyModifiers.None;
        _brushSizeActive = false;
        _brushSizeGestureHasCenter = false;
        _brushSizeHasLastDir = false;
        _hadAlternateBeforeBrushSize = false;
        ClearModifierState();
        _state = RouterState.Idle;
        _runningAction = CanvasAction.None;
        _readyAction = CanvasAction.None;
        _activePointerId = -1;
        _canvasButtonPresetActive = false;
        _host.PaintInputSuspended = false;
        _host.ResetCursor();
        _host.ReleasePointerCapture();
    }

    // ── Private: action dispatch ──

    private void BeginCanvasButtonAction(CanvasAction action, Point viewportPos, long pointerId, object? eventArgs)
    {
        var presetId = PresetIdForAction(action);
        if (presetId == null) return;

        if (!_host.PushTemporaryPreset(presetId)) return;

        if (eventArgs != null && IsViewportTool())
            _host.DispatchViewportPointerInput(ToolInputEventKind.Down, viewportPos, _host.GetViewportPointerPoint((PointerPressedEventArgs)eventArgs));
        else if (eventArgs != null)
            _host.DispatchPointerInput(ToolInputEventKind.Down, _host.GetCanvasPointerPoint((PointerPressedEventArgs)eventArgs));

        EnterRunning(action, pointerId, isTemporaryPreset: true);
        if (eventArgs is PointerPressedEventArgs e)
        {
            _host.CapturePointer(e.Pointer);
            e.Handled = true;
        }
    }

    private void BeginBrushSizeGesture(Point viewportPos, long pointerId, PointerPressedEventArgs e)
    {
        _brushSizeActive = true;
        _host.PaintInputSuspended = true;
        _hadAlternateBeforeBrushSize = _host.IsAlternateActive;
        _host.SetAlternateActive(false);
        _brushSizeGestureStartCanvasPoint = _host.GetCanvasPosition(e);
        _brushSizeGestureStartSize = _host.GetActiveToolSize();

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

        _host.LockCursorPreview(_brushSizeGestureCenterCanvasPoint, forceBrushOutline: true);
        EnterRunning(CanvasAction.BrushSize, pointerId, false);
        _host.CapturePointer(e.Pointer);
        e.Handled = true;
    }

    private void UpdateBrushSizeGesture(PointerEventArgs e, Point viewportPt)
    {
        var canvasPoint = _host.GetCanvasPosition(e);
        if (!_brushSizeGestureHasCenter)
        {
            var startDx = canvasPoint.X - _brushSizeGestureStartCanvasPoint.X;
            var startDy = canvasPoint.Y - _brushSizeGestureStartCanvasPoint.Y;
            var startDistance = Math.Sqrt(startDx * startDx + startDy * startDy);
            if (startDistance > 10.0 / _host.Zoom)
            {
                var startRadius = _brushSizeGestureStartSize * 0.5;
                _brushSizeGestureCenterCanvasPoint = new Point(
                    _brushSizeGestureStartCanvasPoint.X - startDx / startDistance * startRadius,
                    _brushSizeGestureStartCanvasPoint.Y - startDy / startDistance * startRadius);
                _brushSizeGestureHasCenter = true;
            }
            else
            {
                _lastViewportPos = viewportPt;
                _host.LockCursorPreview(_brushSizeGestureCenterCanvasPoint, forceBrushOutline: true);
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

        _host.SetActiveToolSize(BrushSizeAdjustment.FromRadiusDistance(
            radiusDistance,
            _host.GetActiveToolSizeMin(),
            _host.GetActiveToolSizeMax()));
        _lastViewportPos = viewportPt;
        _host.LockCursorPreview(_brushSizeGestureCenterCanvasPoint, forceBrushOutline: true);
        e.Handled = true;
    }

    // ── Private: state transitions ──

    private void EnterRunning(CanvasAction action, long pointerId, bool isTemporaryPreset)
    {
        if (_readyAction != CanvasAction.None)
        {
            DeactivateReady(_readyAction);
            _readyAction = CanvasAction.None;
        }
        _state = RouterState.Running;
        _runningAction = action;
        _activePointerId = pointerId;
        _canvasButtonPresetActive = isTemporaryPreset;
    }

    private void ExitRunning(object? eventArgs)
    {
        _state = RouterState.Idle;
        _runningAction = CanvasAction.None;
        _activePointerId = -1;
        _canvasButtonPresetActive = false;
        _host.ReleasePointerCapture();
        _host.PaintInputSuspended = false;
        _host.ResetCursor();
        if (eventArgs is PointerReleasedEventArgs e)
            e.Handled = true;

        ReevaluateModifierState();
    }

    private void PopTemporaryPresetIfNeeded()
    {
        if (_canvasButtonPresetActive)
        {
            _canvasButtonPresetActive = false;
            _host.PopTemporaryPreset();
        }
    }

    // ── Private: modifier state ──

    private void ClearModifierState()
    {
        switch (_activeModifierAction)
        {
            case ModifierAction.ToolAux:
                _host.SetToolAuxMode(ToolAuxOperationType.None);
                break;
            case ModifierAction.ChangeToolTemporarily:
                if (_state != RouterState.Running && _modifierTemporaryPresetActive)
                    _host.PopTemporaryPreset();
                if (_modifierAlternateActive)
                    _host.SetAlternateActive(false);
                break;
        }
        _activeModifierAction = ModifierAction.None;
        _activeModifierCombo = KeyModifiers.None;
        _activeModifierKey = null;
        _activeModifierPresetId = null;
        _modifierTemporaryPresetActive = false;
        _modifierAlternateActive = false;
        _readyAction = CanvasAction.None;
    }

    private void ReevaluateModifierState()
    {
        var (inputType, outputType) = _host.GetActiveToolTypes();

        ModifierKeyAssignment? assignment = null;

        foreach (var heldKey in _heldBaseKeys)
        {
            var a = App.ModifierKeys.Resolve(inputType, outputType, heldKey, _heldModifiers);
            if (a != null && a.Key.HasValue) { assignment = a; break; }
        }

        if (assignment == null && _heldBaseKeys.Count == 0)
            assignment = App.ModifierKeys.Resolve(inputType, outputType, null, _heldModifiers);

        // Krita rule: modifier changes do NOT interrupt a running transaction.
        if (_state == RouterState.Running)
            return;

        bool unchanged = (assignment == null && _activeModifierAction == ModifierAction.None)
                      || (assignment != null
                          && assignment.Modifiers == _activeModifierCombo
                          && assignment.Action == _activeModifierAction
                          && assignment.Key == _activeModifierKey);
        if (unchanged)
        {
            UpdateReadyAction();
            return;
        }

        ClearModifierAction();

        _activeModifierAction = assignment?.Action ?? ModifierAction.None;
        _activeModifierCombo = assignment?.Modifiers ?? KeyModifiers.None;
        _activeModifierKey = assignment?.Key;
        _activeModifierPresetId = assignment?.TemporaryToolPresetId;

        if (assignment != null)
            ApplyModifierAction(assignment);

        UpdateReadyAction();
    }

    private void ClearModifierAction()
    {
        switch (_activeModifierAction)
        {
            case ModifierAction.ToolAux:
                _host.SetToolAuxMode(ToolAuxOperationType.None);
                break;
            case ModifierAction.ChangeToolTemporarily:
                if (_modifierTemporaryPresetActive)
                    _host.PopTemporaryPreset();
                if (_modifierAlternateActive)
                    _host.SetAlternateActive(false);
                _modifierTemporaryPresetActive = false;
                _modifierAlternateActive = false;
                break;
            case ModifierAction.ChangeBrushSize:
                if (_brushSizeActive)
                {
                    _brushSizeActive = false;
                    _host.FinishActiveToolSizeEdit();
                    _host.UnlockCursorPreview();
                    _host.PaintInputSuspended = false;
                }
                break;
        }
    }

    private void ApplyModifierAction(ModifierKeyAssignment assignment)
    {
        switch (assignment.Action)
        {
            case ModifierAction.ToolAux:
                _host.SetToolAuxMode(assignment.ToolAuxOper);
                break;
            case ModifierAction.ChangeToolTemporarily:
                if (!string.IsNullOrEmpty(assignment.TemporaryToolPresetId))
                    _modifierTemporaryPresetActive = _host.PushTemporaryPreset(assignment.TemporaryToolPresetId);
                else
                {
                    _host.SetAlternateActive(true);
                    _modifierAlternateActive = true;
                }
                break;
        }
    }

    private void UpdateReadyAction()
    {
        var newReady = ResolveReadyAction();
        if (newReady == _readyAction) return;

        DeactivateReady(_readyAction);
        _readyAction = newReady;
        _state = _readyAction == CanvasAction.None ? RouterState.Idle : RouterState.Ready;
        if (_readyAction != CanvasAction.None)
            ActivateReady(_readyAction);
    }

    private CanvasAction ResolveReadyAction()
    {
        if (_state != RouterState.Idle) return CanvasAction.None;

        return _activeModifierAction switch
        {
            ModifierAction.ChangeToolTemporarily => ResolveToolActionFromPreset(),
            ModifierAction.ChangeBrushSize => CanvasAction.BrushSize,
            _ => CanvasAction.None,
        };
    }

    private CanvasAction ResolveToolActionFromPreset()
    {
        var presetId = _activeModifierKey.HasValue
            ? FindPresetIdForKey(_activeModifierKey.Value, _activeModifierCombo)
            : _activeModifierPresetId;

        return presetId switch
        {
            ToolGroupConfig.ViewHandPresetId => CanvasAction.PanCanvas,
            ToolGroupConfig.ViewRotatePresetId => CanvasAction.RotateCanvas,
            ToolGroupConfig.ViewZoomInPresetId or ToolGroupConfig.ViewZoomOutPresetId => CanvasAction.ZoomCanvas,
            ToolGroupConfig.EyedropperPresetId => CanvasAction.Eyedropper,
            ToolGroupConfig.MoveLayerPresetId => CanvasAction.MoveLayer,
            ToolGroupConfig.SelectLayerPresetId => CanvasAction.LayerPick,
            _ => CanvasAction.PrimaryTool,
        };
    }

    private static string? FindPresetIdForKey(Key key, KeyModifiers mods)
    {
        foreach (var assignment in App.ModifierKeys.GeneralAssignments)
        {
            if (assignment.Key == key && assignment.Modifiers == mods
                && !string.IsNullOrEmpty(assignment.TemporaryToolPresetId))
                return assignment.TemporaryToolPresetId;
        }
        return null;
    }

    private static void ActivateReady(CanvasAction action)
    {
    }

    private static void DeactivateReady(CanvasAction action)
    {
    }

    // ── Private: helpers ──

    private bool IsViewportTool()
    {
        var tool = _host.ActiveTool;
        if (tool is not CompositeTool ct) return false;
        return ct.Output is HandOutput or RotateOutput or ZoomOutput;
    }

    private static bool IsModifierKey(Key key) => key switch
    {
        Key.LeftShift or Key.RightShift or
        Key.LeftCtrl or Key.RightCtrl or
        Key.LeftAlt or Key.RightAlt => true,
        _ => false
    };

    private static CanvasAction ResolveButtonAction(PointerPoint pt)
    {
        var props = pt.Properties;
        if (props.IsEraser || pt.Pointer.Type == PointerType.Pen)
        {
            if (props.Pressure >= PenPressureThreshold)
                return CanvasAction.PrimaryTool;
            if (props.IsMiddleButtonPressed)
                return (CanvasAction)App.Shortcuts.MiddleButtonAction;
            if (props.IsRightButtonPressed)
                return (CanvasAction)App.Shortcuts.RightButtonAction;
            return CanvasAction.None;
        }

        if (pt.Pointer.Type == PointerType.Touch)
            return props.IsLeftButtonPressed || props.Pressure >= PenPressureThreshold
                ? CanvasAction.PrimaryTool
                : CanvasAction.None;

        if (props.IsLeftButtonPressed)
            return CanvasAction.PrimaryTool;
        if (props.IsMiddleButtonPressed)
            return (CanvasAction)App.Shortcuts.MiddleButtonAction;
        if (props.IsRightButtonPressed)
            return (CanvasAction)App.Shortcuts.RightButtonAction;
        return CanvasAction.None;
    }

    private static bool IsPointerActivation(bool isPrimaryDown, object? eventArgs)
    {
        if (isPrimaryDown) return true;
        if (eventArgs is not PointerPressedEventArgs e) return false;

        // Pen/touch press events can arrive before pressure has ramped above
        // the draw threshold. Modifier transactions still need to start from
        // that press; otherwise the next move falls back into the base brush.
        return e.Pointer.Type is PointerType.Pen or PointerType.Touch;
    }

    private static string? PresetIdForAction(CanvasAction action)
    {
        return action switch
        {
            CanvasAction.PanCanvas => ToolGroupConfig.ViewHandPresetId,
            CanvasAction.RotateCanvas => ToolGroupConfig.ViewRotatePresetId,
            CanvasAction.ZoomCanvas => ToolGroupConfig.ViewZoomInPresetId,
            CanvasAction.Eyedropper => ToolGroupConfig.EyedropperPresetId,
            CanvasAction.MoveLayer => ToolGroupConfig.MoveLayerPresetId,
            _ => null
        };
    }
}
