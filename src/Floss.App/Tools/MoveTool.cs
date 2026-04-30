using System;
using Avalonia.Media;
using Floss.App.Document;
using Floss.App.Input;

namespace Floss.App.Tools;

// Translates the active layer's offset by dragging.
public sealed class MoveTool : ITool
{
    private bool _dragging;
    private double _startX, _startY;
    private int _origOffsetX, _origOffsetY;
    private bool _didMove;

    public void Activate(ToolContext ctx) { }
    public void Deactivate(ToolContext ctx) { }

    public void PointerDown(ToolContext ctx, CanvasInputSample s)
    {
        var layer = ctx.ActiveLayer;
        if (layer == null) return;
        _dragging = true;
        _didMove = false;
        _startX = s.X;
        _startY = s.Y;
        _origOffsetX = layer.OffsetX;
        _origOffsetY = layer.OffsetY;
    }

    public void PointerMove(ToolContext ctx, CanvasInputSample s)
    {
        if (!_dragging) return;
        var layer = ctx.ActiveLayer;
        if (layer == null) return;

        int dx = (int)Math.Round(s.X - _startX);
        int dy = (int)Math.Round(s.Y - _startY);
        if (dx == 0 && dy == 0) return;

        if (!_didMove)
        {
            ctx.Document.BeginDocumentMutation();
            _didMove = true;
        }

        layer.OffsetX = _origOffsetX + dx;
        layer.OffsetY = _origOffsetY + dy;
        ctx.Document.NotifyChanged(null, ctx.ActiveLayerIndex);
    }

    public void PointerUp(ToolContext ctx, CanvasInputSample s)
    {
        PointerMove(ctx, s);
        _dragging = false;
    }

    public void Cancel(ToolContext ctx)
    {
        if (!_dragging) return;
        _dragging = false;
        var layer = ctx.ActiveLayer;
        if (layer == null) return;
        layer.OffsetX = _origOffsetX;
        layer.OffsetY = _origOffsetY;
        ctx.Document.NotifyChanged(null, ctx.ActiveLayerIndex);
    }

    public void RenderOverlay(DrawingContext dc, ToolContext ctx, double zoom) { }
}
