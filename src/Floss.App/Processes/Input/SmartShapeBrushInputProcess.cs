using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Threading;
using Floss.App.Input;
using Floss.App.Processes;
using Floss.App.Processes.Output;
using Floss.App.SmartShape;

namespace Floss.App.Processes.Input;

/// <summary>
/// Hybrid brush input: normal stroke until hold-still auto-fits a shape; release commits (double undo).
/// No gizmo or launcher — hold still to fit, drag to scale/rotate, release to commit.
/// </summary>
public sealed class SmartShapeBrushInputProcess : IInputProcess
{
    private readonly BrushStrokeInputProcess _brush = new();
    private SmartShapeBrushOutput? _output;
    private SmartShapePhase _phase = SmartShapePhase.Idle;
    private SmartShapeModel? _baseShape;
    private SmartShapeModel? _currentShape;
    private List<CanvasInputSample> _rawSamples = [];
    private bool _strokeClosed;
    private SmartShapeFitKind _activeFitKind = SmartShapeFitKind.Auto;
    private double _adjustRefDist = 1;
    private double _adjustRefAngle;
    private Vec2 _lastMovePos;
    private long _stillSinceMicros;
    private DispatcherTimer? _holdTimer;
    private SmartShapeCommitInput? _commitResult;
    private CanvasInputSample? _lastKnownPos;

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

    /// <summary>Pen still down after auto-fit — drag scales/rotates; release commits.</summary>
    public bool HasPendingSmartShape => _phase == SmartShapePhase.Preview;
    public bool SmartShapeCaptured => HasPendingSmartShape;
    public bool ShowsFittedStrokePreview => _phase == SmartShapePhase.Preview;

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

        if (!IsEnabled)
        {
            _brush.PointerDown(s);
            return;
        }

        _phase = SmartShapePhase.Drawing;
        _rawSamples = [s];
        _lastMovePos = new Vec2(s.X, s.Y);
        _stillSinceMicros = s.TimeMicros;
        _brush.PointerDown(s);
        StartHoldTimer();
        NotifyPhaseChanged();
    }

    public void PointerMove(CanvasInputSample s)
    {
        _lastKnownPos = s;

        if (!IsEnabled || _phase == SmartShapePhase.Idle)
        {
            _brush.PointerMove(s);
            return;
        }

        if (_phase == SmartShapePhase.Preview)
        {
            UpdateAdjustment(s);
            return;
        }

        if (_phase == SmartShapePhase.Drawing)
        {
            _rawSamples.Add(s);
            var dx = s.X - _lastMovePos.X;
            var dy = s.Y - _lastMovePos.Y;
            if (Math.Sqrt(dx * dx + dy * dy) >= App.Config.SmartShapeHoldRadiusPx)
            {
                _lastMovePos = new Vec2(s.X, s.Y);
                _stillSinceMicros = s.TimeMicros;
            }
            _brush.PointerMove(s);
        }
    }

    public void PointerUp(CanvasInputSample s)
    {
        _lastKnownPos = s;
        StopHoldTimer();

        if (!IsEnabled || _phase == SmartShapePhase.Idle)
        {
            _brush.PointerUp(s);
            return;
        }

        if (_phase == SmartShapePhase.Preview && _currentShape != null)
        {
            CommitPreview();
            return;
        }

        if (_phase == SmartShapePhase.Drawing)
        {
            _brush.PointerUp(s);
            ResetShapeState();
        }
    }

    public void Cancel()
    {
        StopHoldTimer();
        ResetShapeState();
        _brush.Cancel();
    }

    public void Commit()
    {
        if (_phase == SmartShapePhase.Preview)
            CommitPreview();
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

    public IProcessedInput? GetImmediateResult() => _brush.GetImmediateResult();

    public IProcessedInput? GetPreview()
    {
        if (_phase == SmartShapePhase.Preview && _currentShape != null)
        {
            return new SmartShapeCommitInput
            {
                Shape = _currentShape,
                StrokeClosed = _strokeClosed,
                RawSamples = _rawSamples.ToArray()
            };
        }

        return _brush.GetPreview();
    }

    public void RenderOverlay(DrawingContext dc, double zoom) => _brush.RenderOverlay(dc, zoom);

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
        if (_phase != SmartShapePhase.Drawing || _rawSamples.Count < 4)
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

        _strokeClosed = SmartShapeFitter.StrokeIsClosed(RawPoints(_rawSamples));
        var shape = SmartShapeFitter.Fit(RawPoints(_rawSamples), SmartShapeFitKind.Auto);
        if (shape == null)
            return;

        _activeFitKind = SmartShapeFitter.DetectFitKind(shape, _strokeClosed);
        EnterPreview(shape, pos);
    }

    private void EnterPreview(SmartShapeModel shape, CanvasInputSample pos)
    {
        StopHoldTimer();
        _output?.AbortLiveStroke();
        _brush.Cancel();

        _phase = SmartShapePhase.Preview;
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

    private void CommitPreview()
    {
        if (_currentShape == null || _phase != SmartShapePhase.Preview)
            return;

        var result = new SmartShapeCommitInput
        {
            Shape = _currentShape,
            StrokeClosed = _strokeClosed,
            RawSamples = _rawSamples.ToArray()
        };
        ResetShapeState();
        _commitResult = result;
    }

    private void ResetShapeState()
    {
        StopHoldTimer();
        _phase = SmartShapePhase.Idle;
        _commitResult = null;
        _rawSamples.Clear();
        _strokeClosed = false;
        _activeFitKind = SmartShapeFitKind.Auto;
        _baseShape = null;
        _currentShape = null;
        NotifyPhaseChanged();
    }

    private void NotifyPhaseChanged() => PhaseChanged?.Invoke();

    private static List<Vec2> RawPoints(IReadOnlyList<CanvasInputSample> samples)
    {
        var pts = new List<Vec2>(samples.Count);
        foreach (var s in samples)
            pts.Add(new Vec2(s.X, s.Y));
        return pts;
    }
}
