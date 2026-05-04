using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Samples color from the canvas and fires the OnColorSampled callback.
public sealed class EyedropperOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not ClickInput click) return;

        var color = ctx.SampleDocumentColor((int)click.Point.X, (int)click.Point.Y);
        if (color.HasValue)
        {
            ctx.OnColorSampled(color.Value);
        }
    }
}
