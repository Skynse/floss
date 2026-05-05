using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Samples color from the canvas and fires the OnColorSampled callback.
public sealed class EyedropperOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;
    public EyedropperSampleMode SampleMode { get; set; } = EyedropperSampleMode.Image;
    public bool ExcludeLockedLayers { get; set; }
    public bool ExcludeReferenceLayers { get; set; }

    public void Preview(ToolContext ctx, IProcessedInput input) { }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        if (input is not ClickInput click) return;

        var color = ctx.SampleDocumentColor(
            (int)click.Point.X,
            (int)click.Point.Y,
            new EyedropperSampleOptions(SampleMode, ExcludeLockedLayers, ExcludeReferenceLayers));
        if (color.HasValue)
        {
            ctx.OnColorSampled(color.Value);
        }
    }
}
