#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Async, cancellable, progress-emitting <c>git clone</c> primitive.
    /// Rejects credential-bearing URLs (Git Credential Manager / SSH
    /// agent must supply the credentials) and refuses to overwrite a
    /// non-empty destination.
    ///
    /// <para>Cancellation note: when a clone is cancelled, partially
    /// downloaded content is intentionally left in place. The caller
    /// decides whether to delete it; leaving the half-written .git is
    /// a clear signal that something went wrong, while auto-clean
    /// would hide bugs.</para>
    /// </summary>
    public interface IAsyncCloneService
    {
        Task<CloneResult> CloneAsync(
            CloneRequest request,
            IProgress<CloneProgress>? progress,
            CancellationToken ct);
    }
}
