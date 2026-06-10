using System;
using System.Threading;

namespace Floss.App.Document;

public sealed class DocumentRenderLock : IDisposable
{
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

    public IDisposable Read()
    {
        _lock.EnterReadLock();
        return new Releaser(_lock, write: false);
    }

    public IDisposable Write()
    {
        _lock.EnterWriteLock();
        return new Releaser(_lock, write: true);
    }

    public void Dispose() => _lock.Dispose();

    private sealed class Releaser(ReaderWriterLockSlim owner, bool write) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (write)
                owner.ExitWriteLock();
            else
                owner.ExitReadLock();
        }
    }
}
