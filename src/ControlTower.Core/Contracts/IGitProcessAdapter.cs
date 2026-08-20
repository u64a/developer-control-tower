#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ControlTower.Core.Contracts
{
    /// <summary>
    /// Result of running a single <c>git</c> invocation through
    /// <see cref="IGitProcessAdapter"/>. Stdout/stderr are pre-redacted of
    /// known credential token shapes before reaching the caller.
    /// </summary>
    public sealed record GitRunResult(
        int ExitCode,
        string Stdout,
        string Stderr,
        TimeSpan Duration,
        bool TimedOut,
        bool Cancelled);

    /// <summary>
    /// Async wrapper for invoking <c>git.exe</c>. The single allowed
    /// pathway for git inside <c>ControlTower.Infrastructure</c>; raw
    /// <see cref="System.Diagnostics.Process"/> use outside this adapter
    /// is forbidden.
    /// </summary>
    public interface IGitProcessAdapter
    {
        /// <summary>
        /// Runs <c>git</c> with the supplied arguments in the supplied
        /// working directory. Arguments are passed via
        /// <c>ArgumentList</c>, never string-concatenated, so values
        /// containing spaces or shell metacharacters are safe.
        ///
        /// <para>Cancellation: an honoured
        /// <see cref="CancellationToken"/> will kill the underlying
        /// process tree and surface <see cref="GitRunResult.Cancelled"/>.
        /// </para>
        /// <para>Timeout: the soft timeout is applied independently of
        /// the token; when it fires the process tree is killed and
        /// <see cref="GitRunResult.TimedOut"/> is set.</para>
        /// <para>Progress: each non-empty line of stdout/stderr is
        /// emitted to the optional <paramref name="progress"/>
        /// callback after redaction.</para>
        /// </summary>
        /// <exception cref="System.IO.DirectoryNotFoundException">
        /// Thrown when <paramref name="workingDirectory"/> does not
        /// exist. The process is not started.
        /// </exception>
        /// <exception cref="GitNotFoundException">Thrown when
        /// <c>git.exe</c> cannot be located.</exception>
        Task<GitRunResult> RunAsync(
            IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            IProgress<string>? progress,
            CancellationToken ct);
    }

    /// <summary>
    /// Surfaces a missing <c>git.exe</c> as a typed error so callers can
    /// produce an actionable message instead of a raw OS exception.
    /// </summary>
    public sealed class GitNotFoundException : Exception
    {
        public GitNotFoundException(string message) : base(message) { }

        public GitNotFoundException(string message, Exception inner)
            : base(message, inner) { }
    }
}
