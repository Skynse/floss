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

    // Render real-time preview overlay.
    void RenderOverlay(DrawingContext dc, double zoom);
}
