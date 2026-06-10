using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Floss.App.Canvas.Engine;

/// <summary>
/// Persistent thread pool for tile compositing — modeled on .
/// Eliminates TPL Parallel.For overhead by keeping N threads alive with a
/// semaphore-driven job queue. Each thread processes tile compositing jobs
/// without thread pool injection latency.
/// </summary>
public sealed class RenderThreadPool : IDisposable
{
    private readonly BlockingCollection<Action> _jobs;
    private readonly Thread[] _threads;
    private volatile bool _disposed;

    public int ThreadCount => _threads.Length;

    public RenderThreadPool(int threadCount, string name = "FlossRender")
    {
        if (threadCount < 1) threadCount = 1;
        _jobs = new BlockingCollection<Action>(boundedCapacity: 8192);
        _threads = new Thread[threadCount];

        for (int i = 0; i < threadCount; i++)
        {
            var thread = new Thread(RunLoop)
            {
                Name = $"{name}_{i}",
                IsBackground = true,
                Priority = ThreadPriority.Normal
            };
            _threads[i] = thread;
            thread.Start();
        }
    }

    /// <summary>Create a pool with sensible defaults for the current machine.</summary>
    public static RenderThreadPool Create(string name = "FlossRender")
    {
        var count = Math.Max(1, Environment.ProcessorCount - 1);
        return new RenderThreadPool(count, name);
    }

    /// <summary>Enqueue a job. Blocks if queue is full (backpressure).</summary>
    public void Enqueue(Action job)
    {
        if (_disposed) return;
        _jobs.Add(job);
    }

    /// <summary>Wait until all queued jobs are complete.</summary>
    public void WaitAll()
    {
        // Submit a sentinel job for each thread, then wait
        var barrier = new Barrier(_threads.Length + 1);
        for (int i = 0; i < _threads.Length; i++)
            _jobs.Add(() => barrier.SignalAndWait());
        barrier.SignalAndWait();
        barrier.Dispose();
    }

    private void RunLoop()
    {
        try
        {
            foreach (var job in _jobs.GetConsumingEnumerable())
                job();
        }
        catch (ObjectDisposedException) { }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _jobs.CompleteAdding();

        foreach (var thread in _threads)
        {
            if (thread.IsAlive && Thread.CurrentThread != thread)
                thread.Join(TimeSpan.FromSeconds(2));
        }

        _jobs.Dispose();
    }
}
