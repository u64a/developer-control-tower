#nullable enable
using System;

namespace ControlTower.Core.Ssh
{
    /// <summary>
    /// Quotes paths and arguments for use in remote SSH command lines.
    /// The remote OS dictates the rules:
    /// <list type="bullet">
    /// <item><description>Windows: <c>cmd.exe</c> processes the line.
    /// Wrap in double quotes and reject characters that remain active inside
    /// quotes or could terminate them.</description></item>
    /// <item><description>POSIX: a shell like sh/bash processes the line.
    /// Wrap in single quotes; escape an embedded single quote with the
    /// usual <c>'\''</c> dance.</description></item>
    /// </list>
    /// Newlines, carriage returns, and NUL bytes are rejected outright —
    /// no valid filesystem path contains them, and accepting them would
    /// give an attacker an injection vector.
    /// </summary>
    public static class SshCommandQuoter
    {
        public static string QuoteWindows(string value)
        {
            Validate(value);

            // cmd.exe has no reliable backslash escape for an embedded quote,
            // and percent/delayed expansion still occurs inside double quotes.
            // These characters are not needed in supported Windows paths,
            // branch names, or repository URLs, so fail closed rather than
            // emitting a command whose meaning can change.
            if (value.IndexOfAny(new[] { '"', '%', '!' }) >= 0)
            {
                throw new ArgumentException(
                    "Value contains a character that cannot be represented safely in a cmd.exe argument.",
                    nameof(value));
            }

            return "\"" + value + "\"";
        }

        public static string QuotePosix(string value)
        {
            Validate(value);

            // Single quotes preserve every byte except another single
            // quote; replace each embedded ' with '\'' to close, escape,
            // and reopen the quoting.
            return "'" + value.Replace("'", "'\\''") + "'";
        }

        private static void Validate(string value)
        {
            if (value == null)
            {
                throw new ArgumentNullException(nameof(value));
            }

            for (int i = 0; i < value.Length; i++)
            {
                var ch = value[i];
                if (ch == '\r' || ch == '\n' || ch == '\0')
                {
                    throw new ArgumentException(
                        "Value contains an unsupported control character (CR/LF/NUL). " +
                        "Refusing to construct a remote command.",
                        nameof(value));
                }
            }
        }
    }
}
