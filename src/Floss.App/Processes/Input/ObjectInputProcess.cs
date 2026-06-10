using System;
using Avalonia;
using Avalonia.Media;
using Floss.App.Config;
using Floss.App.Document;
using Floss.App.Document.Assistants;
using Floss.App.Input;
using Floss.App.Tools;
using Floss.App.Tools.Assistants;

namespace Floss.App.Processes.Input;

/// <summary>Click/drag input for selecting and manipulating canvas objects (Object tool).</summary>
public sealed class ObjectInputProcess : IInputProcess
{
    private readonly DrawingDocument _document;
    private readonly AssistantManipulationSession _session = new();
    private bool _completed;
    private bool _isActive;

    public ObjectInputProcess(DrawingDocument document)
    {
        _document = document;
    }

    public SelectableObjectFlags SelectableFlags { get; set; } = SelectableObjectFlags.Ruler;
    public bool ShiftConstrain { get; set; }
    public bool IsActive => _isActive;

    public ToolAuxOperationType ToolAuxMode { get; set; }
    public double Stabilization { get; set; }

    public void PointerDown(CanvasInputSample s)
    {
        _completed = false;
        _isActive = true;
        _session.PointerDown(_document, new Point(s.X, s.Y), SelectableFlags);
    }

    public void PointerMove(CanvasInputSample s)
    {
        if (!_isActive)
            return;

        _session.PointerMove(_document, new Point(s.X, s.Y), ShiftConstrain);
    }

    public void PointerUp(CanvasInputSample s)
    {
        if (!_isActive)
            return;

        _session.PointerMove(_document, new Point(s.X, s.Y), ShiftConstrain);
        _session.FinishGesture();
        _isActive = false;
        _completed = true;
    }

    public void Cancel()
    {
        _session.Cancel(_document);
        _isActive = false;
        _completed = false;
    }

    public IProcessedInput? GetResult()
    {
        if (!_completed)
            return null;

        _completed = false;
        return _session.HasPendingCommit ? ObjectManipulationInput.Instance : null;
    }

    public IProcessedInput? GetPreview()
        => _isActive && _session.IsDraggingHandle ? ObjectManipulationInput.Instance : null;

    public void RenderOverlay(DrawingContext dc, double zoom) { }

    public bool ConsumesModifier(Avalonia.Input.KeyModifiers mods)
        => mods.HasFlag(Avalonia.Input.KeyModifiers.Shift);

    internal void CommitSession(Action<AssistantsSnapshot> commitChange)
        => _session.Commit(_document, commitChange);

    internal void CancelSession() => Cancel();
}
