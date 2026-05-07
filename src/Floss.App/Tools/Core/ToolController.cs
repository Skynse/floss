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
    public ToolController(ToolContext context, ITool initialTool)
    {
        _context = context;
        ActiveTool = initialTool;
        ActiveTool.Activate(_context);
    }

    public ITool ActiveTool { get; private set; }
    public ToolPresetEngine ActiveEngine { get; private set; } = ToolPresetEngine.Brush;
    public bool HasPendingOperation => ActiveTool.HasPendingOperation;
    public bool HasSavedSettings(ToolPresetEngine engine) => false;

    // Alternate tool runs *on top* of the active tool without switching engines.
    // Used by the viewport-wide eyedropper (Alt key).
    public ITool? AlternateTool { get; set; }

    public void SetActiveTool(ITool tool, ToolPreset? preset = null)
    {
        if (ActiveTool == tool) return;

        ActiveTool.Deactivate(_context);
        _context.ActivePreset = preset;

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
        var tool = AlternateTool ?? ActiveTool;
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
        var tool = AlternateTool ?? ActiveTool;
        var hadPendingOperation = tool.HasPendingOperation;
        tool.Cancel(_context);
        return hadPendingOperation;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
        => (AlternateTool ?? ActiveTool).RenderOverlay(dc, _context, zoom);
}
