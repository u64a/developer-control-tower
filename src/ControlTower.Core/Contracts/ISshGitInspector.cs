#nullable enable
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// SSH-side counterpart of <see cref="IGitWorkspaceInspector"/>. Runs
    /// the same plumbing commands (<c>rev-parse</c>, <c>remote -v</c>,
    /// <c>status --porcelain=v2 --branch</c>) on a remote host through
    /// <see cref="ISshService"/> and parses the output into the same
    /// shapes the local inspector produces so Relocate can treat both
    /// sources uniformly.
    /// </summary>
    public interface ISshGitInspector
    {
        Task<GitWorkspaceClassification> ClassifyAsync(
            string host,
            int port,
            string user,
            string password,
            string remotePath,
            CancellationToken ct);

        Task<GitStatusBuckets> ReadStatusAsync(
            string host,
            int port,
            string user,
            string password,
            string remotePath,
            CancellationToken ct);

        Task<RelocationGitState> ReadRelocationStateAsync(
            string host,
            int port,
            string user,
            string password,
            string remotePath,
            CancellationToken ct) =>
            Task.FromResult(RelocationGitState.Failure(
                "This SSH Git inspector does not support relocation-safe verification."));
    }
}
