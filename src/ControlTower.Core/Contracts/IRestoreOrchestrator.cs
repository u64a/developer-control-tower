#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Drives a batch of <see cref="RestoreSelection"/>s through
    /// quarantine (when requested) and <see cref="IAsyncCloneService"/>.
    /// </summary>
    /// <remarks>
    /// <para>The orchestrator holds <see cref="ILongRunningGitOperationLock"/>
    /// for the duration of the batch so other long-running git
    /// operations (Restore / future Relocate / Scan) cannot interleave.</para>
    /// <para>Cancellation aborts the in-flight row's clone; remaining
    /// selections are not started. Partially cloned content is left in
    /// place per <see cref="IAsyncCloneService"/>'s contract — the
    /// orchestrator never auto-cleans.</para>
    /// </remarks>
    public interface IRestoreOrchestrator
    {
        Task RestoreAsync(
            IReadOnlyList<RestoreSelection> selections,
            IProgress<RestoreRowUpdate>? progress,
            CancellationToken ct);
    }
}
