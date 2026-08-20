#nullable enable
using System.Collections.Generic;

namespace ControlTower.Core.Models
{
    /// <summary>
    /// Classification produced by <see cref="Contracts.IMissingProjectScanner"/>
    /// for a project's expected local clone location. Drives both the
    /// per-row UX in the Restore dialog and the actions
    /// <see cref="Contracts.IRestoreOrchestrator"/> is allowed to take.
    /// </summary>
    public enum RestoreClassification
    {
        /// <summary>Folder does not exist on disk; remote URL is known; safe to clone.</summary>
        Missing,

        /// <summary>
        /// Folder does not exist on disk AND no remote URL is known anywhere
        /// (no cache, no project.yml). User must supply a URL inline before
        /// the row can be selected for clone.
        /// </summary>
        MissingNeedsUrl,

        /// <summary>Folder exists with no entries; safe to clone.</summary>
        EmptyFolder,

        /// <summary>
        /// Folder has content but no <c>.git</c> directory. Requires the
        /// user to pick between Skip and Quarantine &amp; clone.
        /// </summary>
        ConflictNonEmpty,

        /// <summary>
        /// Folder is already a working tree whose canonical origin URL
        /// matches the project's declared remote. Nothing to do.
        /// </summary>
        AlreadyCloned,

        /// <summary>
        /// Folder is a repo we will not overwrite: origin mismatch, bare,
        /// shallow, partial, sparse, has worktrees, or has submodules.
        /// </summary>
        UnsafeExisting,
    }

    /// <summary>
    /// User-chosen action for a <see cref="RestoreCandidate"/> in the
    /// pre-batch selection grid.
    /// </summary>
    public enum RestoreAction
    {
        /// <summary>Do nothing for this candidate.</summary>
        Skip,

        /// <summary>Clone directly into the expected path.</summary>
        Clone,

        /// <summary>
        /// Move the non-empty source into the quarantine root first,
        /// then clone into the now-empty expected path.
        /// </summary>
        QuarantineAndClone,
    }

    /// <summary>
    /// Live state of a single row in the Restore dialog. Emitted by the
    /// orchestrator through <see cref="System.IProgress{T}"/> of
    /// <see cref="RestoreRowUpdate"/>.
    /// </summary>
    public enum RestoreRowState
    {
        /// <summary>Pre-batch state: scanner produced this row but no action has started.</summary>
        Idle,
        Pending,
        Quarantining,
        Cloning,
        Done,
        Failed,
        Skipped,
        AlreadyCloned,
        UnsafeExisting,
    }

    /// <summary>
    /// Input fed into <see cref="Contracts.IMissingProjectScanner"/>.
    /// Callers (the WPF dialog VM) build one of these per project they
    /// want considered; non-local-store projects can be filtered up
    /// front or via <see cref="IsLocalStore"/>.
    /// </summary>
    public sealed record ProjectRestoreInput(
        string ProjectId,
        string ProjectName,
        string Slug,
        string ExpectedPath,
        string RemoteUrl,
        bool IsLocalStore);

    /// <summary>
    /// A classified candidate produced by the scanner. Carries everything
    /// the orchestrator needs to act without re-touching the filesystem
    /// or the project providers.
    /// </summary>
    public sealed record RestoreCandidate(
        string ProjectId,
        string ProjectName,
        string Slug,
        string ExpectedPath,
        string RemoteUrl,
        string CanonicalRemoteUrl,
        RestoreClassification Classification,
        string Detail);

    /// <summary>User selection submitted to <see cref="Contracts.IRestoreOrchestrator"/>.</summary>
    public sealed record RestoreSelection(RestoreCandidate Candidate, RestoreAction Action);

    /// <summary>
    /// Live update for a single row during a restore batch. The
    /// orchestrator emits these via <see cref="System.IProgress{T}"/>;
    /// the dialog VM applies them to the matching row by
    /// <see cref="ProjectId"/>.
    /// </summary>
    public sealed record RestoreRowUpdate(
        string ProjectId,
        RestoreRowState State,
        double? PercentComplete,
        string? Detail,
        string? QuarantinePath,
        string? ErrorCode,
        string? ErrorMessage);
}
