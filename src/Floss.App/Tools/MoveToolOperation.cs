using System;
using Floss.App.Input;

namespace Floss.App.Tools;

public sealed class MoveToolOperation : IToolOperation
{
    private readonly ToolContext _context;
    private readonly double _startX;
    private readonly double _startY;
    private readonly int _layerIndex;
    private readonly int _origOffsetX;
    private readonly int _origOffsetY;
    private bool _didMove;

    public MoveToolOperation(ToolContext context, CanvasInputSample firstSample)
    {
        _context = context;
        var layer = context.ActiveLayer;
        _layerIndex = context.ActiveLayerIndex;
        _startX = firstSample.X;
        _startY = firstSample.Y;
        _origOffsetX = layer?.OffsetX ?? 0;
        _origOffsetY = layer?.OffsetY ?? 0;
        SampleCount = 1;
    }

    public int SampleCount { get; private set; }

    public void Update(CanvasInputSample sample)
    {
        var layer = _context.ActiveLayer;
        if (layer == null || _layerIndex != _context.ActiveLayerIndex) return;

        int dx = (int)Math.Round(sample.X - _startX);
        int dy = (int)Math.Round(sample.Y - _startY);
        if (dx == 0 && dy == 0) return;

        if (!_didMove)
        {
            _context.Document.BeginDocumentMutation();
            _didMove = true;
        }

        layer.OffsetX = _origOffsetX + dx;
        layer.OffsetY = _origOffsetY + dy;
        SampleCount++;
        _context.Document.NotifyChanged(null, _context.ActiveLayerIndex);
    }

    public void Commit(CanvasInputSample sample)
    {
        Update(sample);
        SampleCount = 0;
    }

    public void Cancel()
    {
        var layer = _context.ActiveLayer;
        if (layer != null && _layerIndex == _context.ActiveLayerIndex)
        {
            layer.OffsetX = _origOffsetX;
            layer.OffsetY = _origOffsetY;
            _context.Document.NotifyChanged(null, _context.ActiveLayerIndex);
        }
        SampleCount = 0;
    }
}
