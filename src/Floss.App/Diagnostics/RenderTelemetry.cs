using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Floss.App.Diagnostics;

public readonly record struct RenderTelemetrySnapshot(
    double BrushMs,
    string BrushPath,
    int BrushStamps,
    int BrushCachedDabs,
    double CompositeMs,
    int CompositeDirtyTiles,
    int CompositeMissingTiles,
    int PendingProjectionUpdates);

public readonly struct TelemetryScope : IDisposable
{
    private readonly long _started;

    internal TelemetryScope(long started)
    {
        _started = started;
    }

    public void Dispose()
        => RenderTelemetry.RecordBrushMs(RenderTelemetry.ElapsedMs(_started));
}

public static class RenderTelemetry
{
    private static readonly object Lock = new();
    private static readonly string LogPath = AppPaths.RenderTelemetryLogPath;
    private static long _lastLogTicks;
    private static RenderTelemetrySnapshot _snapshot;

    public static RenderTelemetrySnapshot Snapshot
    {
        get { lock (Lock) return _snapshot; }
    }

    public static TelemetryScope ScopeNow() => new(Stopwatch.GetTimestamp());

    internal static double ElapsedMs(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;

    internal static void RecordBrushMs(double elapsedMs)
    {
        lock (Lock) _snapshot = _snapshot with { BrushMs = elapsedMs };
        MaybeLog();
    }

    public static void RecordBrush(double elapsedMs, string path, int stamps, int cachedDabs)
    {
        lock (Lock)
        {
            _snapshot = _snapshot with
            {
                BrushMs = elapsedMs,
                BrushPath = path,
                BrushStamps = stamps,
                BrushCachedDabs = cachedDabs
            };
        }
        MaybeLog();
    }

    public static void RecordComposite(double elapsedMs, int dirtyTiles, int missingTiles, int pendingProjectionUpdates)
    {
        lock (Lock)
        {
            _snapshot = _snapshot with
            {
                CompositeMs = elapsedMs,
                CompositeDirtyTiles = dirtyTiles,
                CompositeMissingTiles = missingTiles,
                PendingProjectionUpdates = pendingProjectionUpdates
            };
        }
        MaybeLog();
    }

    private static void MaybeLog()
    {
        var now = Environment.TickCount64;
        var previous = Interlocked.Read(ref _lastLogTicks);
        if (now - previous < 1000) return;
        if (Interlocked.CompareExchange(ref _lastLogTicks, now, previous) != previous) return;

        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var s = Snapshot;
            File.AppendAllText(LogPath,
                $"{DateTime.UtcNow:O} brush={s.BrushMs:0.###}ms path={s.BrushPath} stamps={s.BrushStamps} cached={s.BrushCachedDabs} composite={s.CompositeMs:0.###}ms dirtyTiles={s.CompositeDirtyTiles} missingTiles={s.CompositeMissingTiles} pendingProjection={s.PendingProjectionUpdates}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
