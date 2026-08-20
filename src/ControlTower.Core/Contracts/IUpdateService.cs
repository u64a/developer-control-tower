#nullable enable
using System.Threading;
using System.Threading.Tasks;
using System;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    public interface IUpdateService
    {
        UpdateProviderKind ProviderKind { get; }

        /// <summary>
        /// Performs the full check pipeline: locate repo, validate, verify
        /// branch, fetch from origin, compare ahead/behind, classify dirty.
        /// Always returns a value; never throws on expected failure modes
        /// (offline, no upstream, dirty tree).
        /// </summary>
        Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateOptions options, CancellationToken ct);

        /// <summary>
        /// Re-runs the safety preflight (dirty/ahead/diverged), writes a
        /// temp .cmd script, and spawns a visible console via the launch
        /// service. The caller should immediately call Application.Shutdown.
        /// Returns false (with Message populated) if preflight failed at
        /// re-check time.
        /// </summary>
        Task<UpdateLaunchResult> LaunchUpdateAsync(
            UpdateCheckResult lastCheck,
            CancellationToken ct,
            IProgress<int>? progress = null);
    }

    public sealed record UpdateLaunchResult(bool Spawned, string ScriptPath, string Message);
}
