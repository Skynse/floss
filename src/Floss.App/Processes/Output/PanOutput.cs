using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Pans the canvas based on drag delta.
public sealed class PanOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        // Pan is handled by the viewport directly via gesture system.
        // This output process is a no-op placeholder for the architecture.
    }
}
