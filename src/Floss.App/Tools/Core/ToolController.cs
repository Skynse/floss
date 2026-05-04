using System;
using System.Collections.Generic;
using Avalonia.Media;
using Floss.App.Brushes;
using Floss.App.Input;

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
    private readonly Dictionary<ToolPresetEngine, ToolPreset> _enginePresets = new();

    public ToolController(ToolContext context, ITool initialTool)
    {
        _context = context;
        ActiveTool = initialTool;
        ActiveTool.Activate(_context);
    }

    public ITool ActiveTool { get; private set; }
    public ToolPresetEngine ActiveEngine { get; private set; } = ToolPresetEngine.Brush;
    public bool HasPendingOperation => ActiveTool.HasPendingOperation;
    public event Action<BrushPreset>? BrushSettingsChanged;
    public bool HasSavedSettings(ToolPresetEngine engine) => _enginePresets.ContainsKey(engine);

    // Alternate tool runs *on top* of the active tool without switching engines.
    // Used by the viewport-wide eyedropper (Alt key).
    public ITool? AlternateTool { get; set; }

    public void SetActiveTool(ITool tool, ToolPreset? preset = null)
    {
        if (ActiveTool == tool) return;

        // Save current brush settings into the active engine's preset before switching
        SaveEnginePreset();

        ActiveTool.Deactivate(_context);
        _context.ActivePreset = preset;

        // Determine new engine
        var newEngine = EngineForTool(tool);
        ActiveEngine = newEngine;

        // After Activate() applies the preset overrides, re-apply saved per-engine
        // settings so the user's last-used slider values survive the switch.
        if (_enginePresets.TryGetValue(newEngine, out var saved))
        {
            ActiveTool = tool;
            ActiveTool.Activate(_context);
            _context.Brush = saved.ApplyToBrushPreset(_context.Brush);
            BrushSettingsChanged?.Invoke(_context.Brush);
        }
        else
        {
            ActiveTool = tool;
            ActiveTool.Activate(_context);
        }
    }

    public void SaveEnginePreset()
    {
        var engine = ActiveEngine;
        if (engine is not (ToolPresetEngine.Brush or ToolPresetEngine.Eraser))
            return;
        if (!_enginePresets.ContainsKey(engine))
            _enginePresets[engine] = new ToolPreset { Engine = engine };
        _enginePresets[engine].CaptureFromBrushPreset(_context.Brush);
    }

    private static ToolPresetEngine EngineForTool(ITool tool) => tool switch
    {
        BrushTool bt when bt.IsEraser => ToolPresetEngine.Eraser,
        BrushTool => ToolPresetEngine.Brush,
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
