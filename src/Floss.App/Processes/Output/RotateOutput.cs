using System;
using Avalonia;
using Floss.App.Tools;

namespace Floss.App.Processes.Output;

public sealed class RotateOutput : IOutputProcess
{
    public bool Antialiasing { get; set; }

    private double? _lastAngle;

    public void Preview(ToolContext ctx, IProcessedInput input)
    {
        if (input is not DragInput drag) return;
        Apply(ctx, drag);
    }

    public void Execute(ToolContext ctx, IProcessedInput input)
    {
        _lastAngle = null;
    }

    public void Cancel() => _lastAngle = null;

    public bool IsPaintOutput => false;

    private void Apply(ToolContext ctx, DragInput drag)
    {
        var vp = ctx.Viewport;
        if (vp == null) return;

        var vpW = Math.Max(1, ctx.ViewportSize.Width);
        var vpH = Math.Max(1, ctx.ViewportSize.Height);
        var vpCenter = new Point(vpW * 0.5, vpH * 0.5);

        var toRad = Math.Atan2(drag.Current.Y - vpCenter.Y, drag.Current.X - vpCenter.X);

        if (_lastAngle.HasValue)
        {
            var deltaRad = toRad - _lastAngle.Value;
            if (deltaRad > Math.PI) deltaRad -= 2 * Math.PI;
            if (deltaRad < -Math.PI) deltaRad += 2 * Math.PI;
            vp.RotateBy(deltaRad * 180.0 / Math.PI);
        }

        _lastAngle = toRad;
    }
}
