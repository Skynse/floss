using System;
using System.Threading;
using System.Threading.Tasks;
using Floss.App.Brushes.Graph;
using Floss.App.Input;

namespace Floss.App.Brushes.Engine;

public sealed class BrushPreparationScheduler : IDisposable
{
    private readonly object _lock = new();
    private CancellationTokenSource _cts = new();
    private BrushPreset? _lastBrush;
    private int _lastSize;
    private int _lastHardness;

    public void QueuePrepare(BrushPreset brush, CanvasInputSample sample)
    {
        var size = BrushTipMaskRasterization.StrokePeakMaskSize(brush);
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
        // Warm the SHARED brush.Tip caches so the engine thread gets cache hits
        // when it reads masks. Cloning the tip per prepare-run made every brush
        // size change pay the full mask-generation cost (the engine still reads
        // from the shared tip, whose cache stayed empty).
        //
        // Tip caches are already thread-safe (each tip implementation guards
        // its dictionary with an internal lock). Nothing in the codebase
        // disposes a shared brush.Tip while a stroke is in flight, so the
        // engine's raw-pointer access to a cached mask is safe.
        try
        {
            token.ThrowIfCancellationRequested();
            BrushMaterialTips.BindToPreset(brush);
            if (!BrushEngine.UsesProceduralStampEvaluation(brush, brush.Tip, 0))
            {
                brush.Tip.GenerateMask(size, hardness);
                if (brush.Tip.HasColor)
                    brush.Tip.GenerateColorStamp(size);
            }

            if (brush.Shape != null)
            {
                token.ThrowIfCancellationRequested();
                brush.Shape.GenerateMask(size, hardness);
            }

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
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "BrushPreparationScheduler.PrepareCore", flushToDisk: true);
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
