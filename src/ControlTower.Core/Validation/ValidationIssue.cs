namespace ControlTower.Core.Validation
{
    public enum IssueSeverity
    {
        Info,
        Warning,
        Error
    }

    public sealed class ValidationIssue
    {
        public ValidationIssue()
        {
            Severity = IssueSeverity.Warning;
            Message = string.Empty;
            Code = string.Empty;
        }

        public ValidationIssue(IssueSeverity severity, string message)
        {
            Severity = severity;
            Message = message ?? string.Empty;
            Code = string.Empty;
        }

        public ValidationIssue(IssueSeverity severity, string code, string message)
        {
            Severity = severity;
            Code = code ?? string.Empty;
            Message = message ?? string.Empty;
        }

        public IssueSeverity Severity { get; set; }

        /// <summary>
        /// Machine-readable issue code (e.g. <c>authority/mismatch</c>,
        /// <c>cache/corrupt</c>, <c>launch/rejected/&lt;reason&gt;</c>). Optional;
        /// empty for legacy issues that only carry a human-readable message.
        /// </summary>
        public string Code { get; set; }

        public string Message { get; set; }
    }
}
