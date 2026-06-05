using System;
using System.Collections.Generic;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Processes.Output;
using Floss.App.SmartShape;

namespace Floss.App.Processes.Input;

/// <summary>
/// Hybrid brush input: normal stroke until hold-still triggers smart-shape fitting (CSP-style).
/// Fitted stroke preview uses offscreen bitmap (Ctrl+T pattern), not live layer writes.
/// </summary>
public sealed class SmartShapeBrushInputProcess : IInputProcess
{
    private readonly BrushStrokeInputProcess _brush = new();
    private SmartShapeBrushOutput? _output;
    private SmartShapePhase _phase = SmartShapePhase.Idle;
    private SmartShapeModel? _baseShape;
    private SmartShapeModel? _currentShape;
    private List<Vec2> _rawStroke = [];
    private List<double> _pressures = [];
    private bool _strokeClosed;
    private SmartShapeFitKind _activeFitKind = SmartShapeFitKind.Auto;
    private double _adjustRefDist = 1;
    private double _adjustRefAngle;
    private Vec2 _lastMovePos;
    private long _stillSinceMicros;
    private DispatcherTimer? _holdTimer;
    private SmartShapeCommitInput? _commitResult;
    private CanvasInputSample? _lastKnownPos;
    private IReadOnlyList<GizmoHandle> _gizmoHandles = [];
    private GizmoHandle? _activeGizmoHandle;
    private Vec2 _gizmoDragStart;
    private SmartShapeModel? _gizmoShapeAtDragStart;

    public bool HasBrushCursor => true;

    public Action? InvalidateUi { get; set; }
    public Action? PhaseChanged { get; set; }
    public double Stabilization { get => _brush.Stabilization; set => _brush.Stabilization = value; }
    public bool SpeedAdaptiveStabilizer { get => _brush.SpeedAdaptiveStabilizer; set => _brush.SpeedAdaptiveStabilizer = value; }
    public double BrushSize { get => _brush.BrushSize; set => _brush.BrushSize = value; }
    public ToolAuxOperationType ToolAuxMode { get => _brush.ToolAuxMode; set => _brush.ToolAuxMode = value; }

    public SmartShapePhase Phase => _phase;
    public SmartShapeModel? CurrentShape => _currentShape;
    public SmartShapeFitKind ActiveFitKind => _activeFitKind;
    public bool StrokeClosed => _strokeClosed;
    public bool ShiftConstrain { get; set; }

    public bool HasPendingSmartShape =>
        _phase is SmartShapePhase.Adjusting or SmartShapePhase.Launcher or SmartShapePhase.Gizmo;
    public bool SmartShapeCaptured => HasPendingSmartShape;

    public void BindOutput(SmartShapeBrushOutput output) => _output = output;

    private bool IsEnabled =>
        App.Config.SmartShapeEnabled &&
        ToolAuxMode != ToolAuxOperationType.StraightLine;

    public bool IsActive => _phase != SmartShapePhase.Idle || _brush.IsActive;

    public double LastCanvasZoom { get; set; } = 1.0;

    public void PointerDown(CanvasInputSample s)
    {
        _commitResult = null;
        _lastKnownPos = s;

        if (_phase == SmartShapePhase.Launcher)
            return;

        if (_phase == SmartShapePhase.Gizmo && _currentShape != null)
        {
            _gizmoHandles = SmartShapeGizmo.ComputeHandles(_currentShape);
            var pos = new Vec2(s.X, s.Y);
            if (SmartShapeGizmo.HitTest(_gizmoHandles, pos, LastCanvasZoom) is { } hit)
            {
                _activeGizmoHandle = hit;
                _gizmoDragStart = pos;
                _gizmoShapeAtDragStart = _currentShape;
                return;
            }

            if (SmartShapeGizmo.BboxContains(_gizmoHandles, pos, LastCanvasZoom))
            {
                _activeGizmoHandle = new GizmoHandle(GizmoHandleKind.Move, SmartShapeAnalyzer.ShapeCenter(_currentShape));
                _gizmoDragStart = pos;
                _gizmoShapeAtDragStart = _currentShape;
                return;
            }

            Commit();
            return;
        }

        if (!IsEnabled)
        {
            _brush.PointerDown(s);
            return;
        }

        _phase = SmartShapePhase.Drawing;
        _rawStroke = [new Vec2(s.X, s.Y)];
        _pressures = [s.Pressure];
        _lastMovePos = new Vec2(s.X, s.Y);
        _stillSinceMicros = s.TimeMicros;
        _brush.PointerDown(s);
        StartHoldTimer();
        NotifyPhaseChanged();
    }

    public void PointerMove(CanvasInputSample s)
    {
        _lastKnownPos = s;

        if (_phase == SmartShapePhase.Gizmo && _activeGizmoHandle is { } handle && _currentShape != null && _gizmoShapeAtDragStart != null)
        {
            var pos = new Vec2(s.X, s.Y);
            _currentShape = SmartShapeGizmo.ApplyDrag(
                _currentShape,
                handle,
                _gizmoDragStart,
                pos,
                _gizmoShapeAtDragStart);
            if (ShiftConstrain)
                _currentShape = SmartShapeRegular.Constrain(_currentShape);
            InvalidateUi?.Invoke();
            return;
        }

        if (!IsEnabled || _phase == SmartShapePhase.Idle)
        {
            _brush.PointerMove(s);
            return;
        }

        if (_phase == SmartShapePhase.Drawing)
        {
            _rawStroke.Add(new Vec2(s.X, s.Y));
            _pressures.Add(s.Pressure);
            var dx = s.X - _lastMovePos.X;
            var dy = s.Y - _lastMovePos.Y;
            if (Math.Sqrt(dx * dx + dy * dy) >= App.Config.SmartShapeHoldRadiusPx)
            {
                _lastMovePos = new Vec2(s.X, s.Y);
                _stillSinceMicros = s.TimeMicros;
            }
            _brush.PointerMove(s);
            return;
        }

        if (_phase == SmartShapePhase.Adjusting)
            UpdateAdjustment(s);
    }

    public void PointerUp(CanvasInputSample s)
    {
        _lastKnownPos = s;
        StopHoldTimer();

        if (_phase == SmartShapePhase.Gizmo)
        {
            _activeGizmoHandle = null;
            _gizmoShapeAtDragStart = null;
            return;
        }

        if (!IsEnabled || _phase == SmartShapePhase.Idle)
        {
            _brush.PointerUp(s);
            return;
        }

        if (_phase == SmartShapePhase.Drawing)
        {
            _brush.PointerUp(s);
            ResetShapeState();
            return;
        }

        if (_phase == SmartShapePhase.Adjusting && _currentShape != null)
        {
            if (App.Config.SmartShapeShowLauncher)
            {
                _phase = SmartShapePhase.Launcher;
                _baseShape = _currentShape;
                NotifyPhaseChanged();
                InvalidateUi?.Invoke();
            }
            else
                EnterGizmoPhase();
        }
    }

    public void EnterGizmoEdit()
    {
        if (_phase != SmartShapePhase.Launcher || _currentShape == null)
            return;

        EnterGizmoPhase();
    }

    private void EnterGizmoPhase()
    {
        if (_currentShape == null)
            return;

        _phase = SmartShapePhase.Gizmo;
        _gizmoHandles = SmartShapeGizmo.ComputeHandles(_currentShape);
        _baseShape = _currentShape;
        NotifyPhaseChanged();
        InvalidateUi?.Invoke();
    }

    public void Refit(SmartShapeFitKind kind)
    {
        if (_rawStroke.Count < 4 || _phase is SmartShapePhase.Idle or SmartShapePhase.Drawing)
            return;

        var shape = SmartShapeFitter.Fit(_rawStroke, kind);
        if (shape == null)
            return;

        _activeFitKind = kind;
        _baseShape = shape;
        _currentShape = shape;
        if (_phase == SmartShapePhase.Gizmo)
            _gizmoHandles = SmartShapeGizmo.ComputeHandles(shape);
        NotifyPhaseChanged();
        InvalidateUi?.Invoke();
    }

    public void Cancel()
    {
        StopHoldTimer();
        ResetShapeState();
        _brush.Cancel();
    }

    public void Commit()
    {
        if (_currentShape == null)
            return;
        if (_phase is not (SmartShapePhase.Launcher or SmartShapePhase.Gizmo))
            return;

        var result = new SmartShapeCommitInput
        {
            Shape = _currentShape,
            AvgPressure = AveragePressure()
        };
        ResetShapeState();
        _commitResult = result;
    }

    public IProcessedInput? GetResult()
    {
        if (_commitResult is { } commit)
        {
            _commitResult = null;
            return commit;
        }
        if (_phase != SmartShapePhase.Idle)
            return null;
        return _brush.GetResult();
    }

    public IProcessedInput? GetPreview()
    {
        if (_phase is SmartShapePhase.Adjusting or SmartShapePhase.Launcher or SmartShapePhase.Gizmo && _currentShape != null)
        {
            return new SmartShapeCommitInput
            {
                Shape = _currentShape,
                AvgPressure = AveragePressure()
            };
        }

        return _brush.GetPreview();
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
    {
        if (_phase == SmartShapePhase.Gizmo && _currentShape != null)
        {
            _gizmoHandles = SmartShapeGizmo.ComputeHandles(_currentShape);
            SmartShapeOverlay.Draw(dc, zoom, _currentShape, SmartShapeOverlayStyle.Edit, _gizmoHandles);
            return;
        }

        _brush.RenderOverlay(dc, zoom);
    }

    private void StartHoldTimer()
    {
        StopHoldTimer();
        _holdTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _holdTimer.Tick += (_, _) => TryDetectHold();
        _holdTimer.Start();
    }

    private void StopHoldTimer()
    {
        if (_holdTimer == null)
            return;
        _holdTimer.Stop();
        _holdTimer = null;
    }

    private void TryDetectHold()
    {
        if (_phase != SmartShapePhase.Drawing || _rawStroke.Count < 4)
            return;

        var holdMicros = (long)(App.Config.SmartShapeHoldSeconds * 1_000_000);
        if (_lastKnownPos is not { } pos)
            return;

        var dx = pos.X - _lastMovePos.X;
        var dy = pos.Y - _lastMovePos.Y;
        if (Math.Sqrt(dx * dx + dy * dy) >= App.Config.SmartShapeHoldRadiusPx)
            return;

        if (pos.TimeMicros - _stillSinceMicros < holdMicros)
            return;

        _strokeClosed = SmartShapeFitter.StrokeIsClosed(_rawStroke);
        var shape = SmartShapeFitter.Fit(_rawStroke, SmartShapeFitKind.Auto);
        if (shape == null)
            return;

        _activeFitKind = SmartShapeFitter.DetectFitKind(shape, _strokeClosed);
        EnterAdjusting(shape, pos);
    }

    private void EnterAdjusting(SmartShapeModel shape, CanvasInputSample pos)
    {
        StopHoldTimer();
        _output?.AbortLiveStroke();
        _brush.Cancel();

        _phase = SmartShapePhase.Adjusting;
        _baseShape = shape;
        _currentShape = shape;

        var center = SmartShapeAnalyzer.ShapeCenter(shape);
        var dx = pos.X - center.X;
        var dy = pos.Y - center.Y;
        _adjustRefDist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1.0);
        _adjustRefAngle = Math.Atan2(dy, dx);
        NotifyPhaseChanged();
        InvalidateUi?.Invoke();
    }

    private void UpdateAdjustment(CanvasInputSample pos)
    {
        if (_baseShape == null)
            return;

        var center = SmartShapeAnalyzer.ShapeCenter(_baseShape);
        var dx = pos.X - center.X;
        var dy = pos.Y - center.Y;
        var dist = Math.Max(Math.Sqrt(dx * dx + dy * dy), 1.0);
        var scale = dist / _adjustRefDist;
        var rotDeg = (Math.Atan2(dy, dx) - _adjustRefAngle) * (180.0 / Math.PI);
        var transformed = SmartShapeTransforms.Transform(_baseShape, center.X, center.Y, scale, rotDeg);
        _currentShape = ShiftConstrain ? SmartShapeRegular.Constrain(transformed) : transformed;
        InvalidateUi?.Invoke();
    }

    private void ResetShapeState()
    {
        StopHoldTimer();
        _phase = SmartShapePhase.Idle;
        _commitResult = null;
        _rawStroke.Clear();
        _pressures.Clear();
        _strokeClosed = false;
        _activeFitKind = SmartShapeFitKind.Auto;
        _baseShape = null;
        _currentShape = null;
        _gizmoHandles = [];
        _activeGizmoHandle = null;
        _gizmoShapeAtDragStart = null;
        NotifyPhaseChanged();
    }

    private void NotifyPhaseChanged() => PhaseChanged?.Invoke();

    private double AveragePressure()
    {
        if (_pressures.Count == 0)
            return 1.0;
        var sum = 0.0;
        foreach (var p in _pressures)
            sum += p;
        return sum / _pressures.Count;
    }
}
