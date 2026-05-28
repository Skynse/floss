using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Floss.App.Canvas.Engine;

/// <summary>
/// Zero-cost performance tracing modeled on Drawpile's DP_PERF system.
/// Writes Chrome Trace Event format JSON when enabled via PERF_TRACE environment variable.
/// Otherwise all calls are branch-predicted no-ops.
/// </summary>
public static class PerfTrace
{
    private static readonly bool Enabled;
    private static StreamWriter? _writer;
    private static readonly object WriteLock = new();
    private static long _eventCounter;
    private static readonly long StartTimestamp = Stopwatch.GetTimestamp();
    private static readonly double TickToMicroseconds = 1_000_000.0 / Stopwatch.Frequency;

    static PerfTrace()
    {
        var path = Environment.GetEnvironmentVariable("FLOSS_PERF_TRACE");
        Enabled = !string.IsNullOrEmpty(path);
        if (Enabled && path != null)
        {
            try
            {
                var dir = Path.GetDirectoryName(path);
                if (dir != null) Directory.CreateDirectory(dir);
                _writer = new StreamWriter(path, append: false);
                _writer.WriteLine("[");
            }
            catch { Enabled = false; _writer = null; }
        }
    }

    /// <summary>Begin a trace span. Returns a handle for End().</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long Begin(string name, string? categories = null)
    {
        if (!Enabled || _writer == null) return -1;

        var id = Interlocked.Increment(ref _eventCounter);
        var ts = (long)((Stopwatch.GetTimestamp() - StartTimestamp) * TickToMicroseconds);

        lock (WriteLock)
        {
            if (id > 1) _writer!.Write(',');
            _writer!.Write($"\n  {{\"name\":\"{name}\",\"ph\":\"B\",\"pid\":1,\"tid\":{Environment.CurrentManagedThreadId},\"ts\":{ts},\"cat\":\"{categories ?? ""}\"}}");
            _writer.Flush();
        }

        return id;
    }

    /// <summary>End a trace span. The handle comes from Begin().</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void End(long handle)
    {
        if (handle < 0 || _writer == null) return;

        var ts = (long)((Stopwatch.GetTimestamp() - StartTimestamp) * TickToMicroseconds);

        lock (WriteLock)
        {
            _writer!.Write(",\n  ");
            _writer.Write($"{{\"name\":\"\",\"ph\":\"E\",\"pid\":1,\"tid\":{Environment.CurrentManagedThreadId},\"ts\":{ts}}}");
            _writer.Flush();
        }
    }

    /// <summary>Close the trace file (called on app shutdown).</summary>
    public static void Close()
    {
        if (_writer == null) return;
        lock (WriteLock)
        {
            _writer.WriteLine("\n]");
            _writer.Dispose();
        }
    }
}

/// <summary>
/// RAII-style trace scope. Disposable struct — use with 'using' statement.
/// </summary>
public readonly ref struct TraceScope
{
    private readonly long _handle;

    public TraceScope(string name, string? categories = null)
        => _handle = PerfTrace.Begin(name, categories);

    public void Dispose() => PerfTrace.End(_handle);
}
