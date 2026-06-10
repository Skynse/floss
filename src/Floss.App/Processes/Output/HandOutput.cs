using System;
using Avalonia;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

public sealed class HandOutput : IOutputProcess
{
    public bool Antialiasing { get; set; }

    private Point? _lastVpPos;

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not DragInput drag)
            return;
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

        double dx, dy;
        if (_lastVpPos.HasValue)
        {
            dx = drag.Current.X - _lastVpPos.Value.X;
            dy = drag.Current.Y - _lastVpPos.Value.Y;
        }
        else
        {
            dx = 0;
            dy = 0;
        }

        _lastVpPos = new Point(drag.Current.X, drag.Current.Y);

        if (Math.Abs(dx) > 0.001 || Math.Abs(dy) > 0.001)
            vp.PanBy(dx, dy);
    }
}
