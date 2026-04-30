using Avalonia.Media;
using Floss.App.Input;

namespace Floss.App.Tools;

public abstract class DragToolOperation : IToolOperation
{
    protected DragToolOperation(ToolContext context, CanvasInputSample firstSample)
    {
        Context = context;
        StartSample = firstSample;
        CurrentSample = firstSample;
        SampleCount = 1;
    }

    protected ToolContext Context { get; }
    protected CanvasInputSample StartSample { get; }
    protected CanvasInputSample CurrentSample { get; private set; }

    public int SampleCount { get; private set; }

    public virtual void Update(CanvasInputSample sample)
    {
        CurrentSample = sample;
        SampleCount++;
        Context.InvalidateRender();
    }

    public void Commit(CanvasInputSample sample)
    {
        CurrentSample = sample;
        Apply();
        Finish();
    }

    public virtual void Cancel()
    {
        Finish();
    }

    public abstract void RenderOverlay(DrawingContext dc, double zoom);

    protected abstract void Apply();

    protected virtual void Finish()
    {
        SampleCount = 0;
        Context.InvalidateRender();
    }
}
