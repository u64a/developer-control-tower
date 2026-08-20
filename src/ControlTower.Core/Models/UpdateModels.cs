#nullable enable
namespace ControlTower.Core.Models
{
    public enum UpdateProviderKind
    {
        SourceRepository,
        PackagedRelease
    }

    /// <summary>
    /// Outcome of an <c>IUpdateService.CheckForUpdatesAsync</c> call. The
    /// service is total — every expected failure mode (offline, dirty tree,
    /// no upstream) is represented as a status value rather than as an
    /// exception so the chip / dialog can always render a calm result.
    /// </summary>
    public enum UpdateStatus
    {
        Unknown,
        UpToDate,
        UpdateAvailable,
        NotEligible,
        RepoNotFound,
        InvalidRepoRoot,
        NoUpstream,
        WrongBranch,
        DirtyTree,
        AheadOfOrigin,
        Diverged,
        FetchFailed
    }

    public sealed record UpdateCheckResult(
        UpdateStatus Status,
        string CurrentSha,
        string RemoteSha,
        string Branch,
        string ConfiguredBranch,
        int CommitsBehind,
        int CommitsAhead,
        string RepoRoot,
        string ExecutablePath,
        string Message,
        UpdateProviderKind Provider = UpdateProviderKind.SourceRepository,
        string CurrentVersion = "",
        string TargetVersion = "",
        string Channel = "",
        string Source = "",
        string Artifact = "");

    public sealed record UpdateOptions(
        string Branch,
        bool AutoCheckOnLaunch,
        string RepoRootOverride)
    {
        public static UpdateOptions Defaults()
            => new UpdateOptions("main", true, string.Empty);
    }
}
