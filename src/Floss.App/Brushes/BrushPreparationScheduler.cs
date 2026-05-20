using System;
using System.Threading;
using System.Threading.Tasks;
using Floss.App.Input;

namespace Floss.App.Brushes;

public sealed class BrushPreparationScheduler : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource _cts = new();
    private BrushPreset? _lastBrush;
    private int _lastSize;
    private int _lastHardness;

    public void QueuePrepare(BrushPreset brush, CanvasInputSample sample)
    {
        var size = Math.Max(1, Math.Min(1024, (int)Math.Ceiling(brush.Size * Math.Max(1.0, brush.Dynamics.Size.MaxOutput))));
        var hardness = Math.Clamp((int)MathF.Round((float)Math.Clamp(brush.Hardness, 0.001, 1.0) * 255f), 0, 255);

        lock (_lock)
        {
            if (ReferenceEquals(_lastBrush, brush) && _lastSize == size && _lastHardness == hardness)
                return;

            _lastBrush = brush;
            _lastSize = size;
            _lastHardness = hardness;
            _cts.Cancel();
            _cts.Dispose();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            Task.Run(() => PrepareCore(brush, size, hardness / 255f, token), token)
                .ContinueWith(t =>
                {
                    if (t.IsFaulted && t.Exception != null)
                        CrashLog.Write(t.Exception, "BrushPreparationScheduler");
                }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    private static void PrepareCore(BrushPreset brush, int size, float hardness, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        brush.Tip.GenerateMask(size, hardness);
        if (brush.Tip.HasColor)
            brush.Tip.GenerateColorStamp(size);

        if (brush.Shape != null)
            brush.Shape.GenerateMask(size, hardness);

        foreach (var tip in brush.Tips)
        {
            token.ThrowIfCancellationRequested();
            var liveTip = tip.CreateTip();
            liveTip.GenerateMask(size, hardness);
            if (liveTip.HasColor)
                liveTip.GenerateColorStamp(size);
            if (liveTip is IDisposable disposable)
                disposable.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _cts.Cancel();
            _cts.Dispose();
        }
    }
}
