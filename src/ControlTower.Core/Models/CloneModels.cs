#nullable enable
namespace ControlTower.Core.Models
{
    public sealed record CloneRequest(
        string RemoteUrl,
        string DestinationPath,
        string? Branch = null,
        bool SingleBranch = true);

    /// <summary>
    /// A progress event emitted while a clone is running. <see cref="Stage"/>
    /// is a short tag (e.g. "receiving", "resolving", "counting"); messages
    /// are pre-redacted by the underlying process adapter.
    /// </summary>
    public sealed record CloneProgress(
        string Stage,
        double? PercentComplete,
        string Message);

    public enum CloneStatus
    {
        Succeeded,
        Failed,
        Cancelled
    }

    /// <summary>
    /// Structured failure reason for a clone attempt. <see cref="None"/>
    /// indicates success.
    /// </summary>
    public enum CloneError
    {
        None,
        CredentialInUrl,
        DestinationNotEmpty,
        ParentNotCreateable,
        GitNotFound,
        TimedOut,
        CommandFailed,
        InvalidUrl
    }

    public sealed record CloneResult(
        CloneStatus Status,
        CloneError Error,
        string Message,
        string? ResolvedBranch,
        string? CommitSha)
    {
        public bool Success => Status == CloneStatus.Succeeded;

        public static CloneResult Failure(CloneError error, string message) =>
            new(CloneStatus.Failed, error, message, null, null);

        public static CloneResult CancelledResult(string message) =>
            new(CloneStatus.Cancelled, CloneError.None, message, null, null);

        public static CloneResult Ok(string? branch, string? sha, string message) =>
            new(CloneStatus.Succeeded, CloneError.None, message, branch, sha);
    }
}
