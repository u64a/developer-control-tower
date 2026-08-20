using ControlTower.Core.Validation;

namespace ControlTower.Core.Models
{
    public enum LaunchTargetKind
    {
        Code,
        CodeAdmin,
        RemoteCode,
        GitHub,
        Ado,
        PrimaryDoc,
        Plan
    }

    public enum LaunchStatus
    {
        Ok,
        Unconfigured,
        Rejected,
        Unavailable,
        Failed
    }

    public sealed class LaunchResult
    {
        public LaunchResult()
        {
            Success = false;
            Message = string.Empty;
            Status = LaunchStatus.Failed;
        }

        public bool Success { get; set; }

        public string Message { get; set; }

        public LaunchStatus Status { get; set; }

        /// <summary>
        /// Optional structured issue carrying a machine-readable code such as
        /// <c>launch/rejected/scheme</c> per ADR-004. Null when the result has
        /// no structured issue (e.g. the launch succeeded).
        /// </summary>
        public ValidationIssue Issue { get; set; }

        public static LaunchResult Ok(string message)
            => new() { Success = true, Status = LaunchStatus.Ok, Message = message ?? string.Empty };

        public static LaunchResult Unconfigured(string message)
            => new() { Success = false, Status = LaunchStatus.Unconfigured, Message = message ?? string.Empty };

        public static LaunchResult Rejected(string code, string message)
            => new()
            {
                Success = false,
                Status = LaunchStatus.Rejected,
                Message = message ?? string.Empty,
                Issue = new ValidationIssue(IssueSeverity.Error, code ?? string.Empty, message ?? string.Empty)
            };

        public static LaunchResult Unavailable(string code, string message)
            => new()
            {
                Success = false,
                Status = LaunchStatus.Unavailable,
                Message = message ?? string.Empty,
                Issue = new ValidationIssue(IssueSeverity.Error, code ?? string.Empty, message ?? string.Empty)
            };

        public static LaunchResult Failed(string message)
            => new() { Success = false, Status = LaunchStatus.Failed, Message = message ?? string.Empty };
    }
}
