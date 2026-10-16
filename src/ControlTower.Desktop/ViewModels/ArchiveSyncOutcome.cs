#nullable enable
using System;
using System.Collections.Generic;

namespace ControlTower.Desktop.ViewModels
{
    /// <summary>
    /// Why a user-requested <see cref="MainViewModel.RunArchiveSyncAsync"/>
    /// pass ended the way it did. Exposed as a typed result
    /// (<see cref="MainViewModel.LastArchiveSyncOutcome"/> and
    /// <see cref="MainViewModel.ArchiveSyncCompleted"/>) so the UI and tests
    /// can react to the actual outcome instead of parsing
    /// <see cref="MainViewModel.ArchiveSyncStatusText"/>.
    /// </summary>
    public enum ArchiveSyncOutcomeKind
    {
        /// <summary>No archive sync service is configured.</summary>
        ServiceUnavailable,

        /// <summary>No archive storage root is configured.</summary>
        InvalidStorageRoot,

        /// <summary>No Archive-group project has a remote URL to sync.</summary>
        NoEligibleTargets,

        /// <summary>
        /// The whole-batch source-safety preflight refused to run. No
        /// project was touched and no archive state was persisted.
        /// </summary>
        Blocked,

        /// <summary>
        /// The batch ran to completion (possibly with per-project
        /// failures); see <see cref="ArchiveSyncOutcome.SucceededCount"/> /
        /// <see cref="ArchiveSyncOutcome.FailedCount"/>.
        /// </summary>
        Completed
    }

    /// <summary>One archived project's sync failure, kept distinct from the aggregate count.</summary>
    public sealed class ArchiveSyncFailureDetail
    {
        public ArchiveSyncFailureDetail(string projectId, string displayName, string reason)
        {
            ProjectId = projectId ?? string.Empty;
            DisplayName = displayName ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public string ProjectId { get; }

        public string DisplayName { get; }

        public string Reason { get; }
    }

    /// <summary>
    /// Result of one <see cref="MainViewModel.RunArchiveSyncAsync"/> pass.
    /// A non-<see cref="ArchiveSyncOutcomeKind.Completed"/> outcome never
    /// started a sync and never mutated persisted archive state — it exists
    /// purely so the caller can explain the refusal (status bar, blocking
    /// dialog, or a test assertion) without re-parsing display strings.
    /// </summary>
    public sealed class ArchiveSyncOutcome
    {
        private ArchiveSyncOutcome(
            ArchiveSyncOutcomeKind kind,
            string message,
            IReadOnlyList<string>? blockedProjectIds,
            int succeededCount,
            int failedCount,
            IReadOnlyList<ArchiveSyncFailureDetail>? failures)
        {
            Kind = kind;
            Message = message ?? string.Empty;
            BlockedProjectIds = blockedProjectIds ?? Array.Empty<string>();
            SucceededCount = succeededCount;
            FailedCount = failedCount;
            Failures = failures ?? Array.Empty<ArchiveSyncFailureDetail>();
        }

        public ArchiveSyncOutcomeKind Kind { get; }

        /// <summary>Full, display-ready explanation (used verbatim by the status bar and the blocking dialog).</summary>
        public string Message { get; }

        /// <summary>Project ids shown as "Blocked" for a <see cref="ArchiveSyncOutcomeKind.Blocked"/> outcome.</summary>
        public IReadOnlyList<string> BlockedProjectIds { get; }

        public int SucceededCount { get; }

        public int FailedCount { get; }

        /// <summary>Per-project failure reasons for a <see cref="ArchiveSyncOutcomeKind.Completed"/> outcome.</summary>
        public IReadOnlyList<ArchiveSyncFailureDetail> Failures { get; }

        /// <summary>True for every outcome that is not a normal completed pass (nothing ran).</summary>
        public bool IsGuardRefusal => Kind != ArchiveSyncOutcomeKind.Completed;

        public static ArchiveSyncOutcome ServiceUnavailable(string message) =>
            new ArchiveSyncOutcome(ArchiveSyncOutcomeKind.ServiceUnavailable, message, null, 0, 0, null);

        public static ArchiveSyncOutcome InvalidStorageRoot(string message) =>
            new ArchiveSyncOutcome(ArchiveSyncOutcomeKind.InvalidStorageRoot, message, null, 0, 0, null);

        public static ArchiveSyncOutcome NoEligibleTargets(string message) =>
            new ArchiveSyncOutcome(ArchiveSyncOutcomeKind.NoEligibleTargets, message, null, 0, 0, null);

        public static ArchiveSyncOutcome Blocked(string message, IReadOnlyList<string> blockedProjectIds) =>
            new ArchiveSyncOutcome(ArchiveSyncOutcomeKind.Blocked, message, blockedProjectIds, 0, 0, null);

        public static ArchiveSyncOutcome Completed(
            int succeededCount,
            int failedCount,
            IReadOnlyList<ArchiveSyncFailureDetail> failures,
            string message) =>
            new ArchiveSyncOutcome(ArchiveSyncOutcomeKind.Completed, message, null, succeededCount, failedCount, failures);
    }
}
