#nullable enable
using System.Collections.Generic;

namespace ControlTower.Core.Models
{
    /// <summary>
    /// Result of inspecting a folder to determine whether it is a git
    /// repository and, if so, what shape. Pure inspection: no mutation.
    /// </summary>
    public abstract record GitWorkspaceClassification(string Path);

    /// <summary>Folder has no <c>.git</c> directory or file.</summary>
    public sealed record NotARepo(string Path)
        : GitWorkspaceClassification(Path);

    /// <summary>
    /// A normal working tree. <see cref="Remotes"/> is empty when the repo
    /// has no remotes configured (e.g. fresh <c>git init</c>).
    /// </summary>
    public sealed record WorkingTreeRepo(
        string Path,
        string GitDir,
        string Branch,
        bool IsDetached,
        bool IsShallow,
        bool IsSparse,
        bool IsPartialClone,
        bool HasWorktrees,
        bool HasSubmodules,
        string? OriginUrl,
        IReadOnlyList<GitRemote> Remotes)
        : GitWorkspaceClassification(Path);

    /// <summary>A bare repository (no working tree).</summary>
    public sealed record BareRepo(
        string Path,
        string GitDir,
        IReadOnlyList<GitRemote> Remotes)
        : GitWorkspaceClassification(Path);

    public sealed record GitRemote(string Name, string FetchUrl, string PushUrl);

    /// <summary>
    /// Status buckets produced by <c>git status --porcelain=v2</c> and
    /// related plumbing. Filenames are repo-relative, slash-separated.
    /// </summary>
    public sealed record GitStatusBuckets(
        IReadOnlyList<string> Modified,
        IReadOnlyList<string> Staged,
        IReadOnlyList<string> UntrackedNotIgnored,
        IReadOnlyList<string> IgnoredFiles,
        int? AheadOfOrigin,
        int? BehindOrigin)
    {
        /// <summary>
        /// True when there is no local work that would be lost: no modified
        /// tracked files, no staged changes, no untracked-and-not-ignored
        /// files, and (if upstream is known) no unpushed commits.
        /// </summary>
        public bool IsClean =>
            Modified.Count == 0 &&
            Staged.Count == 0 &&
            UntrackedNotIgnored.Count == 0 &&
            (AheadOfOrigin ?? 0) == 0;
    }
}
