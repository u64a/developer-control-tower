#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Classifies the expected local clone location for each input
    /// project as one of <see cref="RestoreClassification"/> and returns
    /// the actionable subset (projects with a remote URL backed by a
    /// local store).
    /// </summary>
    /// <remarks>
    /// SSH-store projects and projects with no <c>RemoteUrl</c> are
    /// filtered out of the result. AlreadyCloned and UnsafeExisting rows
    /// ARE returned so the dialog can show them; the UI is responsible
    /// for blocking selection on those rows.
    /// </remarks>
    public interface IMissingProjectScanner
    {
        Task<IReadOnlyList<RestoreCandidate>> ScanAsync(
            IReadOnlyList<ProjectRestoreInput> projects,
            CancellationToken ct);
    }
}
