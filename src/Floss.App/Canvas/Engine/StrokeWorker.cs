using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Floss.App.Canvas.Engine;

/// <summary>
/// Dedicated background stroke worker — modeled on Drawpile's DP_StrokeWorker.
/// Runs brush dab computation on a persistent background thread to keep the
/// UI responsive. Uses a BlockingCollection for job dispatch and a dedicated
/// Thread (not ThreadPool) for predictable latency.
/// </summary>
public sealed class StrokeWorker : IDisposable
{
    private readonly BlockingCollection<IStrokeJob> _jobs = new(boundedCapacity: 64);
    private readonly Thread _thread;
    private volatile bool _running = true;
    private volatile bool _disposed;

    public StrokeWorker(string name = "FlossStrokeWorker")
    {
        _thread = new Thread(RunLoop)
        {
            Name = name,
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    /// <summary>Post a job to the worker thread. Non-blocking unless queue is full.</summary>
    public void Post(IStrokeJob job)
    {
        if (_disposed) return;
        _jobs.Add(job);
    }

    /// <summary>Wait for all pending jobs to complete.</summary>
    public void Flush()
    {
        _jobs.CompleteAdding();
        // Wait for the worker to drain, then restart collection
        _thread.Join();
        if (_disposed) return;
        _running = true; // reset for reuse
    }

    private void RunLoop()
    {
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
            {
                if (!_running) break;
                job.Execute();
            }
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _jobs.CompleteAdding();
        if (_thread.IsAlive && Thread.CurrentThread != _thread)
            _thread.Join(TimeSpan.FromSeconds(2));
        _jobs.Dispose();
    }
}

/// <summary>Lightweight job interface for the stroke worker.</summary>
public interface IStrokeJob
{
    void Execute();
}

/// <summary>Reusable delegate-based job.</summary>
public sealed class DelegateStrokeJob : IStrokeJob
{
    private readonly Action _action;
    public DelegateStrokeJob(Action action) => _action = action;
    public void Execute() => _action();
}

/// <summary>Reusable delegate-based synchronous job with state.</summary>
public sealed class DelegateStrokeJob<T> : IStrokeJob
{
    private readonly Action<T> _action;
    private readonly T _state;
    public DelegateStrokeJob(Action<T> action, T state) { _action = action; _state = state; }
    public void Execute() => _action(_state);
}
