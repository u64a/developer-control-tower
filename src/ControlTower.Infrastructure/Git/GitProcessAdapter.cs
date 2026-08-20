#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Infrastructure.Git
{
    /// <summary>
    /// Real git-CLI implementation of <see cref="IGitProcessAdapter"/>.
    /// Mirrors the test-seam pattern used by
    /// <c>WindowsLaunchService</c>: an optional factory parameter
    /// lets tests substitute a fake handle without ever touching the
    /// real <see cref="Process"/> API. All stdout/stderr that leaves
    /// this class is redacted through
    /// <see cref="AppLogger.Redact(string)"/>.
    /// </summary>
    public sealed class GitProcessAdapter : IGitProcessAdapter
    {
        private readonly string _gitExecutable;
        private readonly Func<ProcessStartInfo, IGitProcessHandle> _handleFactory;

        public GitProcessAdapter()
            : this(new ToolSettings(), null)
        {
        }

        public GitProcessAdapter(ToolSettings settings)
            : this(settings, null)
        {
        }

        // Test seam: when handleFactory is non-null tests can inject a
        // fake handle instead of starting a real process.
        public GitProcessAdapter(
            ToolSettings settings,
            Func<ProcessStartInfo, IGitProcessHandle>? handleFactory)
        {
            var resolved = settings?.GitCommand;
            _gitExecutable = string.IsNullOrWhiteSpace(resolved) ? "git" : resolved;
            _handleFactory = handleFactory ?? DefaultHandleFactory;
        }

        public async Task<GitRunResult> RunAsync(
            IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            if (arguments == null)
            {
                throw new ArgumentNullException(nameof(arguments));
            }

            if (string.IsNullOrWhiteSpace(workingDirectory))
            {
                throw new ArgumentException("Working directory must be supplied.", nameof(workingDirectory));
            }

            if (!Directory.Exists(workingDirectory))
            {
                throw new DirectoryNotFoundException(
                    "Working directory does not exist: " + workingDirectory);
            }

            var psi = new ProcessStartInfo
            {
                FileName = _gitExecutable,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in arguments)
            {
                if (arg == null)
                {
                    continue;
                }
                psi.ArgumentList.Add(arg);
            }

            IGitProcessHandle handle;
            try
            {
                handle = _handleFactory(psi);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 2 /* ERROR_FILE_NOT_FOUND */)
            {
                throw new GitNotFoundException(
                    "git.exe was not found on PATH. Install Git or set tooling.git_command in settings.", ex);
            }
            catch (FileNotFoundException ex)
            {
                throw new GitNotFoundException("git.exe was not found on PATH.", ex);
            }

            using (handle)
            {
                var stopwatch = Stopwatch.StartNew();

                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                if (timeout > TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                {
                    linkedCts.CancelAfter(timeout);
                }

                var stdoutTask = ReadStreamAsync(handle.StandardOutput, progress, linkedCts.Token);
                var stderrTask = ReadStreamAsync(handle.StandardError, progress, linkedCts.Token);

                bool timedOut = false;
                bool cancelled = false;

                try
                {
                    await handle.WaitForExitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { handle.Kill(entireProcessTree: true); } catch { }
                    cancelled = ct.IsCancellationRequested;
                    timedOut = !cancelled && linkedCts.Token.IsCancellationRequested;
                }

                // Best-effort drain of whatever the streams produced.
                string stdout;
                string stderr;
                try { stdout = await stdoutTask.ConfigureAwait(false); }
                catch (Exception) { stdout = string.Empty; }
                try { stderr = await stderrTask.ConfigureAwait(false); }
                catch (Exception) { stderr = string.Empty; }

                stopwatch.Stop();

                if (timedOut || cancelled)
                {
                    return new GitRunResult(
                        ExitCode: -1,
                        Stdout: AppLogger.Redact(stdout),
                        Stderr: AppLogger.Redact(stderr),
                        Duration: stopwatch.Elapsed,
                        TimedOut: timedOut,
                        Cancelled: cancelled);
                }

                int exitCode;
                try { exitCode = handle.ExitCode; }
                catch (Exception) { exitCode = -1; }

                return new GitRunResult(
                    ExitCode: exitCode,
                    Stdout: AppLogger.Redact(stdout),
                    Stderr: AppLogger.Redact(stderr),
                    Duration: stopwatch.Elapsed,
                    TimedOut: false,
                    Cancelled: false);
            }
        }

        private static async Task<string> ReadStreamAsync(
            TextReader reader,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            if (reader == null)
            {
                return string.Empty;
            }

            var sb = new System.Text.StringBuilder();
            while (true)
            {
                string? line;
                try
                {
                    line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception)
                {
                    break;
                }

                if (line == null)
                {
                    break;
                }

                sb.AppendLine(line);

                if (progress != null && line.Length > 0)
                {
                    try { progress.Report(AppLogger.Redact(line)); }
                    catch { /* progress consumers must not break the read loop */ }
                }
            }

            return sb.ToString();
        }

        private static IGitProcessHandle DefaultHandleFactory(ProcessStartInfo psi)
        {
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null for git.");
            return new RealGitProcessHandle(process);
        }
    }

    /// <summary>
    /// Abstraction over a running <c>git</c> process so the adapter can
    /// be unit-tested without spawning real processes.
    /// </summary>
    public interface IGitProcessHandle : IDisposable
    {
        TextReader StandardOutput { get; }
        TextReader StandardError { get; }
        bool HasExited { get; }
        int ExitCode { get; }
        Task WaitForExitAsync(CancellationToken ct);
        void Kill(bool entireProcessTree);
    }

    internal sealed class RealGitProcessHandle : IGitProcessHandle
    {
        private readonly Process _process;

        public RealGitProcessHandle(Process process)
        {
            _process = process;
        }

        public TextReader StandardOutput => _process.StandardOutput;
        public TextReader StandardError => _process.StandardError;
        public bool HasExited => SafeBool(() => _process.HasExited);
        public int ExitCode => _process.ExitCode;

        public Task WaitForExitAsync(CancellationToken ct) => _process.WaitForExitAsync(ct);

        public void Kill(bool entireProcessTree)
        {
            try { _process.Kill(entireProcessTree); }
            catch { }
        }

        public void Dispose()
        {
            try { _process.Dispose(); } catch { }
        }

        private static bool SafeBool(Func<bool> f)
        {
            try { return f(); } catch { return false; }
        }
    }
}
