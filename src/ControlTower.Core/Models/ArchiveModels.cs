#nullable enable
using System;
using System.Collections.Generic;

namespace ControlTower.Core.Models
{
    /// <summary>
    /// Reserved <see cref="ProjectDefinition.Group"/> value. Projects in this
    /// group get an additional local-safety-net copy maintained by
    /// <c>IArchiveSyncService</c>: a full clone (all branches/tags) kept
    /// outside the Velopack install directory, refreshed on launch, and
    /// never deleted locally even if the upstream remote disappears.
    /// </summary>
    public static class ArchiveGroup
    {
        public const string Name = "Archive";

        public static bool Matches(string? group) =>
            string.Equals(group?.Trim(), Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Coarse status of an archived project's upstream remote.</summary>
    public enum ArchiveUpstreamStatus
    {
        /// <summary>No sync has completed yet.</summary>
        Unknown = 0,

        /// <summary>Last sync reached the remote successfully.</summary>
        Reachable,

        /// <summary>
        /// Last sync could not read the remote (404 / repository not found /
        /// access revoked) — the signature of a takedown or upstream
        /// deletion. The local copy is left untouched and flagged.
        /// </summary>
        Unavailable
    }

    /// <summary>Outcome of the most recent release-asset integrity check.</summary>
    public enum ArchiveReleaseIntegrity
    {
        /// <summary>No release has been captured yet.</summary>
        NotCaptured = 0,

        /// <summary>Downloaded asset(s) matched the GitHub-reported SHA256 digest.</summary>
        Verified,

        /// <summary>
        /// GitHub did not report a digest for the asset (pre-June-2025 upload)
        /// so the previous release was kept rather than trusting an
        /// unverified download.
        /// </summary>
        Unverifiable,

        /// <summary>Downloaded bytes did not match the reported digest.</summary>
        Mismatch
    }

    /// <summary>
    /// Durable per-project archive sync state. Persisted outside the
    /// Velopack install directory alongside other local, disposable state
    /// (it is a cache of git/GitHub facts, not a source of truth).
    /// </summary>
    public sealed class ArchiveRepoState
    {
        public ArchiveRepoState()
        {
            ProjectId = string.Empty;
            ArchivePath = string.Empty;
            LastSyncedCommitSha = string.Empty;
            LastError = string.Empty;
            LastReleaseTag = string.Empty;
            AutoSyncEnabled = true;
        }

        public string ProjectId { get; set; }

        /// <summary>Local filesystem path of the full clone backing this archive entry.</summary>
        public string ArchivePath { get; set; }

        public DateTime? LastSyncedUtc { get; set; }

        public string LastSyncedCommitSha { get; set; }

        public ArchiveUpstreamStatus UpstreamStatus { get; set; }

        public string LastError { get; set; }

        /// <summary>Per-repo opt-out. Once a repo is confirmed gone there is no point re-checking every launch.</summary>
        public bool AutoSyncEnabled { get; set; }

        public string LastReleaseTag { get; set; }

        public ArchiveReleaseIntegrity LastReleaseIntegrity { get; set; }

        public DateTime? LastReleaseCapturedUtc { get; set; }

        /// <summary>
        /// "owner/repo" of the root (non-fork) upstream this archive's local
        /// branch and captured release track, when the remote is a GitHub
        /// fork. Empty when the remote isn't a fork (archive tracks its own
        /// remote instead).
        /// </summary>
        public string UpstreamFullName { get; set; } = string.Empty;
    }

    /// <summary>
    /// The root (non-fork) GitHub repo a fork descends from, as reported by
    /// the GitHub API's "source" field. Used so an archived fork's local
    /// clone tracks the original project's default branch and releases
    /// instead of the (never-pushed-to) fork remote.
    /// </summary>
    public sealed record ArchiveUpstreamRepo(string Owner, string Repo, string CloneUrl, string DefaultBranch)
    {
        public string FullName => Owner + "/" + Repo;
    }

    /// <summary>One release asset as reported by the GitHub Releases API.</summary>
    public sealed record ArchiveReleaseAsset(
        string Name,
        string DownloadUrl,
        long SizeBytes,
        string? DigestSha256);

    /// <summary>Latest-release metadata + assets for a repo, as captured from GitHub.</summary>
    public sealed record ArchiveReleaseInfo(
        string TagName,
        string Name,
        string Body,
        DateTime? PublishedAtUtc,
        IReadOnlyList<ArchiveReleaseAsset> Assets);

    /// <summary>Request to sync (clone-or-fetch) one archived project.</summary>
    public sealed record ArchiveSyncRequest(
        string ProjectId,
        string RemoteUrl,
        string ArchiveStorageRoot);

    public enum ArchiveSyncStatus
    {
        Succeeded,
        UpstreamUnavailable,
        Failed,
        Skipped
    }

    public sealed record ArchiveSyncResult(
        string ProjectId,
        ArchiveSyncStatus Status,
        string Message,
        string? CommitSha);

    /// <summary>Live progress event for the Desktop status bar during an archive sync pass.</summary>
    public sealed record ArchiveSyncProgress(
        int CompletedCount,
        int TotalCount,
        string CurrentProjectId,
        string CurrentProjectDisplayName);
}
