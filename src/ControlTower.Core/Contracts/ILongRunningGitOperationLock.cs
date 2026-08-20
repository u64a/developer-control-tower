#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Process-wide mutual-exclusion for long-running git operations
    /// (Phase A Restore today; Phase B Relocate and Phase C Scan
    /// tomorrow). Only one holder at a time; while held, other UI
    /// surfaces that would also acquire it should disable.
    /// </summary>
    /// <remarks>
    /// Dispose of the returned handle to release. Awaiting
    /// <see cref="AcquireAsync(CancellationToken)"/> with a cancelled
    /// token throws <see cref="OperationCanceledException"/>.
    /// </remarks>
    public interface ILongRunningGitOperationLock
    {
        Task<IDisposable> AcquireAsync(CancellationToken ct);

        /// <summary>True while the lock is currently held by anyone.</summary>
        bool IsHeld { get; }
    }
}
