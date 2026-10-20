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
        /// The provider did not report a digest, but the downloaded asset was
        /// captured locally without a provider checksum.
        /// </summary>
        Unverifiable,

        /// <summary>Downloaded bytes did not match the reported digest.</summary>
        Mismatch
    }

    /// <summary>Whether the current/latest release was checked and completely captured.</summary>
    public enum ArchiveReleaseStatus
    {
        /// <summary>No explicit release check has completed (including legacy state).</summary>
        NotChecked = 0,

        /// <summary>The provider confirmed that the repository has no current release.</summary>
        NoCurrentRelease,

        /// <summary>The current release and every reported asset were captured locally.</summary>
        Captured,

        /// <summary>A release lookup, download, or integrity check did not complete.</summary>
        Incomplete,

        /// <summary>The remote cannot be checked through a supported release API.</summary>
        Unsupported
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
            PreviousArchivePaths = new List<string>();
            LastSyncedCommitSha = string.Empty;
            LastError = string.Empty;
            LastReleaseTag = string.Empty;
            LastReleaseDirectoryName = string.Empty;
            LastReleaseError = string.Empty;
            LastReleaseAssetNames = new List<string>();
            AutoSyncEnabled = true;
        }

        public string ProjectId { get; set; }

        /// <summary>Local filesystem path of the full clone backing this archive entry.</summary>
        public string ArchivePath { get; set; }

        /// <summary>
        /// Prior archive clone locations retained when the global storage root
        /// changes. They are references only: the sync service never moves,
        /// deletes, or updates those directories.
        /// </summary>
        public IList<string> PreviousArchivePaths { get; }

        public DateTime? LastSyncedUtc { get; set; }

        public string LastSyncedCommitSha { get; set; }

        public ArchiveUpstreamStatus UpstreamStatus { get; set; }

        public string LastError { get; set; }

        /// <summary>Per-repo opt-out. Once a repo is confirmed gone there is no point re-checking every launch.</summary>
        public bool AutoSyncEnabled { get; set; }

        public string LastReleaseTag { get; set; }

        /// <summary>Whether the latest release was checked and captured during a completed sync.</summary>
        public ArchiveReleaseStatus ReleaseStatus { get; set; }

        public ArchiveReleaseIntegrity LastReleaseIntegrity { get; set; }

        public DateTime? LastReleaseCapturedUtc { get; set; }

        /// <summary>Directory name below the archive's releases root for the current capture.</summary>
        public string LastReleaseDirectoryName { get; set; }

        /// <summary>Locally stored asset file names expected in the current release directory.</summary>
        public IList<string> LastReleaseAssetNames { get; }

        /// <summary>Actionable reason why the latest release is not completely captured.</summary>
        public string LastReleaseError { get; set; }

        /// <summary>
        /// "owner/repo" of the root (non-fork) upstream this archive's local
        /// branch and captured release track, when the remote is a GitHub
        /// fork. Empty when the remote isn't a fork (archive tracks its own
        /// remote instead).
        /// </summary>
        public string UpstreamFullName { get; set; } = string.Empty;
    }

    /// <summary>
    /// The root (non-fork) hosted repo a fork descends from, when exposed by
    /// its release provider. Used so an archived fork's local clone tracks
    /// the original project's default branch and releases instead of the
    /// (never-pushed-to) fork remote.
    /// </summary>
    public sealed record ArchiveUpstreamRepo(string Owner, string Repo, string CloneUrl, string DefaultBranch)
    {
        public string FullName => Owner + "/" + Repo;
    }

    /// <summary>One release asset as reported by a hosted-release API.</summary>
    public sealed record ArchiveReleaseAsset(
        string Name,
        string DownloadUrl,
        long SizeBytes,
        string? DigestSha256);

    /// <summary>Current/latest release metadata + assets captured from a hosted-release API.</summary>
    public sealed record ArchiveReleaseInfo(
        string TagName,
        string Name,
        string Body,
        DateTime? PublishedAtUtc,
        IReadOnlyList<ArchiveReleaseAsset> Assets);

    /// <summary>
    /// A resolved local working-source location registered in the portfolio.
    /// Remote-only projects do not appear here because they have no local
    /// working folder to protect.
    /// </summary>
    public sealed record ArchiveWorkingSource(string ProjectId, string LocalPath);

    /// <summary>
    /// The complete portfolio-derived set of local working-source locations.
    /// A missing or failed inventory is deliberately distinguishable from a
    /// complete inventory with no local sources so archive sync can fail
    /// closed rather than silently bypassing the source-safety check.
    /// </summary>
    public sealed class ArchiveWorkingSourceInventory
    {
        public ArchiveWorkingSourceInventory(
            IReadOnlyList<ArchiveWorkingSource>? workingSources,
            string? failureMessage = null)
        {
            WorkingSources = workingSources;
            FailureMessage = failureMessage ?? string.Empty;
        }

        public IReadOnlyList<ArchiveWorkingSource>? WorkingSources { get; }

        public string FailureMessage { get; }

        public bool IsComplete =>
            WorkingSources != null && string.IsNullOrWhiteSpace(FailureMessage);

        public static ArchiveWorkingSourceInventory Complete(
            IReadOnlyList<ArchiveWorkingSource>? workingSources) =>
            new ArchiveWorkingSourceInventory(workingSources ?? Array.Empty<ArchiveWorkingSource>());

        public static ArchiveWorkingSourceInventory Failed(string failureMessage) =>
            new ArchiveWorkingSourceInventory(null, failureMessage);
    }

    /// <summary>Input to the archive source-safety preflight.</summary>
    public sealed record ArchiveSyncSafetyRequest(
        string ArchiveStorageRoot,
        ArchiveWorkingSourceInventory? WorkingSourceInventory);

    /// <summary>Result of proving that the archive root is disjoint from all working sources.</summary>
    public sealed record ArchiveSyncSafetyResult(
        bool IsSafe,
        string EffectiveArchiveStorageRoot,
        string Message);

    /// <summary>Request to sync (clone-or-fetch) one archived project.</summary>
    public sealed record ArchiveSyncRequest(
        string ProjectId,
        string RemoteUrl,
        string ArchiveStorageRoot)
    {
        /// <summary>
        /// Required complete inventory of registered local working sources.
        /// A request without it is rejected by the sync service.
        /// </summary>
        public ArchiveWorkingSourceInventory? WorkingSourceInventory { get; init; }

        public ArchiveSyncRequest(
            string projectId,
            string remoteUrl,
            string archiveStorageRoot,
            ArchiveWorkingSourceInventory? workingSourceInventory)
            : this(projectId, remoteUrl, archiveStorageRoot)
        {
            WorkingSourceInventory = workingSourceInventory;
        }
    }

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
