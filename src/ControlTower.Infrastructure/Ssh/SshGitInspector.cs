#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Ssh;

namespace ControlTower.Infrastructure.Ssh
{
    /// <summary>
    /// SSH-side git plumbing inspector. Issues read-only plumbing commands
    /// through <see cref="ISshService"/> and parses the output into the
    /// same <see cref="GitWorkspaceClassification"/> /
    /// <see cref="GitStatusBuckets"/> shapes the local inspector produces.
    /// </summary>
    /// <remarks>
    /// All command construction goes through <see cref="SshCommandQuoter"/>
    /// so paths containing spaces, quotes, dollars, etc. are safe.
    /// </remarks>
    public sealed class SshGitInspector : ISshGitInspector
    {
        private const int IgnoredFilesCap = 1000;

        private static readonly (string RelativePath, string Operation)[] ActiveOperationMarkers =
        {
            ("MERGE_HEAD", "merge"),
            ("rebase-merge", "rebase"),
            ("rebase-apply", "rebase"),
            ("CHERRY_PICK_HEAD", "cherry-pick"),
            ("REVERT_HEAD", "revert"),
            ("sequencer", "cherry-pick/revert"),
            ("BISECT_START", "bisect"),
            ("BISECT_LOG", "bisect")
        };

        private readonly ISshService _ssh;

        public SshGitInspector(ISshService ssh)
        {
            _ssh = ssh ?? throw new ArgumentNullException(nameof(ssh));
        }

        public Task<GitWorkspaceClassification> ClassifyAsync(
            string host, int port, string user, string password, string remotePath, CancellationToken ct)
        {
            // ISshService is sync; wrap so callers can await uniformly.
            return Task.Run(() => ClassifyCore(host, port, user, password, remotePath, ct), ct);
        }

        public Task<GitStatusBuckets> ReadStatusAsync(
            string host, int port, string user, string password, string remotePath, CancellationToken ct)
        {
            return Task.Run(() => ReadStatusCore(host, port, user, password, remotePath, ct), ct);
        }

        public Task<RelocationGitState> ReadRelocationStateAsync(
            string host, int port, string user, string password, string remotePath, CancellationToken ct)
        {
            return Task.Run(
                () => ReadRelocationStateCore(host, port, user, password, remotePath, ct),
                ct);
        }

        private GitWorkspaceClassification ClassifyCore(
            string host, int port, string user, string password, string remotePath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return new NotARepo(remotePath ?? string.Empty);
            }

            if (!TryDetectRemoteIsWindows(
                host, port, user, password, out var remoteIsWindows, out _))
            {
                return new NotARepo(remotePath);
            }
            string quotedPath = remoteIsWindows
                ? SshCommandQuoter.QuoteWindows(remotePath)
                : SshCommandQuoter.QuotePosix(remotePath);

            // rev-parse --git-dir tells us whether the folder is a git
            // repo (and where its .git is). If it returns ".", the folder
            // is itself a bare repo.
            var gitDirRun = _ssh.RunCommand(host, port, user, password,
                $"git -c safe.bareRepository=all -C {quotedPath} rev-parse --git-dir");
            if (!gitDirRun.Success || string.IsNullOrWhiteSpace(gitDirRun.Output))
            {
                return new NotARepo(remotePath);
            }

            string gitDir = gitDirRun.Output.Trim().Split('\n', '\r').FirstOrDefault() ?? string.Empty;

            var isInsideRun = _ssh.RunCommand(host, port, user, password,
                $"git -c safe.bareRepository=all -C {quotedPath} rev-parse --is-inside-work-tree");
            bool isWorkingTree = isInsideRun.Success
                && string.Equals(isInsideRun.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase);

            var remotes = ReadRemotes(host, port, user, password, quotedPath);

            if (!isWorkingTree)
            {
                return new BareRepo(remotePath, gitDir, remotes);
            }

            var branchRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} symbolic-ref --short -q HEAD");
            string branch = branchRun.Success ? branchRun.Output.Trim() : string.Empty;
            bool detached = false;
            if (string.IsNullOrEmpty(branch))
            {
                var headRun = _ssh.RunCommand(host, port, user, password,
                    $"git -C {quotedPath} rev-parse --verify --quiet HEAD");
                if (headRun.Success && !string.IsNullOrWhiteSpace(headRun.Output))
                {
                    detached = true;
                    branch = headRun.Output.Trim();
                }
            }

            var shallowRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} rev-parse --is-shallow-repository");
            bool isShallow = shallowRun.Success
                && string.Equals(shallowRun.Output.Trim(), "true", StringComparison.OrdinalIgnoreCase);

            // info/sparse-checkout marks a sparse checkout.
            string sparsePath = JoinRemotePath(gitDir, "info/sparse-checkout", remoteIsWindows);
            string sparseQuoted = remoteIsWindows
                ? SshCommandQuoter.QuoteWindows(sparsePath)
                : SshCommandQuoter.QuotePosix(sparsePath);
            var sparseRun = remoteIsWindows
                ? _ssh.RunCommand(host, port, user, password,
                    $"if exist {sparseQuoted} (echo Y) else (echo N)")
                : _ssh.RunCommand(host, port, user, password,
                    $"test -f {sparseQuoted} && echo Y || echo N");
            bool isSparse = sparseRun.Success && sparseRun.Output.Trim().StartsWith("Y", StringComparison.Ordinal);

            var partialRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} config --get-regexp ^remote\\..*\\.partialclonefilter$");
            bool isPartial = partialRun.Success && !string.IsNullOrWhiteSpace(partialRun.Output);

            var worktreesRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} worktree list --porcelain");
            int worktreeCount = 0;
            if (worktreesRun.Success)
            {
                foreach (var line in SplitLines(worktreesRun.Output))
                {
                    if (line.StartsWith("worktree ", StringComparison.Ordinal))
                    {
                        worktreeCount++;
                    }
                }
            }
            bool hasWorktrees = worktreeCount > 1;

            string modulesPath = JoinRemotePath(remotePath, ".gitmodules", remoteIsWindows);
            string modulesQuoted = remoteIsWindows
                ? SshCommandQuoter.QuoteWindows(modulesPath)
                : SshCommandQuoter.QuotePosix(modulesPath);
            var modulesRun = remoteIsWindows
                ? _ssh.RunCommand(host, port, user, password,
                    $"if exist {modulesQuoted} (echo Y) else (echo N)")
                : _ssh.RunCommand(host, port, user, password,
                    $"test -f {modulesQuoted} && echo Y || echo N");
            bool hasSubmodules = modulesRun.Success && modulesRun.Output.Trim().StartsWith("Y", StringComparison.Ordinal);

            string? originUrl = remotes
                .FirstOrDefault(r => string.Equals(r.Name, "origin", StringComparison.OrdinalIgnoreCase))
                ?.FetchUrl;

            return new WorkingTreeRepo(
                Path: remotePath,
                GitDir: gitDir,
                Branch: branch,
                IsDetached: detached,
                IsShallow: isShallow,
                IsSparse: isSparse,
                IsPartialClone: isPartial,
                HasWorktrees: hasWorktrees,
                HasSubmodules: hasSubmodules,
                OriginUrl: originUrl,
                Remotes: remotes);
        }

        private GitStatusBuckets ReadStatusCore(
            string host, int port, string user, string password, string remotePath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            var modified = new List<string>();
            var staged = new List<string>();
            var untracked = new List<string>();
            var ignored = new List<string>();
            int? ahead = null;
            int? behind = null;

            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return new GitStatusBuckets(modified, staged, untracked, ignored, ahead, behind);
            }

            if (!TryDetectRemoteIsWindows(
                host, port, user, password, out var remoteIsWindows, out _))
            {
                return new GitStatusBuckets(modified, staged, untracked, ignored, ahead, behind);
            }
            string quotedPath = remoteIsWindows
                ? SshCommandQuoter.QuoteWindows(remotePath)
                : SshCommandQuoter.QuotePosix(remotePath);

            var statusRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} status --porcelain=v2 --branch --untracked-files=normal");

            if (statusRun.Success)
            {
                ParsePorcelainV2(
                    statusRun.Output,
                    modified,
                    staged,
                    untracked,
                    ref ahead,
                    ref behind,
                    out _);
            }

            var ignoredRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} ls-files --others --ignored --exclude-standard");
            if (ignoredRun.Success)
            {
                foreach (var line in SplitLines(ignoredRun.Output))
                {
                    if (ignored.Count >= IgnoredFilesCap) break;
                    if (!string.IsNullOrWhiteSpace(line))
                    {
                        ignored.Add(line.Trim());
                    }
                }
            }

            return new GitStatusBuckets(modified, staged, untracked, ignored, ahead, behind);
        }

        private RelocationGitState ReadRelocationStateCore(
            string host, int port, string user, string password, string remotePath, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(remotePath))
            {
                return RelocationGitState.Failure("Source working tree is unavailable.");
            }

            if (!TryDetectRemoteIsWindows(
                host, port, user, password, out var remoteIsWindows, out var osError))
            {
                return RelocationGitState.Failure(osError);
            }

            string quotedPath = Quote(remotePath, remoteIsWindows);
            var branchRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} symbolic-ref --quiet --short HEAD");
            var branch = branchRun.Output.Trim();
            if (!branchRun.Success || string.IsNullOrWhiteSpace(branch))
            {
                return RelocationGitState.Failure(
                    "Source HEAD is detached or its named branch could not be read.",
                    remoteIsWindows);
            }

            var headRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} rev-parse --verify HEAD^{{commit}}");
            var headSha = headRun.Output.Trim();
            if (!headRun.Success || !IsFullObjectId(headSha))
            {
                return RelocationGitState.Failure(
                    "Source HEAD SHA could not be read as a full Git object ID.",
                    remoteIsWindows);
            }

            if (!TryReadActiveGitOperation(
                host,
                port,
                user,
                password,
                quotedPath,
                remoteIsWindows,
                out var activeOperation,
                out var operationError))
            {
                return RelocationGitState.Failure(operationError, remoteIsWindows);
            }
            if (!string.IsNullOrEmpty(activeOperation))
            {
                return RelocationGitState.Failure(
                    $"Source has an active Git {activeOperation} operation.",
                    remoteIsWindows);
            }

            var statusRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} status --porcelain=v2 --branch --untracked-files=normal");
            if (!statusRun.Success)
            {
                return RelocationGitState.Failure(
                    "Source status command failed: " + DescribeFailure(statusRun),
                    remoteIsWindows);
            }

            var modified = new List<string>();
            var staged = new List<string>();
            var untracked = new List<string>();
            var ignored = new List<string>();
            int? ahead = null;
            int? behind = null;
            if (!ParsePorcelainV2(
                statusRun.Output,
                modified,
                staged,
                untracked,
                ref ahead,
                ref behind,
                out var statusParseError))
            {
                return RelocationGitState.Failure(statusParseError, remoteIsWindows);
            }
            ParseBranchHeaders(statusRun.Output, out var statusBranch, out var upstream);

            if (!string.Equals(statusBranch, branch, StringComparison.Ordinal))
            {
                return RelocationGitState.Failure(
                    "Source status did not confirm the checked-out branch.",
                    remoteIsWindows);
            }

            var expectedUpstream = "origin/" + branch;
            if (!string.Equals(upstream, expectedUpstream, StringComparison.Ordinal))
            {
                return RelocationGitState.Failure(
                    $"Source branch '{branch}' must track '{expectedUpstream}'.",
                    remoteIsWindows);
            }

            if (!ahead.HasValue || !behind.HasValue)
            {
                return RelocationGitState.Failure(
                    "Source ahead/behind counts relative to origin could not be established.",
                    remoteIsWindows);
            }

            var ignoredRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} ls-files --others --ignored --exclude-standard");
            if (!ignoredRun.Success)
            {
                return RelocationGitState.Failure(
                    "Source ignored-file inventory failed: " + DescribeFailure(ignoredRun),
                    remoteIsWindows);
            }
            bool ignoredInventoryComplete = true;
            foreach (var line in SplitLines(ignoredRun.Output))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (ignored.Count >= IgnoredFilesCap)
                {
                    ignoredInventoryComplete = false;
                    break;
                }
                ignored.Add(line.Trim());
            }

            var branchRef = "refs/heads/" + branch;
            var branchRefQuoted = Quote(branchRef, remoteIsWindows);
            var remoteHeadRun = _ssh.RunCommand(host, port, user, password,
                $"git -C {quotedPath} ls-remote --heads -- origin {branchRefQuoted}");
            if (!remoteHeadRun.Success
                || !TryReadLsRemoteSha(remoteHeadRun.Output, branchRef, out var originHeadSha))
            {
                return RelocationGitState.Failure(
                    "Origin branch HEAD could not be read: " + DescribeFailure(remoteHeadRun),
                    remoteIsWindows);
            }

            return new RelocationGitState(
                true,
                string.Empty,
                new GitStatusBuckets(modified, staged, untracked, ignored, ahead, behind),
                branch,
                headSha.ToLowerInvariant(),
                originHeadSha,
                upstream,
                remoteIsWindows)
            {
                IgnoredFilesInventoryComplete = ignoredInventoryComplete
            };
        }

        private bool TryReadActiveGitOperation(
            string host,
            int port,
            string user,
            string password,
            string quotedPath,
            bool remoteIsWindows,
            out string operation,
            out string error)
        {
            operation = string.Empty;
            error = string.Empty;

            var gitDirRun = _ssh.RunCommand(
                host,
                port,
                user,
                password,
                $"git -C {quotedPath} rev-parse --absolute-git-dir");
            if (!gitDirRun.Success)
            {
                error = "Source Git operation state could not be inspected: "
                    + DescribeFailure(gitDirRun);
                return false;
            }

            var gitDirLines = SplitLines(gitDirRun.Output)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();
            if (gitDirLines.Count != 1
                || gitDirLines[0].IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            {
                error = "Source Git operation state could not be inspected safely.";
                return false;
            }

            var gitDir = remoteIsWindows
                ? gitDirLines[0].Replace('/', '\\')
                : gitDirLines[0];
            if (!IsAbsoluteRemotePath(gitDir, remoteIsWindows))
            {
                error = "Source Git operation directory was not absolute.";
                return false;
            }

            string probeCommand;
            if (remoteIsWindows)
            {
                var clauses = ActiveOperationMarkers.Select(marker =>
                    "if exist "
                    + Quote(
                        JoinRemotePath(gitDir, marker.RelativePath, remoteIsWindows: true),
                        remoteIsWindows: true)
                    + " (echo "
                    + marker.Operation
                    + ")");
                probeCommand = string.Join(" else ", clauses) + " else (echo none)";
            }
            else
            {
                var clauses = ActiveOperationMarkers.Select((marker, index) =>
                    (index == 0 ? "if" : "elif")
                    + " test -e "
                    + Quote(
                        JoinRemotePath(gitDir, marker.RelativePath, remoteIsWindows: false),
                        remoteIsWindows: false)
                    + "; then printf '%s' "
                    + Quote(marker.Operation, remoteIsWindows: false)
                    + ";");
                probeCommand = string.Join(" ", clauses)
                    + " else printf '%s' 'none'; fi";
            }

            var probeRun = _ssh.RunCommand(
                host,
                port,
                user,
                password,
                probeCommand);
            if (!probeRun.Success)
            {
                error = "Source Git operation state could not be inspected: "
                    + DescribeFailure(probeRun);
                return false;
            }

            var probeResult = probeRun.Output.Trim();
            if (string.Equals(probeResult, "none", StringComparison.Ordinal))
            {
                return true;
            }
            if (ActiveOperationMarkers.Any(marker =>
                string.Equals(
                    marker.Operation,
                    probeResult,
                    StringComparison.Ordinal)))
            {
                operation = probeResult;
                return true;
            }

            error = "Source Git operation state returned an unexpected result.";
            return false;
        }

        private bool TryDetectRemoteIsWindows(
            string host,
            int port,
            string user,
            string password,
            out bool remoteIsWindows,
            out string error)
        {
            remoteIsWindows = false;
            error = string.Empty;
            var windowsProbe = _ssh.RunCommand(host, port, user, password, "echo %OS%");
            if (!windowsProbe.Success)
            {
                error = "Remote OS probe failed: " + DescribeFailure(windowsProbe);
                return false;
            }

            if (windowsProbe.Output.IndexOf("Windows_NT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                remoteIsWindows = true;
                return true;
            }

            var posixProbe = _ssh.RunCommand(host, port, user, password, "uname -s");
            if (posixProbe.Success && IsKnownPosixOs(posixProbe.Output.Trim()))
            {
                return true;
            }

            error = "Remote OS could not be determined safely.";
            return false;
        }

        private IReadOnlyList<GitRemote> ReadRemotes(string host, int port, string user, string password, string quotedPath)
        {
            var run = _ssh.RunCommand(host, port, user, password,
                $"git -c safe.bareRepository=all -C {quotedPath} remote -v");
            if (!run.Success || string.IsNullOrWhiteSpace(run.Output))
            {
                return Array.Empty<GitRemote>();
            }

            var byName = new Dictionary<string, (string fetch, string push)>(StringComparer.Ordinal);
            foreach (var line in SplitLines(run.Output))
            {
                var tabIdx = line.IndexOf('\t');
                if (tabIdx <= 0) continue;

                var name = line.Substring(0, tabIdx);
                var rest = line.Substring(tabIdx + 1);
                var spaceIdx = rest.LastIndexOf(' ');
                if (spaceIdx < 0) continue;

                var url = rest.Substring(0, spaceIdx);
                var kind = rest.Substring(spaceIdx + 1).Trim('(', ')').Trim();

                (string fetch, string push) current = byName.TryGetValue(name, out var v)
                    ? v
                    : (string.Empty, string.Empty);
                if (string.Equals(kind, "fetch", StringComparison.OrdinalIgnoreCase))
                    current.fetch = url;
                else if (string.Equals(kind, "push", StringComparison.OrdinalIgnoreCase))
                    current.push = url;
                byName[name] = current;
            }

            var result = new List<GitRemote>();
            foreach (var kvp in byName)
            {
                result.Add(new GitRemote(
                    Name: kvp.Key,
                    FetchUrl: kvp.Value.fetch,
                    PushUrl: string.IsNullOrEmpty(kvp.Value.push) ? kvp.Value.fetch : kvp.Value.push));
            }
            return result;
        }

        private static bool ParsePorcelainV2(
            string stdout,
            List<string> modified,
            List<string> staged,
            List<string> untracked,
            ref int? ahead,
            ref int? behind,
            out string error)
        {
            error = string.Empty;

            foreach (var line in SplitLines(stdout))
            {
                if (string.IsNullOrEmpty(line)) continue;

                // # branch.ab +<ahead> -<behind>
                if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    var rest = line.Substring("# branch.ab ".Length).Trim();
                    var parts = rest.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2)
                    {
                        var aPart = parts[0].TrimStart('+');
                        var bPart = parts[1].TrimStart('-');
                        if (int.TryParse(aPart, out var a)) ahead = a;
                        if (int.TryParse(bPart, out var b)) behind = b;
                    }

                    continue;
                }

                var prefix = line[0];
                if (prefix == '#') continue;
                if (line.Length < 2)
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = "Source status contained a malformed porcelain-v2 record.";
                    }
                    continue;
                }

                if (prefix == '?')
                {
                    var p = line.Length > 2 ? line.Substring(2) : string.Empty;
                    if (!string.IsNullOrEmpty(p))
                    {
                        untracked.Add(p);
                    }
                    else if (string.IsNullOrEmpty(error))
                    {
                        error = "Source status contained a malformed porcelain-v2 record.";
                    }
                    continue;
                }

                if (prefix == '!')
                {
                    continue;
                }

                if (prefix == '1' || prefix == '2' || prefix == 'u')
                {
                    var parts = line.Split(' ');
                    int minimumParts = prefix == 'u' ? 11 : 9;
                    if (parts.Length < minimumParts)
                    {
                        if (string.IsNullOrEmpty(error))
                        {
                            error = "Source status contained a malformed porcelain-v2 record.";
                        }
                        continue;
                    }

                    var xy = parts[1];
                    if (xy.Length < 2)
                    {
                        if (string.IsNullOrEmpty(error))
                        {
                            error = "Source status contained a malformed porcelain-v2 record.";
                        }
                        continue;
                    }

                    char x = xy[0];
                    char y = xy[1];

                    int pathStart = prefix == '1' ? 8 : prefix == '2' ? 9 : 10;
                    var path = string.Join(' ', parts, pathStart, parts.Length - pathStart);
                    var tabIdx = path.IndexOf('\t');
                    if (tabIdx >= 0)
                    {
                        path = path.Substring(0, tabIdx);
                    }
                    if (string.IsNullOrEmpty(path))
                    {
                        if (string.IsNullOrEmpty(error))
                        {
                            error = "Source status contained a malformed porcelain-v2 record.";
                        }
                        continue;
                    }

                    if (prefix == 'u')
                    {
                        staged.Add(path);
                        modified.Add(path);
                        continue;
                    }

                    if (x != '.' && x != '?') staged.Add(path);
                    if (y != '.' && y != '?') modified.Add(path);
                    continue;
                }

                if (string.IsNullOrEmpty(error))
                {
                    error = "Source status contained an unsupported porcelain-v2 record type.";
                }
            }

            return string.IsNullOrEmpty(error);
        }

        private static void ParseBranchHeaders(string stdout, out string branch, out string upstream)
        {
            branch = string.Empty;
            upstream = string.Empty;
            foreach (var line in SplitLines(stdout))
            {
                if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
                {
                    branch = line.Substring("# branch.head ".Length).Trim();
                }
                else if (line.StartsWith("# branch.upstream ", StringComparison.Ordinal))
                {
                    upstream = line.Substring("# branch.upstream ".Length).Trim();
                }
            }
        }

        private static bool TryReadLsRemoteSha(string stdout, string expectedRef, out string sha)
        {
            sha = string.Empty;
            foreach (var line in SplitLines(stdout))
            {
                var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && string.Equals(parts[1], expectedRef, StringComparison.Ordinal)
                    && IsFullObjectId(parts[0]))
                {
                    if (!string.IsNullOrEmpty(sha)) return false;
                    sha = parts[0].ToLowerInvariant();
                }
            }
            return !string.IsNullOrEmpty(sha);
        }

        private static bool IsFullObjectId(string value)
        {
            if (value.Length != 40 && value.Length != 64) return false;
            return value.All(ch =>
                (ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'f')
                || (ch >= 'A' && ch <= 'F'));
        }

        private static bool IsKnownPosixOs(string value)
        {
            return value.StartsWith("Linux", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Darwin", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("FreeBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("OpenBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("NetBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("SunOS", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("CYGWIN", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("MINGW", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsAbsoluteRemotePath(
            string path,
            bool remoteIsWindows)
        {
            if (string.IsNullOrEmpty(path))
            {
                return false;
            }
            if (!remoteIsWindows)
            {
                return path[0] == '/';
            }

            return (path.Length >= 3
                    && char.IsLetter(path[0])
                    && path[1] == ':'
                    && path[2] == '\\')
                || path.StartsWith(@"\\", StringComparison.Ordinal);
        }

        private static string Quote(string value, bool remoteIsWindows)
        {
            return remoteIsWindows
                ? SshCommandQuoter.QuoteWindows(value)
                : SshCommandQuoter.QuotePosix(value);
        }

        private static string DescribeFailure(SshResult result)
        {
            var detail = string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error;
            return string.IsNullOrWhiteSpace(detail) ? "remote command failed." : detail.Trim();
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text)) yield break;
            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                yield return line;
            }
        }

        private static string JoinRemotePath(string baseDir, string subPath, bool remoteIsWindows)
        {
            char sep = remoteIsWindows ? '\\' : '/';
            var b = (baseDir ?? string.Empty).TrimEnd('/', '\\');
            var s = (subPath ?? string.Empty).TrimStart('/', '\\');
            if (string.IsNullOrEmpty(b)) return s;
            return b + sep + s;
        }
    }
}
