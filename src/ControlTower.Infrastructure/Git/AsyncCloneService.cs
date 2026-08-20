#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Infrastructure.Git
{
    /// <summary>
    /// Async git-clone primitive built on
    /// <see cref="IGitProcessAdapter"/>. Replaces the bespoke
    /// <c>Process.Start</c> path that lived in
    /// <c>RepoBootstrapService</c>.
    /// </summary>
    /// <remarks>
    /// <para>The service refuses to start a clone when the remote URL
    /// embeds credentials. Authentication must come from Git
    /// Credential Manager or SSH-agent so secrets do not leak through
    /// process arguments / log files.</para>
    /// <para>On cancellation the partially downloaded destination is
    /// intentionally left in place. Auto-cleanup would hide bugs; the
    /// caller decides whether to delete the half-written directory.
    /// </para>
    /// </remarks>
    public sealed class AsyncCloneService : IAsyncCloneService
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);

        private readonly IGitProcessAdapter _adapter;
        private readonly TimeSpan _timeout;

        public AsyncCloneService(IGitProcessAdapter adapter)
            : this(adapter, DefaultTimeout)
        {
        }

        public AsyncCloneService(IGitProcessAdapter adapter, TimeSpan timeout)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
            _timeout = timeout > TimeSpan.Zero ? timeout : DefaultTimeout;
        }

        public async Task<CloneResult> CloneAsync(
            CloneRequest request,
            IProgress<CloneProgress>? progress,
            CancellationToken ct)
        {
            if (request == null)
            {
                return CloneResult.Failure(CloneError.InvalidUrl, "No clone request supplied.");
            }

            if (string.IsNullOrWhiteSpace(request.RemoteUrl))
            {
                return CloneResult.Failure(CloneError.InvalidUrl, "Remote URL is required.");
            }

            if (string.IsNullOrWhiteSpace(request.DestinationPath))
            {
                return CloneResult.Failure(CloneError.InvalidUrl, "Destination path is required.");
            }

            // Credential-bearing URL? Refuse without starting the process and
            // without logging the URL.
            if (UrlCarriesCredentials(request.RemoteUrl, out var hostHint))
            {
                AppLogger.Warn(
                    "AsyncCloneService",
                    "Refusing clone: credential-in-URL detected (host=" + (hostHint ?? "?") + ").");
                return CloneResult.Failure(
                    CloneError.CredentialInUrl,
                    "Clone refused: remote URL contains embedded credentials. " +
                    "Use Git Credential Manager or SSH-agent instead.");
            }

            // Destination must be empty or non-existent.
            var dest = request.DestinationPath;
            try
            {
                if (Directory.Exists(dest))
                {
                    bool empty = !Directory.EnumerateFileSystemEntries(dest).Any();
                    if (!empty)
                    {
                        return CloneResult.Failure(
                            CloneError.DestinationNotEmpty,
                            "Clone refused: destination is not empty (" + dest + ").");
                    }
                }
                else
                {
                    var parent = Path.GetDirectoryName(dest);
                    if (!string.IsNullOrEmpty(parent) && !Directory.Exists(parent))
                    {
                        try { Directory.CreateDirectory(parent); }
                        catch (Exception ex)
                        {
                            return CloneResult.Failure(
                                CloneError.ParentNotCreateable,
                                "Could not create parent directory: " + ex.Message);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                return CloneResult.Failure(CloneError.ParentNotCreateable, ex.Message);
            }

            // We run git from the *parent* directory so the process has a
            // valid working directory regardless of whether the destination
            // exists yet.
            var workingDir = Path.GetDirectoryName(dest);
            if (string.IsNullOrEmpty(workingDir))
            {
                workingDir = Directory.GetCurrentDirectory();
            }
            if (!Directory.Exists(workingDir))
            {
                try { Directory.CreateDirectory(workingDir); }
                catch { /* validated above already */ }
            }

            // Build args: every value goes in via ArgumentList — no shell
            // concatenation. This is what defends scan roots / slug names /
            // branch names from injection.
            var args = new List<string> { "clone", "--progress" };
            if (request.SingleBranch)
            {
                args.Add("--single-branch");
            }
            if (!string.IsNullOrWhiteSpace(request.Branch))
            {
                args.Add("--branch");
                args.Add(request.Branch);
            }
            args.Add("--");
            args.Add(request.RemoteUrl);
            args.Add(dest);

            // Line-progress callback over the adapter's redacted stderr.
            var progressForwarder = progress != null
                ? new Progress<string>(line => ForwardProgress(progress, line))
                : null;

            GitRunResult run;
            try
            {
                run = await _adapter.RunAsync(
                    args, workingDir, _timeout, progressForwarder, ct).ConfigureAwait(false);
            }
            catch (GitNotFoundException ex)
            {
                return CloneResult.Failure(CloneError.GitNotFound, ex.Message);
            }
            catch (DirectoryNotFoundException ex)
            {
                return CloneResult.Failure(CloneError.ParentNotCreateable, ex.Message);
            }
            catch (OperationCanceledException)
            {
                return CloneResult.CancelledResult("Clone cancelled.");
            }

            if (run.Cancelled)
            {
                return CloneResult.CancelledResult(
                    "Clone cancelled. Partial content left at: " + dest);
            }

            if (run.TimedOut)
            {
                return CloneResult.Failure(CloneError.TimedOut,
                    "Clone timed out after " + _timeout.TotalSeconds.ToString("0", CultureInfo.InvariantCulture) + "s.");
            }

            if (run.ExitCode != 0)
            {
                var stderrSnippet = FirstNonEmptyLine(run.Stderr);
                return CloneResult.Failure(CloneError.CommandFailed,
                    string.IsNullOrWhiteSpace(stderrSnippet)
                        ? "git clone failed (exit " + run.ExitCode + ")."
                        : "git clone failed: " + stderrSnippet);
            }

            // Resolve branch + commit SHA from the freshly cloned repo
            // *directly off disk*. We can't trust the adapter's redacted
            // GitRunResult.Stdout for a SHA (40-char tokens get redacted
            // by the credential-redaction patterns).
            var (resolvedBranch, sha) = ResolveHead(dest);

            var ok = "Cloned into " + dest;
            return CloneResult.Ok(resolvedBranch, sha, ok);
        }

        private static void ForwardProgress(IProgress<CloneProgress> progress, string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            // git --progress emits lines like:
            //   "Receiving objects: 73% (8/11)"
            //   "remote: Counting objects: 100% (50/50), done."
            //   "Resolving deltas: 100% (3/3), done."
            //   "Cloning into '/some/path'..."
            string stage = "info";
            double? percent = null;

            var trimmed = line.Trim();

            if (trimmed.StartsWith("Cloning into", StringComparison.OrdinalIgnoreCase))
            {
                stage = "starting";
            }
            else if (Regex.IsMatch(trimmed, @"Counting objects", RegexOptions.IgnoreCase))
            {
                stage = "counting";
            }
            else if (Regex.IsMatch(trimmed, @"Compressing objects", RegexOptions.IgnoreCase))
            {
                stage = "compressing";
            }
            else if (Regex.IsMatch(trimmed, @"Receiving objects", RegexOptions.IgnoreCase))
            {
                stage = "receiving";
            }
            else if (Regex.IsMatch(trimmed, @"Resolving deltas", RegexOptions.IgnoreCase))
            {
                stage = "resolving";
            }
            else if (Regex.IsMatch(trimmed, @"Updating files", RegexOptions.IgnoreCase))
            {
                stage = "checkout";
            }

            var pctMatch = Regex.Match(trimmed, @"(?<pct>\d{1,3})%");
            if (pctMatch.Success && double.TryParse(
                    pctMatch.Groups["pct"].Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var pctValue))
            {
                if (pctValue >= 0 && pctValue <= 100)
                {
                    percent = pctValue;
                }
            }

            try { progress.Report(new CloneProgress(stage, percent, trimmed)); }
            catch { }
        }

        private static string FirstNonEmptyLine(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
            return string.Empty;
        }

        private static (string? branch, string? sha) ResolveHead(string repoPath)
        {
            try
            {
                var gitDir = Path.Combine(repoPath, ".git");
                if (!Directory.Exists(gitDir))
                {
                    // .git could be a file (worktree) — caller can re-check
                    return (null, null);
                }

                var headPath = Path.Combine(gitDir, "HEAD");
                if (!File.Exists(headPath))
                {
                    return (null, null);
                }

                var head = File.ReadAllText(headPath).Trim();
                if (head.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
                {
                    var refName = head.Substring(4).Trim();
                    var shortName = refName.StartsWith("refs/heads/", StringComparison.Ordinal)
                        ? refName.Substring("refs/heads/".Length)
                        : refName;

                    var refFile = Path.Combine(gitDir, refName.Replace('/', Path.DirectorySeparatorChar));
                    string? sha = null;
                    if (File.Exists(refFile))
                    {
                        var contents = File.ReadAllText(refFile).Trim();
                        if (Regex.IsMatch(contents, @"^[0-9a-fA-F]{4,64}$"))
                        {
                            sha = contents;
                        }
                    }
                    return (shortName, sha);
                }

                // Detached HEAD — the HEAD file is the SHA itself.
                if (Regex.IsMatch(head, @"^[0-9a-fA-F]{4,64}$"))
                {
                    return (null, head);
                }
            }
            catch
            {
                // best effort — never fail the clone result over a HEAD probe
            }
            return (null, null);
        }

        /// <summary>
        /// Returns <c>true</c> when the URL embeds credentials in its
        /// user-info component. Recognises both <c>HTTP URL containing user-info credentials</c>
        /// and the scp-like <c>user@host:org/repo</c> form is intentionally
        /// allowed (the bare username before <c>@</c> in an SSH URL is the
        /// identity, not a credential).
        /// </summary>
        internal static bool UrlCarriesCredentials(string url, out string? hostHint)
        {
            hostHint = null;
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                hostHint = uri.Host;
                if (string.IsNullOrEmpty(uri.UserInfo))
                {
                    return false;
                }

                // For ssh:// a bare username is the identity, not a secret;
                // a password in the user-info IS a credential.
                if (string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase))
                {
                    return uri.UserInfo.Contains(':');
                }

                return true;
            }

            // scp-like: "user@host:path" — never carries a password in the
            // user-info syntactically.
            var scpMatch = Regex.Match(url, @"^(?<user>[^@/:\s]+)@(?<host>[^:/\s]+):");
            if (scpMatch.Success)
            {
                hostHint = scpMatch.Groups["host"].Value;
                return false;
            }

            return false;
        }
    }
}
