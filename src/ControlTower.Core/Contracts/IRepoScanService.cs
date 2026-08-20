#nullable enable
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Walks one or more root folders looking for <c>.git</c> directories
    /// or bare repository shapes and returns a flat list of
    /// <see cref="ScanCandidate"/> rows ready for the user to pick from
    /// in the Scan-and-Register dialog. Pure inspection: no mutation,
    /// no network IO, classification through
    /// <see cref="IGitWorkspaceInspector"/>.
    /// </summary>
    public interface IRepoScanService
    {
        Task<ScanResult> ScanAsync(
            IReadOnlyList<string> rootPaths,
            ScanOptions options,
            System.IProgress<ScanProgressUpdate>? progress,
            CancellationToken ct);
    }
}
