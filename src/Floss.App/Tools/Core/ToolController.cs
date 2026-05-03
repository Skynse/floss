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

    public void SetActiveTool(ITool tool, ToolPreset? preset = null)
    {
        if (ActiveTool == tool) return;

        // Save current brush settings into the active engine's preset before switching
        var currentEngine = ActiveEngine;
        if (!_enginePresets.ContainsKey(currentEngine))
            _enginePresets[currentEngine] = new ToolPreset { Engine = currentEngine };
        _enginePresets[currentEngine].CaptureFromBrushPreset(_context.Brush);

        ActiveTool.Deactivate(_context);
        _context.ActivePreset = preset;

        // Determine new engine
        var newEngine = EngineForTool(tool);
        ActiveEngine = newEngine;

        // After Activate() applies the preset overrides, re-apply saved per-engine
        // settings so the user's last-used slider values survive the switch.
        if (_enginePresets.TryGetValue(newEngine, out var saved))
        {
            var beforeActivate = _context.Brush;
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

    private static ToolPresetEngine EngineForTool(ITool tool) => tool switch
    {
        BrushTool bt when bt.IsEraser => ToolPresetEngine.Eraser,
        BrushTool => ToolPresetEngine.Brush,
        SmudgeTool => ToolPresetEngine.Smudge,
        _ => ToolPresetEngine.Brush
    };

    public void Dispatch(ToolInputEvent input)
    {
        switch (input.Kind)
        {
            case ToolInputEventKind.Down:
                ActiveTool.PointerDown(_context, input.Sample);
                break;
            case ToolInputEventKind.Move:
                ActiveTool.PointerMove(_context, input.Sample);
                break;
            case ToolInputEventKind.Up:
                ActiveTool.PointerUp(_context, input.Sample);
                break;
        }
    }

    public bool Cancel()
    {
        var hadPendingOperation = ActiveTool.HasPendingOperation;
        ActiveTool.Cancel(_context);
        return hadPendingOperation;
    }

    public void RenderOverlay(DrawingContext dc, double zoom)
        => ActiveTool.RenderOverlay(dc, _context, zoom);
}
