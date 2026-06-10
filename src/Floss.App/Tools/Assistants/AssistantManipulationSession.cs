using System;
using Avalonia;
using Floss.App.Document;
using Floss.App.Document.Assistants;
using Floss.App.Tools;

namespace Floss.App.Tools.Assistants;

/// <summary>Select and drag painting-assistant handles (shared by Object tool and Assistant tool).</summary>
public sealed class AssistantManipulationSession
{
    private AssistantsSnapshot? _undoBefore;
    private int _draggingHandle;

    public bool IsDraggingHandle => _draggingHandle > 0;
    public bool HasPendingCommit => _undoBefore != null;

    public bool PointerDown(DrawingDocument document, Point point, SelectableObjectFlags flags, double tolerance = 12)
    {
        _undoBefore = document.Assistants.CaptureSnapshot();
        _draggingHandle = 0;

        var assistants = document.Assistants;
        if ((flags & SelectableObjectFlags.Ruler) != 0)
        {
            if (assistants.HitTestHandle(point, tolerance) is { } hit)
            {
                assistants.SelectedId = hit.Assistant.Id;
                _draggingHandle = hit.HandleIndex;
                assistants.NotifyChanged();
                return true;
            }

            if (assistants.HitTestLine(point, tolerance) is { } lineHit)
            {
                assistants.SelectedId = lineHit.Id;
                assistants.NotifyChanged();
                return true;
            }
        }

        if (assistants.SelectedId != null)
        {
            assistants.SelectedId = null;
            assistants.NotifyChanged();
        }
        return false;
    }

    public void PointerMove(DrawingDocument document, Point point, bool shiftConstrain)
    {
        if (_draggingHandle <= 0)
            return;

        var selected = document.Assistants.FindById(document.Assistants.SelectedId);
        if (selected == null)
            return;

        if (shiftConstrain)
            point = ConstrainHandle(selected, _draggingHandle, point);

        selected.SetHandle(_draggingHandle, point);
        document.Assistants.NotifyChanged();
    }

    public void FinishGesture()
    {
        _draggingHandle = 0;
    }

    public void Commit(DrawingDocument document, Action<AssistantsSnapshot> commitChange)
        => TryCommit(document, commitChange);

    public void Cancel(DrawingDocument document)
    {
        if (_undoBefore != null)
            document.Assistants.RestoreSnapshot(_undoBefore);

        _undoBefore = null;
        _draggingHandle = 0;
    }

    private void TryCommit(DrawingDocument document, Action<AssistantsSnapshot> commitChange)
    {
        if (_undoBefore == null)
            return;

        commitChange(_undoBefore);
        _undoBefore = null;
    }

    private static Point ConstrainHandle(PaintingAssistant selected, int handleIndex, Point documentPoint)
    {
        var anchor = handleIndex switch
        {
            1 => selected.GetHandle(2),
            2 => selected.GetHandle(1),
            3 => selected.GetHandle(4),
            4 => selected.GetHandle(3),
            _ => documentPoint,
        };

        return AssistantSnap.ConstrainTo45Degrees(anchor, documentPoint);
    }
}
