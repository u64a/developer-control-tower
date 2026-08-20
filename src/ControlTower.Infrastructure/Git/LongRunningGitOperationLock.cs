#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;

namespace ControlTower.Infrastructure.Git
{
    /// <summary>
    /// Process-wide implementation of <see cref="ILongRunningGitOperationLock"/>.
    /// Single permit; AcquireAsync waits its turn; the returned IDisposable
    /// releases on Dispose.
    /// </summary>
    public sealed class LongRunningGitOperationLock : ILongRunningGitOperationLock, IDisposable
    {
        private readonly SemaphoreSlim _semaphore = new SemaphoreSlim(1, 1);

        public bool IsHeld => _semaphore.CurrentCount == 0;

        public async Task<IDisposable> AcquireAsync(CancellationToken ct)
        {
            await _semaphore.WaitAsync(ct).ConfigureAwait(false);
            return new Releaser(_semaphore);
        }

        public void Dispose() => _semaphore.Dispose();

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim? _semaphore;

            public Releaser(SemaphoreSlim semaphore)
            {
                _semaphore = semaphore;
            }

            public void Dispose()
            {
                var s = System.Threading.Interlocked.Exchange(ref _semaphore, null);
                if (s != null)
                {
                    try { s.Release(); } catch { /* disposed */ }
                }
            }
        }
    }
}
