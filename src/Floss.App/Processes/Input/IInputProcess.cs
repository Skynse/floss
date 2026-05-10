using Avalonia.Input;
using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Processes;

// Captures raw pointer input, applies stabilization/smoothing, produces IProcessedInput.
public interface IInputProcess
{
    bool IsActive { get; }
    double Stabilization { get; set; }  // 0 = none, 1 = full

    void PointerDown(CanvasInputSample s);
    void PointerMove(CanvasInputSample s);
    void PointerUp(CanvasInputSample s);
    void Cancel();

    // Returns the processed input if complete, null otherwise.
    IProcessedInput? GetResult();

    // Force-finish the current input (for Enter key / double-click).
    void Commit() { }

    // Returns the current in-progress input for live preview, null if not available.
    IProcessedInput? GetPreview();

    // Render real-time preview overlay.
    void RenderOverlay(DrawingContext dc, double zoom);

    // True for brush-family inputs that should show a brush radius cursor outline.
    bool HasBrushCursor => false;
    bool ConsumesModifier(Avalonia.Input.KeyModifiers mods) => false;
    ToolAuxOperationType ToolAuxMode { get; set; }
}
