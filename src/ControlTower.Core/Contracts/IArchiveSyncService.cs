#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Maintains the local safety-net copy for every project in the
    /// <see cref="ArchiveGroup"/>. Read-only against hosted remotes: this
    /// service only ever clones/fetches, it never pushes or deletes anything
    /// remotely, and it never deletes the local copy — a vanished upstream
    /// is reported, not acted on.
    /// </summary>
    public interface IArchiveSyncService
    {
        /// <summary>
        /// Clones (first run) or fetches (subsequent runs) the full history
        /// of one archived project, then captures/verifies its latest
        /// release. Safe to call even when the remote has disappeared: the
        /// existing local copy is left untouched and the failure is
        /// reported in the result + persisted state.
        /// </summary>
        Task<ArchiveSyncResult> SyncAsync(
            ArchiveSyncRequest request,
            IProgress<string>? progress,
            CancellationToken ct);
    }

    /// <summary>
    /// Proves an archive root is disjoint from the complete registered set of
    /// local working sources. The Desktop runs this for the whole batch before
    /// invoking a per-project sync, and <see cref="IArchiveSyncService"/>
    /// repeats it as a defense in depth.
    /// </summary>
    public interface IArchiveSyncSafetyService
    {
        ArchiveSyncSafetyResult ValidateSafety(ArchiveSyncSafetyRequest request);
    }

    /// <summary>Persists <see cref="ArchiveRepoState"/> across launches.</summary>
    public interface IArchiveStateStore
    {
        ArchiveRepoState Load(string projectId);

        void Save(ArchiveRepoState state);
    }

    /// <summary>
    /// Read-only hosted-release API access used to capture the current/latest
    /// release's metadata/assets and verify their integrity before they
    /// replace a previous local copy.
    /// </summary>
    public interface IArchiveReleaseClient
    {
        /// <summary>
        /// Returns null only when the provider has confirmed the repository
        /// has no current/latest release. Lookup failures and unsupported
        /// remotes must throw so the archive can remain incomplete rather
        /// than reporting a no-release result.
        /// </summary>
        Task<ArchiveReleaseInfo?> GetLatestReleaseAsync(
            string remoteUrl,
            CancellationToken ct);

        /// <summary>
        /// Downloads an asset to <paramref name="destinationPath"/> and
        /// verifies it against <see cref="ArchiveReleaseAsset.DigestSha256"/>
        /// when present.
        /// </summary>
        Task<ArchiveReleaseIntegrity> DownloadAndVerifyAsync(
            ArchiveReleaseAsset asset,
            string destinationPath,
            CancellationToken ct);

        /// <summary>
        /// Resolves the root (non-fork) upstream repo for
        /// <paramref name="remoteUrl"/> when the release provider exposes that
        /// relationship. Returns null when the remote isn't a supported fork, or
        /// the lookup fails — callers then fall back to treating the remote as
        /// its own source of truth.
        /// </summary>
        Task<ArchiveUpstreamRepo?> ResolveUpstreamAsync(
            string remoteUrl,
            CancellationToken ct);
    }
}
