#nullable enable
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Pure inspector for a folder that may or may not be a git
    /// repository. Performs no mutation and no network IO; all queries
    /// run via local plumbing commands through
    /// <see cref="IGitProcessAdapter"/>.
    /// </summary>
    public interface IGitWorkspaceInspector
    {
        /// <summary>
        /// Decides whether <paramref name="path"/> is a working tree,
        /// a bare repo, or not a repo at all. Always returns a value;
        /// never throws on a missing or non-repo path.
        /// </summary>
        Task<GitWorkspaceClassification> ClassifyAsync(string path, CancellationToken ct);

        /// <summary>
        /// Reads the git status of a working tree, separating modified,
        /// staged, untracked-not-ignored and ignored entries, plus the
        /// ahead/behind counts relative to the upstream when one
        /// exists.
        /// </summary>
        Task<GitStatusBuckets> ReadStatusAsync(string workingTreePath, CancellationToken ct);

        /// <summary>
        /// Reads the strict branch, HEAD, status, and origin tracking state
        /// required before relocation can clone or delete a source.
        /// Command failures and missing upstream data are returned as an
        /// explicit failure rather than clean-looking empty buckets.
        /// </summary>
        Task<RelocationGitState> ReadRelocationStateAsync(
            string workingTreePath,
            CancellationToken ct) =>
            Task.FromResult(RelocationGitState.Failure(
                "This Git inspector does not support relocation-safe verification."));

        /// <summary>
        /// Returns a canonical, credential-free, lower-case-host form
        /// of a git remote URL. The result is stable across the common
        /// equivalent forms (scp-like <c>git@host:org/repo</c>,
        /// <c>HTTP URL containing user-info credentials</c>,
        /// <c>ssh://git@host/org/repo</c>, etc.).
        /// </summary>
        string CanonicalizeRemote(string remote);

        /// <summary>
        /// Returns a scheme-independent identity for a git remote URL,
        /// suitable for dedupe comparisons. Strips user-info, lower-cases
        /// the host, strips a single trailing <c>.git</c>, and drops the
        /// scheme. Returns an empty string for null/whitespace input.
        /// Example: both <c>https://github.com/x/y.git</c> and
        /// <c>git@github.com:x/y</c> return <c>github.com/x/y</c>.
        /// </summary>
        string GetRemoteIdentity(string remote);
    }
}
