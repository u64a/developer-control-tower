using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Ssh;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Infrastructure.Git
{
    public sealed class GitRepoScanner : IRepoScanner
    {
        private readonly ToolSettings _settings;
        private readonly ISshService _sshService;
        private readonly ICredentialStore _credentialStore;
        private readonly IStoreProvider _storeProvider;

        public GitRepoScanner()
            : this(new ToolSettings(), null, null, null)
        {
        }

        public GitRepoScanner(ToolSettings settings)
            : this(settings, null, null, null)
        {
        }

        public GitRepoScanner(
            ToolSettings settings,
            ISshService sshService,
            ICredentialStore credentialStore,
            IStoreProvider storeProvider)
        {
            _settings = settings ?? new ToolSettings();
            _sshService = sshService;
            _credentialStore = credentialStore;
            _storeProvider = storeProvider;
        }

        public RepoSnapshot Scan(string repoPath)
        {
            var snapshot = new RepoSnapshot();
            snapshot.RepoPath = repoPath;

            if (IsSshPath(repoPath))
            {
                return ScanSsh(repoPath, snapshot);
            }

            if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(repoPath))
            {
                snapshot.IsAvailable = false;
                snapshot.StatusMessage = "Local repo path is missing or unavailable";
                return snapshot;
            }

            snapshot.Branch = RunGit(repoPath, "rev-parse --abbrev-ref HEAD");
            if (string.IsNullOrWhiteSpace(snapshot.Branch) || snapshot.Branch.StartsWith("fatal", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.IsAvailable = false;
                snapshot.StatusMessage = snapshot.Branch != null &&
                    snapshot.Branch.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase)
                    ? "Empty repository (no commits yet)"
                    : "Git repo not available";
                return snapshot;
            }

            snapshot.IsAvailable = true;

            var porcelain = RunGit(repoPath, "status --porcelain");
            snapshot.IsDirty = !string.IsNullOrWhiteSpace(porcelain);

            var upstream = RunGit(repoPath, "rev-list --left-right --count HEAD...@{upstream}");
            if (!string.IsNullOrWhiteSpace(upstream) && !upstream.StartsWith("fatal", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.HasUpstream = true;
                var pieces = upstream.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length >= 2)
                {
                    int ahead;
                    int behind;
                    if (int.TryParse(pieces[0], out ahead))
                    {
                        snapshot.AheadBy = ahead;
                    }

                    if (int.TryParse(pieces[1], out behind))
                    {
                        snapshot.BehindBy = behind;
                    }
                }
            }

            var lastCommit = RunGit(repoPath, "log -1 --format=%ci");
            DateTime parsed;
            if (DateTime.TryParse(lastCommit, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                snapshot.LastCommitUtc = parsed.ToUniversalTime();
            }

            var originUrl = RunGit(repoPath, "remote get-url origin");
            if (!string.IsNullOrWhiteSpace(originUrl) &&
                !originUrl.StartsWith("fatal", StringComparison.OrdinalIgnoreCase) &&
                !originUrl.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.OriginUrl = originUrl.Trim();
            }

            snapshot.StatusMessage = "Repo scan complete";
            return snapshot;
        }

        private RepoSnapshot ScanSsh(string sshPath, RepoSnapshot snapshot)
        {
            string host;
            string remotePath;
            if (!TryParseSshPath(sshPath, out host, out remotePath))
            {
                snapshot.IsAvailable = false;
                snapshot.StatusMessage = "SSH target is not valid";
                return snapshot;
            }

            snapshot.RepoPath = host + ":" + remotePath;

            // Use SSH.NET when available (supports password auth)
            if (_sshService != null && _credentialStore != null && _storeProvider != null)
            {
                return ScanSshViaService(host, remotePath, snapshot);
            }

            // Fallback: shell out to ssh.exe (only works with key-based auth)
            return ScanSshViaProcess(host, remotePath, snapshot);
        }

        private RepoSnapshot ScanSshViaService(string hostPart, string remotePath, RepoSnapshot snapshot)
        {
            // Parse user@host
            string user = null;
            string hostname = hostPart;
            var atIndex = hostPart.IndexOf('@');
            if (atIndex > 0)
            {
                user = hostPart.Substring(0, atIndex);
                hostname = hostPart.Substring(atIndex + 1);
            }

            // Find matching store by host and user
            var store = FindMatchingStore(hostname, user);
            if (store == null)
            {
                snapshot.IsAvailable = false;
                snapshot.StatusMessage = $"No SSH store configured for host '{hostname}'";
                return snapshot;
            }

            var password = string.Empty;
            if (!string.IsNullOrWhiteSpace(store.CredentialTarget))
            {
                password = _credentialStore.GetPassword(store.CredentialTarget);
            }

            var sshUser = user ?? store.User;
            int port = store.Port > 0 ? store.Port : 22;

            // Detect the remote OS from the path shape so we can quote correctly.
            // POSIX absolute paths start with '/'; Windows absolute paths start with a
            // drive letter. The store Root is used as a fallback hint for relative paths.
            bool isWindowsRemote = RemotePathIsWindows(remotePath, store);

            string quotedPath;
            try
            {
                quotedPath = isWindowsRemote
                    ? SshCommandQuoter.QuoteWindows(remotePath)
                    : SshCommandQuoter.QuotePosix(remotePath);
            }
            catch (ArgumentException)
            {
                snapshot.IsAvailable = false;
                snapshot.StatusMessage = "SSH path contains unsafe characters";
                return snapshot;
            }

            // git -C <path> is cross-platform and does not require 'cd /d' (Windows-only).
            // Run probes separately so optional metadata failures cannot hide a valid repo.
            var gitC = "git -C " + quotedPath;
            var branchResult = _sshService.RunCommand(
                hostname, port, sshUser, password,
                gitC + " rev-parse --abbrev-ref HEAD");

            if (!branchResult.Success)
            {
                snapshot.IsAvailable = false;
                var errLine = string.IsNullOrWhiteSpace(branchResult.Error)
                    ? string.Empty : branchResult.Error.Split('\n')[0].Trim();

                if (errLine.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase) ||
                    errLine.Contains("unknown revision", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.StatusMessage = "Empty repository (no commits yet)";
                }
                else
                {
                    snapshot.StatusMessage = string.IsNullOrWhiteSpace(errLine)
                        ? "SSH repo unavailable"
                        : "SSH: " + errLine;
                }

                return snapshot;
            }

            var branch = branchResult.Output.Trim();
            if (string.IsNullOrWhiteSpace(branch) ||
                branch.StartsWith("fatal", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.IsAvailable = false;
                snapshot.StatusMessage = branch != null &&
                    branch.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase)
                    ? "Empty repository (no commits yet)"
                    : "SSH repo unavailable — not a git repository";
                return snapshot;
            }

            snapshot.Branch = branch;
            snapshot.IsAvailable = true;

            var statusResult = _sshService.RunCommand(
                hostname, port, sshUser, password,
                gitC + " status --porcelain");
            if (statusResult.Success)
            {
                snapshot.IsDirty = !string.IsNullOrWhiteSpace(statusResult.Output);
            }

            var upstreamResult = _sshService.RunCommand(
                hostname, port, sshUser, password,
                gitC + " rev-list --left-right --count HEAD...@{upstream}");
            if (upstreamResult.Success)
            {
                var pieces = upstreamResult.Output.Split(
                    new[] { ' ', '\t', '\r', '\n' },
                    StringSplitOptions.RemoveEmptyEntries);
                int ahead;
                int behind;
                if (pieces.Length >= 2 &&
                    int.TryParse(pieces[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out ahead) &&
                    int.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out behind))
                {
                    snapshot.HasUpstream = true;
                    snapshot.AheadBy = ahead;
                    snapshot.BehindBy = behind;
                }
            }

            var lastCommitResult = _sshService.RunCommand(
                hostname, port, sshUser, password,
                gitC + " log -1 --format=%ci");
            if (lastCommitResult.Success)
            {
                DateTime parsed;
                if (DateTime.TryParse(lastCommitResult.Output.Trim(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out parsed))
                {
                    snapshot.LastCommitUtc = parsed.ToUniversalTime();
                }
            }

            var originResult = _sshService.RunCommand(
                hostname, port, sshUser, password,
                gitC + " remote get-url origin");
            if (originResult.Success)
            {
                var origin = originResult.Output.Trim();
                if (!string.IsNullOrWhiteSpace(origin) &&
                    !origin.StartsWith("fatal", StringComparison.OrdinalIgnoreCase) &&
                    !origin.StartsWith("error", StringComparison.OrdinalIgnoreCase))
                {
                    snapshot.OriginUrl = origin;
                }
            }

            snapshot.StatusMessage = "Remote SSH repo scan complete";
            return snapshot;
        }

        private RepoStore FindMatchingStore(string hostname, string user)
        {
            var stores = _storeProvider.GetStores();
            if (stores == null || stores.Count == 0)
            {
                return null;
            }

            // Prefer exact match on host + user
            var match = stores.FirstOrDefault(s =>
                s.IsSsh &&
                string.Equals(s.Host, hostname, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(user) &&
                string.Equals(s.User, user, StringComparison.OrdinalIgnoreCase));

            if (match != null)
            {
                return match;
            }

            // Fall back to host-only match
            return stores.FirstOrDefault(s =>
                s.IsSsh &&
                string.Equals(s.Host, hostname, StringComparison.OrdinalIgnoreCase));
        }

        private RepoSnapshot ScanSshViaProcess(string host, string remotePath, RepoSnapshot snapshot)
        {
            snapshot.Branch = RunSshGit(host, remotePath, "rev-parse --abbrev-ref HEAD");
            if (string.IsNullOrWhiteSpace(snapshot.Branch) ||
                snapshot.Branch.StartsWith("fatal", StringComparison.OrdinalIgnoreCase) ||
                snapshot.Branch.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.IsAvailable = false;
                snapshot.StatusMessage = snapshot.Branch != null &&
                    snapshot.Branch.Contains("ambiguous argument", StringComparison.OrdinalIgnoreCase)
                    ? "Empty repository (no commits yet)"
                    : "SSH repo unavailable";
                return snapshot;
            }

            snapshot.IsAvailable = true;

            var porcelain = RunSshGit(host, remotePath, "status --porcelain");
            snapshot.IsDirty = !string.IsNullOrWhiteSpace(porcelain);

            var upstream = RunSshGit(host, remotePath, "rev-list --left-right --count HEAD...@{upstream}");
            if (!string.IsNullOrWhiteSpace(upstream) && !upstream.StartsWith("fatal", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.HasUpstream = true;
                var pieces = upstream.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (pieces.Length >= 2)
                {
                    int ahead;
                    int behind;
                    if (int.TryParse(pieces[0], out ahead))
                    {
                        snapshot.AheadBy = ahead;
                    }

                    if (int.TryParse(pieces[1], out behind))
                    {
                        snapshot.BehindBy = behind;
                    }
                }
            }

            var lastCommit = RunSshGit(host, remotePath, "log -1 --format=%ci");
            DateTime parsed;
            if (DateTime.TryParse(lastCommit, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out parsed))
            {
                snapshot.LastCommitUtc = parsed.ToUniversalTime();
            }

            var originUrl = RunSshGit(host, remotePath, "remote get-url origin");
            if (!string.IsNullOrWhiteSpace(originUrl) &&
                !originUrl.StartsWith("fatal", StringComparison.OrdinalIgnoreCase) &&
                !originUrl.StartsWith("error", StringComparison.OrdinalIgnoreCase))
            {
                snapshot.OriginUrl = originUrl.Trim();
            }

            snapshot.StatusMessage = "Remote SSH repo scan complete";
            return snapshot;
        }

        private static bool IsSshPath(string repoPath)
        {
            if (string.IsNullOrWhiteSpace(repoPath))
            {
                return false;
            }

            // Detect scp-style SSH paths (host:path) but not Windows drive letters (C:\)
            var colonIndex = repoPath.IndexOf(':');
            return colonIndex > 1 && colonIndex < repoPath.Length - 1;
        }

        private static bool TryParseSshPath(string sshPath, out string host, out string remotePath)
        {
            host = string.Empty;
            remotePath = string.Empty;

            if (string.IsNullOrWhiteSpace(sshPath))
            {
                return false;
            }

            var separator = sshPath.IndexOf(':');
            if (separator <= 1 || separator >= sshPath.Length - 1)
            {
                return false;
            }

            host = sshPath.Substring(0, separator).Trim();
            remotePath = sshPath.Substring(separator + 1).Trim();
            return IsSafeHost(host) && IsSafeRemotePath(remotePath);
        }

        /// <summary>
        /// Infers whether the remote path resides on a Windows host.
        /// POSIX absolute paths start with '/'; Windows absolute paths start with
        /// a drive letter followed by ':'. If the remotePath is relative or ambiguous,
        /// the store Root is used as a secondary hint. Defaults to POSIX (false).
        /// </summary>
        private static bool RemotePathIsWindows(string remotePath, RepoStore store)
        {
            if (!string.IsNullOrEmpty(remotePath))
            {
                if (remotePath[0] == '/') return false;
                if (remotePath.Length >= 2 && char.IsLetter(remotePath[0]) && remotePath[1] == ':') return true;
            }
            var root = store?.Root;
            if (!string.IsNullOrEmpty(root))
            {
                if (root[0] == '/') return false;
                if (root.Length >= 2 && char.IsLetter(root[0]) && root[1] == ':') return true;
            }
            return false;
        }

        private string RunGit(string repoPath, string arguments)
        {
            return RunProcess(_settings.GitCommand, "-C \"" + EscapeQuotes(repoPath) + "\" " + arguments);
        }

        private string RunSshGit(string host, string remotePath, string gitArguments)
        {
            if (!IsSafeHost(host) || !IsSafeRemotePath(remotePath))
            {
                return "SSH target is not valid";
            }

            var escapedPath = remotePath.Replace("'", "'\\''");
            var arguments = "-o BatchMode=yes -o ConnectTimeout=5 " + host + " \"git -C '" + escapedPath + "' " + gitArguments + "\"";
            return RunProcess(_settings.SshCommand, arguments);
        }

        private static bool IsSafeHost(string host)
        {
            return !string.IsNullOrWhiteSpace(host) &&
                   Regex.IsMatch(host, @"^[A-Za-z0-9._@-]+$");
        }

        private static bool IsSafeRemotePath(string remotePath)
        {
            return !string.IsNullOrWhiteSpace(remotePath) &&
                   remotePath.IndexOfAny(new[] { '\r', '\n', '"' }) < 0;
        }

        private static string EscapeQuotes(string value)
        {
            return (value ?? string.Empty).Replace("\"", "\\\"");
        }

        private static string RunProcess(string fileName, string arguments)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return string.Empty;
            }

            var psi = new ProcessStartInfo();
            psi.FileName = fileName;
            psi.Arguments = arguments;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;

            const int timeoutMs = 15000;

            try
            {
                using (var process = Process.Start(psi))
                {
                    if (process == null)
                    {
                        return string.Empty;
                    }

                    var output = process.StandardOutput.ReadToEnd().Trim();
                    var error = process.StandardError.ReadToEnd().Trim();

                    if (!process.WaitForExit(timeoutMs))
                    {
                        try { process.Kill(); } catch { }
                        return "Command timed out";
                    }

                    if (!string.IsNullOrWhiteSpace(output))
                    {
                        return output;
                    }

                    return error;
                }
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }
    }
}
