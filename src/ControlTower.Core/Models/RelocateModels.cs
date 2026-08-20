#nullable enable
using System.Collections.Generic;

namespace ControlTower.Core.Models
{
    /// <summary>
    /// Caller-provided description of a Relocate operation. The current
    /// project's identity and metadata (name, summary, lifecycle, remote
    /// URLs) are carried verbatim through the operation and re-applied at
    /// the Rebind step so the registration preserves what the portfolio
    /// already knows about the project.
    /// </summary>
    public sealed class RelocateRequest
    {
        public string ProjectId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string LifecycleState { get; set; } = "active";
        public string GitHubUrl { get; set; } = string.Empty;
        public string AdoUrl { get; set; } = string.Empty;
        public string RemoteUrl { get; set; } = string.Empty;

        /// <summary>
        /// Store the source currently lives in. Used to look up SSH
        /// credentials for an SSH source. May be empty for a local source
        /// when the path is enough to identify it.
        /// </summary>
        public string SourceStoreId { get; set; } = string.Empty;

        /// <summary>Local filesystem path of the source. Empty for SSH sources.</summary>
        public string SourceLocalPath { get; set; } = string.Empty;

        /// <summary>
        /// SSH target shaped as <c>user@host:path</c>. Empty for local sources.
        /// </summary>
        public string SourceSshTarget { get; set; } = string.Empty;

        public string TargetStoreId { get; set; } = string.Empty;

        /// <summary>
        /// Folder name (not a full path) for the relocated project under the
        /// target store root. Must satisfy <c>SafeFolderRegex</c> in the
        /// service.
        /// </summary>
        public string TargetFolder { get; set; } = string.Empty;

        public bool CopyIgnoredFiles { get; set; }

        public bool DeleteSourceAfterSuccess { get; set; }
    }

    /// <summary>
    /// Ordered list of steps the Relocate state machine walks. Reported back
    /// to the UI as <see cref="RelocateStepUpdate"/> values.
    /// </summary>
    public enum RelocateStep
    {
        Preflight = 0,
        CreateDestFolder = 1,
        CloneOrigin = 2,
        MigrateMetadata = 3,
        CopyIgnoredFiles = 4,
        VerifyDestination = 5,
        RebindPortfolio = 6,
        DeleteSource = 7
    }

    /// <summary>
    /// State of a single Relocate step. Steps progress Pending → Running →
    /// (Done|Failed|Skipped|Cancelled). <see cref="Warning"/> is a terminal
    /// success-with-asterisk state used by copy-ignored entries that
    /// completed with non-fatal warnings.
    /// </summary>
    public enum RelocateStepState
    {
        Pending = 0,
        Running = 1,
        Done = 2,
        Failed = 3,
        Skipped = 4,
        Cancelled = 5,
        Warning = 6
    }

    public sealed record RelocateStepUpdate(
        RelocateStep Step,
        RelocateStepState State,
        string Detail,
        double? Progress);

    /// <summary>
    /// Result of running the preflight checks for a Relocate request. The
    /// pipeline always runs end-to-end so the UI can surface every blocker
    /// in one pass instead of forcing the user to fix issues one at a time.
    /// </summary>
    public sealed class RelocatePreflightResult
    {
        public bool OkToRelocate { get; set; }
        public List<string> Issues { get; } = new List<string>();
        public List<string> Warnings { get; } = new List<string>();
        public int IgnoredFilesCount { get; set; }
        public long IgnoredFilesBytes { get; set; }
        public string ResolvedSourcePath { get; set; } = string.Empty;
        public string ResolvedTargetPath { get; set; } = string.Empty;

        /// <summary>
        /// True when the only thing stopping Relocate is unpushed commits.
        /// The UI surfaces a "Push" affordance to clear this in one click.
        /// </summary>
        public bool NeedsPush { get; set; }

        public bool SourceIsSsh { get; set; }
        public bool TargetIsSsh { get; set; }

        public int AheadOfOrigin { get; set; }
        public int BehindOfOrigin { get; set; }

        public string OriginUrl { get; set; } = string.Empty;
        public string SourceBranch { get; set; } = string.Empty;
        public string SourceHeadSha { get; set; } = string.Empty;
        public bool SourceIsWindows { get; set; }
        public bool TargetIsWindows { get; set; }
    }

    /// <summary>
    /// Relocation-only proof that a working tree can be reproduced by
    /// cloning its named branch from origin. Unlike the general scan
    /// buckets, failure is explicit so relocation can fail closed.
    /// </summary>
    public sealed record RelocationGitState(
        bool Success,
        string ErrorMessage,
        GitStatusBuckets Status,
        string Branch,
        string HeadSha,
        string OriginHeadSha,
        string Upstream,
        bool? RemoteIsWindows)
    {
        /// <summary>
        /// True only when <see cref="GitStatusBuckets.IgnoredFiles"/> contains
        /// the complete ignored-file inventory rather than a capped prefix.
        /// Relocation may continue without this proof, but source deletion
        /// must not.
        /// </summary>
        public bool IgnoredFilesInventoryComplete { get; init; }

        public static RelocationGitState Failure(string errorMessage, bool? remoteIsWindows = null) =>
            new(
                false,
                errorMessage,
                new GitStatusBuckets(
                    new List<string>(),
                    new List<string>(),
                    new List<string>(),
                    new List<string>(),
                    null,
                    null),
                string.Empty,
                string.Empty,
                string.Empty,
                string.Empty,
                remoteIsWindows)
            {
                IgnoredFilesInventoryComplete = false
            };
    }

    /// <summary>
    /// Final result of a Relocate run. Failure carries the step that broke
    /// so the caller can describe the partial state precisely.
    /// </summary>
    public sealed class RelocateResult
    {
        public bool Success { get; set; }
        public RelocateStep FailedStep { get; set; }
        public string ErrorMessage { get; set; } = string.Empty;
        public string FinalTargetPath { get; set; } = string.Empty;
        public bool Cancelled { get; set; }
    }
}
