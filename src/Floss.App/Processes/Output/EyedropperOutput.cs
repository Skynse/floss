using Floss.App.Input;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

// Samples color from the canvas and fires the OnColorSampled callback.
public sealed class EyedropperOutput : IOutputProcess
{
    public bool Antialiasing { get; set; } = false;
    public EyedropperSampleMode SampleMode { get; set; } = EyedropperSampleMode.Image;
    public bool ExcludeLockedLayers { get; set; }
    public bool ExcludeReferenceLayers { get; set; }

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        SampleColor(ctx, input);
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        SampleColor(ctx, input);
    }

    private void SampleColor(ToolContext ctx, IProcessedInput input)
    {
        CanvasInputSample? sample = input switch
        {
            ClickInput click => click.Point,
            StrokeInput stroke when stroke.SmoothedSamples.Count > 0 => stroke.SmoothedSamples[^1],
            DragInput drag => drag.Current,
            _ => null
        };

        if (sample == null) return;

        var color = ctx.SampleDocumentColor(
            (int)sample.Value.X,
            (int)sample.Value.Y,
            new EyedropperSampleOptions(SampleMode, ExcludeLockedLayers, ExcludeReferenceLayers));
        if (color.HasValue)
        {
            ctx.OnColorSampled(color.Value);
        }
    }
}
