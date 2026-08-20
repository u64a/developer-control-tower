using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace ControlTower.Infrastructure.Diagnostics
{
    public static class AppLogger
    {
        private static readonly object _lock = new();
        private static string _logFolder;

        // Token-shape patterns we redact before writing to the log (L2). The
        // patterns intentionally catch GitHub PATs, JWT-like blobs, and
        // anything trailing common credential prefixes.
        private static readonly (Regex Pattern, string Replacement)[] RedactionPatterns = new[]
        {
            // GitHub-style tokens: ghp_, gho_, ghu_, ghs_, ghr_, github_pat_
            (new Regex(@"gh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.Compiled), "[redacted-gh-token]"),
            (new Regex(@"github_pat_[A-Za-z0-9_]{20,}", RegexOptions.Compiled), "[redacted-gh-pat]"),
            // JWT-like (three base64url segments separated by '.').
            (new Regex(@"eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}", RegexOptions.Compiled), "[redacted-jwt]"),
            // Authorization: <anything> header values.
            (new Regex(@"(?i)Authorization\s*:\s*[^\r\n]+", RegexOptions.Compiled), "Authorization: [redacted]"),
            // Bearer <token>.
            (new Regex(@"(?i)Bearer\s+[A-Za-z0-9._\-]+", RegexOptions.Compiled), "Bearer [redacted]"),
            // password=value or password: value (form/query/yaml).
            (new Regex(@"(?i)password\s*[:=]\s*\S+", RegexOptions.Compiled), "password=[redacted]")
        };

        // Generic high-entropy backstop: 40+ char base64/hex blobs that
        // none of the specific patterns above caught. Applied via a match
        // evaluator so pure-hex strings of git-SHA length (40 = SHA-1,
        // 64 = SHA-256) are preserved - the tool runs git constantly and
        // nuking every SHA in the log makes it unreadable.
        private static readonly Regex HighEntropyPattern =
            new(@"\b[A-Za-z0-9_\-]{40,}\b", RegexOptions.Compiled);

        // Expected/benign exception types whose stack trace adds noise
        // without value. For these we log only the message.
        private static readonly HashSet<string> ExpectedExceptionTypeNames = new(StringComparer.Ordinal)
        {
            nameof(FileNotFoundException),
            nameof(DirectoryNotFoundException),
            nameof(UnauthorizedAccessException),
            nameof(OperationCanceledException),
            "TaskCanceledException",
            nameof(IOException),
            "Win32Exception"
        };

        public static string LogFolder
        {
            get
            {
                if (_logFolder == null)
                {
                    _logFolder = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "DeveloperControlTower",
                        "logs");
                }
                return _logFolder;
            }
        }
        public static string CurrentLogFile
            => Path.Combine(LogFolder, "app-" + DateTime.UtcNow.ToString("yyyyMMdd") + ".log");

        public static void Info(string scope, string message)
            => Write("INFO", scope, message, null);

        public static void Warn(string scope, string message)
            => Write("WARN", scope, message, null);

        public static void Debug(string scope, string message)
            => Write("DEBUG", scope, message, null);

        public static void Error(string scope, string message, Exception ex = null)
            => Write("ERROR", scope, message, ex);

        /// <summary>
        /// Applies the redaction rules to an arbitrary string. Exposed so
        /// callers can sanitise text they intend to surface in the UI as
        /// well as the log.
        /// </summary>
        public static string Redact(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return text ?? string.Empty;
            }

            var result = text;
            foreach (var (pattern, replacement) in RedactionPatterns)
            {
                result = pattern.Replace(result, replacement);
            }
            // Apply the high-entropy backstop last so it only fires on blobs
            // none of the specific patterns above caught. Preserve pure-hex
            // strings of git-SHA length so commit/blob SHAs survive.
            result = HighEntropyPattern.Replace(result, m =>
                IsGitShaShape(m.Value) ? m.Value : "[redacted-token]");
            return result;
        }

        private static bool IsGitShaShape(string value)
        {
            // SHA-1 = 40 hex chars; SHA-256 = 64 hex chars. Anything outside
            // those exact lengths is treated as an unknown high-entropy blob.
            if (value.Length != 40 && value.Length != 64)
            {
                return false;
            }
            foreach (var c in value)
            {
                var isHex = (c >= '0' && c <= '9')
                    || (c >= 'a' && c <= 'f')
                    || (c >= 'A' && c <= 'F');
                if (!isHex)
                {
                    return false;
                }
            }
            return true;
        }

        private static void Write(string level, string scope, string message, Exception ex)
        {
            try
            {
                Directory.CreateDirectory(LogFolder);
                var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("o")).Append("  ");
                sb.Append(level.PadRight(5)).Append("  ");
                sb.Append('[').Append(scope ?? "-").Append("]  ");
                sb.AppendLine(Redact(message ?? string.Empty));
                if (ex != null)
                {
                    if (IsExpectedException(ex))
                    {
                        sb.Append(ex.GetType().Name).Append(": ").AppendLine(Redact(ex.Message ?? string.Empty));
                    }
                    else
                    {
                        sb.AppendLine(Redact(ex.ToString()));
                    }
                }

                lock (_lock)
                {
                    File.AppendAllText(CurrentLogFile, sb.ToString(), new UTF8Encoding(false));
                }
            }
            catch
            {
                // Never let logging break anything.
            }
        }

        private static bool IsExpectedException(Exception ex)
        {
            return ex != null && ExpectedExceptionTypeNames.Contains(ex.GetType().Name);
        }
    }
}
