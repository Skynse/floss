using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Floss.App;

public readonly record struct RenderTelemetrySnapshot(
    double BrushMs,
    string BrushPath,
    int BrushStamps,
    int BrushCachedDabs,
    double CompositeMs,
    int CompositeDirtyTiles,
    int CompositeMissingTiles,
    int Lod,
    int PendingProjectionUpdates);

public sealed class TelemetryScope : IDisposable
{
    private readonly string _name;
    private readonly long _started;

    internal TelemetryScope(string name)
    {
        _name = name;
        _started = Stopwatch.GetTimestamp();
    }

    public void Dispose()
        => RenderTelemetry.Record(_name, ElapsedMs(_started));

    private static double ElapsedMs(long started)
        => (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
}

public static class RenderTelemetry
{
    private static readonly object Lock = new();
    private static readonly string LogPath = Path.Combine(AppPaths.AppDirectory, "render-telemetry.log");
    private static long _lastLogTicks;
    private static RenderTelemetrySnapshot _snapshot;

    public static RenderTelemetrySnapshot Snapshot
    {
        get { lock (Lock) return _snapshot; }
    }

    public static TelemetryScope Scope(string name) => new(name);

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

    public static void RecordComposite(double elapsedMs, int dirtyTiles, int missingTiles, int lod, int pendingProjectionUpdates)
    {
        lock (Lock)
        {
            _snapshot = _snapshot with
            {
                CompositeMs = elapsedMs,
                CompositeDirtyTiles = dirtyTiles,
                CompositeMissingTiles = missingTiles,
                Lod = lod,
                PendingProjectionUpdates = pendingProjectionUpdates
            };
        }
        MaybeLog();
    }

    internal static void Record(string name, double elapsedMs)
    {
        if (name == "Brush")
        {
            lock (Lock) _snapshot = _snapshot with { BrushMs = elapsedMs };
        }
        else if (name == "Composite")
        {
            lock (Lock) _snapshot = _snapshot with { CompositeMs = elapsedMs };
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
            Directory.CreateDirectory(AppPaths.AppDirectory);
            var s = Snapshot;
            File.AppendAllText(LogPath,
                $"{DateTime.UtcNow:O} brush={s.BrushMs:0.###}ms path={s.BrushPath} stamps={s.BrushStamps} cached={s.BrushCachedDabs} composite={s.CompositeMs:0.###}ms dirtyTiles={s.CompositeDirtyTiles} missingTiles={s.CompositeMissingTiles} lod={s.Lod} pendingProjection={s.PendingProjectionUpdates}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
