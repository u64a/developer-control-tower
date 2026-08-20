using System;

namespace ControlTower.Infrastructure.Ssh
{
    /// <summary>
    /// Thrown when an SSH config value contains characters that could escape
    /// the directive line (newlines, control chars, unescaped quotes) and
    /// allow injection of unintended SSH config (H4).
    /// </summary>
    public sealed class SshConfigValueException : Exception
    {
        public SshConfigValueException(string directive, string value, string reason)
            : base($"Refused to write SSH config: directive '{directive}' has invalid value ({reason}).")
        {
            Directive = directive ?? string.Empty;
            Value = value ?? string.Empty;
            Reason = reason ?? string.Empty;
        }

        public string Directive { get; }
        public string Value { get; }
        public string Reason { get; }
    }
}
