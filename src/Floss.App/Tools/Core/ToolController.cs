using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Brushes;
using Floss.App.Input;
using Floss.App.Processes;

namespace Floss.App.Tools;

public enum ToolInputEventKind
{
    Down,
    Move,
    Up
}

public readonly record struct ToolInputEvent(ToolInputEventKind Kind, CanvasInputSample Sample);

public sealed class ToolController
{
    private readonly ToolContext _context;
    private bool _isAlternateActive;

    public ToolController(ToolContext context, ITool initialTool)
    {
        _context = context;
        ActiveTool = initialTool;
        ActiveTool.Activate(_context);
    }

    public ITool ActiveTool { get; private set; }
    public bool IsAlternateActive => _isAlternateActive && ActiveTool.Alternate != null;
    public ToolPresetEngine ActiveEngine { get; private set; } = ToolPresetEngine.Brush;
    public bool HasPendingOperation => Current.HasPendingOperation;
    public bool HasSavedSettings(ToolPresetEngine engine) => false;

    private ITool Current => _isAlternateActive ? ActiveTool.Alternate ?? ActiveTool : ActiveTool;

    public void SetAlternateActive(bool active)
    {
        if (_isAlternateActive == active) return;
        _isAlternateActive = active && ActiveTool.Alternate != null;
    }

    public void SetActiveTool(ITool tool, ToolPreset? preset = null)
    {
        if (ActiveTool == tool) return;

        ActiveTool.Deactivate(_context);
        _context.ActivePreset = preset;
        _isAlternateActive = false;

        var newEngine = EngineForTool(tool);
        ActiveEngine = newEngine;

        ActiveTool = tool;
        ActiveTool.Activate(_context);
    }

    public void SaveEnginePreset()
    {
    }

    private static ToolPresetEngine EngineForTool(ITool tool) => tool switch
    {
        CompositeTool => ToolPresetEngine.Brush,
        _ => ToolPresetEngine.Brush
    };

    public void Dispatch(ToolInputEvent input)
    {
        var tool = Current;
        switch (input.Kind)
        {
            case ToolInputEventKind.Down:
                tool.PointerDown(_context, input.Sample);
                break;
            case ToolInputEventKind.Move:
                tool.PointerMove(_context, input.Sample);
                break;
            case ToolInputEventKind.Up:
                tool.PointerUp(_context, input.Sample);
                break;
        }
    }

    public bool Cancel()
    {
        var tool = Current;
        var hadPendingOperation = tool.HasPendingOperation;
        tool.Cancel(_context);
        return hadPendingOperation;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
        => Current.RenderOverlay(dc, _context, zoom);
}
