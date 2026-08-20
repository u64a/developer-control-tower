#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Drives Phase B Relocate: walks a project from one store to another
    /// by full-cloning from origin into the destination, migrating the
    /// <c>.controltower/</c> metadata across, optionally copying ignored
    /// files, and finally rebinding the portfolio to the new path.
    /// </summary>
    /// <remarks>
    /// Preflight is read-only and does not take the long-running git
    /// lock. RelocateAsync acquires the lock for the duration of the run
    /// so a Restore or a parallel Relocate cannot interleave with it.
    /// </remarks>
    public interface IRelocateProjectService
    {
        Task<RelocatePreflightResult> PreflightAsync(
            RelocateRequest request,
            CancellationToken ct);

        Task<RelocateResult> RelocateAsync(
            RelocateRequest request,
            IProgress<RelocateStepUpdate>? progress,
            CancellationToken ct);

        /// <summary>
        /// Pushes any unpushed commits in the source. Used by the UI to
        /// clear an "ahead-of-origin" preflight blocker before re-running
        /// preflight.
        /// </summary>
        Task<RelocateStepUpdate> PushSourceAsync(
            RelocateRequest request,
            CancellationToken ct);
    }
}
