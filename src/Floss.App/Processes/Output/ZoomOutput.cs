using System;
using Avalonia;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

public sealed class ZoomOutput : IOutputProcess
{
    public bool Antialiasing { get; set; }
    public double ZoomSensitivity { get; set; } = App.Shortcuts.ZoomDragSensitivity;
    public double Direction { get; set; } = 1;

    private Point? _lastVpPos;

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not DragInput drag) return;
        Apply(ctx, drag);
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        _lastVpPos = null;
    }

    public void Cancel() => _lastVpPos = null;

    public bool IsPaintOutput => false;

    private void Apply(ToolContext ctx, DragInput drag)
    {
        var vp = ctx.Viewport;
        if (vp == null) return;

        double delta;
        if (_lastVpPos.HasValue)
        {
            var dx = drag.Current.X - _lastVpPos.Value.X;
            delta = dx;
        }
        else
        {
            delta = 0;
        }

        _lastVpPos = new Point(drag.Current.X, drag.Current.Y);

        if (Math.Abs(delta) < 0.001) return;

        vp.ZoomBy(Math.Pow(ZoomSensitivity, delta * Direction),
            new Point(drag.Start.X, drag.Start.Y));
    }
}
