using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Zooms the canvas based on drag delta.
public sealed class ZoomOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;
    public double Sensitivity { get; set; } = 0.01;

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        // Zoom is handled by the viewport directly via gesture system.
        // This output process is a no-op placeholder for the architecture.
    }
}
