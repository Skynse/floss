using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Config;
using Floss.App.Document;
using Floss.App.Document.Assistants;
using Floss.App.Input;

namespace Floss.App.Tools.Assistants;

/// <summary>Edit and create document painting assistants (assistant tool).</summary>
public sealed class AssistantTool : ITool
{
    private readonly DrawingDocument _document;
    private readonly AssistantCreateSettings _createSettings;
    private AssistantsSnapshot? _undoBefore;
    private PaintingAssistant? _createPreview;
    private Point _createStart;
    private int _draggingHandle;
    private Point _moveOffset;
    private const int BodyMoveHandle = -2;
    private const double CreateDragThreshold = 16;

    public AssistantTool(DrawingDocument document, AssistantCreateSettings createSettings)
    {
        _document = document;
        _createSettings = createSettings;
    }

    public string CreateTypeId => _createSettings.TypeId;

    public bool HasPendingOperation => _createPreview != null || _draggingHandle != 0;

    public void Activate(ToolContext ctx) { }

    public void Deactivate(ToolContext ctx) => Cancel(ctx);

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        _undoBefore = _document.Assistants.CaptureSnapshot();

        var assistants = _document.Assistants;
        var point = new Point(s.X, s.Y);
        const double tolerance = 12;

        if (assistants.HitTestHandle(point, tolerance) is { } hit)
        {
            assistants.SelectedId = hit.Assistant.Id;
            _draggingHandle = hit.HandleIndex;
            ctx.InvalidateRender();
            return;
        }

        var lineHit = assistants.HitTestLine(point, tolerance);
        if (lineHit != null)
        {
            assistants.SelectedId = lineHit.Id;
            _draggingHandle = BodyMoveHandle;
            _moveOffset = new Point(s.X, s.Y);
            ctx.InvalidateRender();
            return;
        }

        assistants.SelectedId = null;
        _createStart = point;
        _draggingHandle = -1;
        ctx.InvalidateRender();
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        var point = ApplyShift(ctx, new Point(s.X, s.Y));

        if (_draggingHandle == -1 && _createPreview == null)
        {
            var dx = s.X - _createStart.X;
            var dy = s.Y - _createStart.Y;
            if (dx * dx + dy * dy >= CreateDragThreshold * CreateDragThreshold)
            {
                _createPreview = PaintingAssistant.FromDrag(_createSettings.TypeId, _createStart, point, _createSettings);
            }
            ctx.InvalidateRender();
            return;
        }

        if (_createPreview != null)
        {
            _createPreview = PaintingAssistant.FromDrag(_createSettings.TypeId, _createStart, point, _createSettings);
            ctx.InvalidateRender();
            return;
        }

        if (_draggingHandle == BodyMoveHandle)
        {
            var selected = _document.Assistants.FindById(_document.Assistants.SelectedId);
            if (selected == null)
                return;

            var dx = point.X - _moveOffset.X;
            var dy = point.Y - _moveOffset.Y;
            _moveOffset = point;
            for (var i = 1; i <= selected.HandleCount; i++)
                selected.SetHandle(i, new Point(selected.GetHandle(i).X + dx, selected.GetHandle(i).Y + dy));
            _document.Assistants.NotifyChanged();
            ctx.InvalidateRender();
            return;
        }

        if (_draggingHandle <= 0)
            return;

        var sel = _document.Assistants.FindById(_document.Assistants.SelectedId);
        if (sel == null)
            return;

        sel.SetHandle(_draggingHandle, point);
        _document.Assistants.NotifyChanged();
        ctx.InvalidateRender();
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        if (_createPreview != null)
        {
            var end = ApplyShift(ctx, new Point(s.X, s.Y));
            var assistant = IsDragLargeEnough(_createStart, end)
                ? PaintingAssistant.FromDrag(_createSettings.TypeId, _createStart, end, _createSettings)
                : _createSettings.TypeId == PaintingAssistant.PerspectiveType
                    ? PaintingAssistant.CreateDefaultAt(
                        _createStart,
                        _document.Width,
                        _document.Height,
                        _createSettings)
                    : PaintingAssistant.FromDrag(
                        _createSettings.TypeId,
                        _createStart,
                        new Point(_createStart.X + 120, _createStart.Y),
                        _createSettings);
            _document.Assistants.Add(assistant, createAtEditingLayer: _createSettings.CreateAtEditingLayer);
            _createPreview = null;
            _draggingHandle = 0;
            TryCommitAssistantsChange();
            ctx.InvalidateRender();
            return;
        }

        if (_draggingHandle == -1)
        {
            _undoBefore = null;
        }

        _draggingHandle = 0;
        TryCommitAssistantsChange();
    }

    public void Cancel(ToolContext ctx)
    {
        if (_undoBefore != null)
        {
            _document.Assistants.RestoreSnapshot(_undoBefore);
            _undoBefore = null;
        }

        _createPreview = null;
        _draggingHandle = 0;
        ctx.InvalidateRender();
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) { }

    public bool ConsumesModifier(KeyModifiers mods) => mods.HasFlag(KeyModifiers.Shift);

    public PaintingAssistant? CreatePreview => _createPreview;

    private void TryCommitAssistantsChange()
    {
        if (_undoBefore == null) return;
        _document.CommitAssistantsChange(_undoBefore);
        _undoBefore = null;
    }

    private static bool IsDragLargeEnough(Point start, Point end)
    {
        var dx = end.X - start.X;
        var dy = end.Y - start.Y;
        return dx * dx + dy * dy >= 4;
    }

    private Point ApplyShift(ToolContext ctx, Point documentPoint)
    {
        if (!ctx.CurrentModifiers.HasFlag(KeyModifiers.Shift))
            return documentPoint;

        if (_createPreview != null)
            return AssistantSnap.ConstrainTo45Degrees(_createStart, documentPoint);

        var selected = _document.Assistants.FindById(_document.Assistants.SelectedId);
        if (selected == null || _draggingHandle <= 0)
            return documentPoint;

        var anchor = _draggingHandle switch
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
