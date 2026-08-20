#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Git
{
    /// <summary>
    /// Reads the local state of a folder using <c>git</c> plumbing
    /// commands. No mutation, no network IO. Designed so callers
    /// (Phase A Restore, Phase C Scan&amp;Register, Phase B Relocate)
    /// can ask "what is this folder?" without committing to a course of
    /// action.
    /// </summary>
    public sealed class GitWorkspaceInspector : IGitWorkspaceInspector
    {
        private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        // Caps the size of the ignored-files bucket so a node_modules
        // sized directory cannot blow up memory. Modified/Staged/
        // Untracked are uncapped — those are work that would be lost
        // and must always be reported faithfully.
        private const int IgnoredFilesCap = 1000;

        private static readonly (string RelativePath, string Operation, bool IsDirectory)[] ActiveOperationMarkers =
        {
            ("MERGE_HEAD", "merge", false),
            ("rebase-merge", "rebase", true),
            ("rebase-apply", "rebase", true),
            ("CHERRY_PICK_HEAD", "cherry-pick", false),
            ("REVERT_HEAD", "revert", false),
            ("sequencer", "cherry-pick/revert", true),
            ("BISECT_START", "bisect", false),
            ("BISECT_LOG", "bisect", false)
        };

        private readonly IGitProcessAdapter _adapter;

        public GitWorkspaceInspector(IGitProcessAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        public async Task<GitWorkspaceClassification> ClassifyAsync(string path, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                return new NotARepo(path ?? string.Empty);
            }

            // Cheap filesystem-first probe. This works around modern git's
            // `safe.bareRepository=explicit` default, which makes bare repos
            // unusable without an explicit --git-dir or GIT_DIR.
            var dotGit = System.IO.Path.Combine(path, ".git");
            bool hasDotGit = Directory.Exists(dotGit) || File.Exists(dotGit);

            bool looksBare =
                !hasDotGit &&
                File.Exists(System.IO.Path.Combine(path, "HEAD")) &&
                Directory.Exists(System.IO.Path.Combine(path, "objects")) &&
                Directory.Exists(System.IO.Path.Combine(path, "refs"));

            if (looksBare)
            {
                var remotesBare = await ReadRemotesAsync(path, ct).ConfigureAwait(false);
                return new BareRepo(path, path, remotesBare);
            }

            if (!hasDotGit)
            {
                return new NotARepo(path);
            }

            // It's a working tree. Confirm with git and gather details.
            var isInside = await RunInDirAsync(path, new[] { "rev-parse", "--is-inside-work-tree" }, ct)
                .ConfigureAwait(false);
            var gitDirRun = await RunInDirAsync(path, new[] { "rev-parse", "--git-dir" }, ct)
                .ConfigureAwait(false);

            if (gitDirRun.ExitCode != 0 ||
                !string.Equals(isInside.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase))
            {
                return new NotARepo(path);
            }

            var gitDir = ResolveGitDir(path, gitDirRun.Stdout.Trim());
            var remotes = await ReadRemotesAsync(path, ct).ConfigureAwait(false);

            // Branch — empty for unborn HEAD, "HEAD" prefix for detached.
            var branchRun = await RunInDirAsync(
                path, new[] { "symbolic-ref", "--short", "-q", "HEAD" }, ct).ConfigureAwait(false);
            string branch = branchRun.Stdout.Trim();
            bool detached = false;
            if (string.IsNullOrEmpty(branch))
            {
                // Could be detached or unborn. Try rev-parse HEAD; if it
                // succeeds we're detached on a commit; otherwise the
                // repo has no commits yet.
                var headRun = await RunInDirAsync(
                    path, new[] { "rev-parse", "--verify", "--quiet", "HEAD" }, ct).ConfigureAwait(false);
                if (headRun.ExitCode == 0)
                {
                    detached = true;
                    branch = headRun.Stdout.Trim();
                }
            }

            var shallow = await RunInDirAsync(
                path, new[] { "rev-parse", "--is-shallow-repository" }, ct).ConfigureAwait(false);
            bool isShallow = string.Equals(shallow.Stdout.Trim(), "true", StringComparison.OrdinalIgnoreCase);

            bool isSparse = File.Exists(System.IO.Path.Combine(gitDir, "info", "sparse-checkout"));

            bool isPartial = await IsPartialCloneAsync(path, ct).ConfigureAwait(false);

            bool hasWorktrees = await HasAdditionalWorktreesAsync(path, ct).ConfigureAwait(false);
            bool hasSubmodules = File.Exists(System.IO.Path.Combine(path, ".gitmodules"));

            string? originUrl = remotes
                .FirstOrDefault(r => string.Equals(r.Name, "origin", StringComparison.OrdinalIgnoreCase))
                ?.FetchUrl;

            return new WorkingTreeRepo(
                Path: path,
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

        public async Task<GitStatusBuckets> ReadStatusAsync(string workingTreePath, CancellationToken ct)
        {
            var modified = new List<string>();
            var staged = new List<string>();
            var untracked = new List<string>();
            var ignored = new List<string>();
            int? ahead = null;
            int? behind = null;

            if (string.IsNullOrWhiteSpace(workingTreePath) || !Directory.Exists(workingTreePath))
            {
                return new GitStatusBuckets(
                    Modified: modified,
                    Staged: staged,
                    UntrackedNotIgnored: untracked,
                    IgnoredFiles: ignored,
                    AheadOfOrigin: ahead,
                    BehindOrigin: behind);
            }

            // status --porcelain=v2 separates index vs worktree state
            // cleanly and gives us a stable parse target.
            var statusRun = await RunInDirAsync(
                workingTreePath,
                new[] { "status", "--porcelain=v2", "--untracked-files=normal" },
                ct).ConfigureAwait(false);

            ParsePorcelainV2(statusRun.Stdout, modified, staged, untracked, out _);

            // ls-files for ignored only (cheaper than asking status to
            // include them; we cap the result).
            var ignoredRun = await RunInDirAsync(
                workingTreePath,
                new[] { "ls-files", "--others", "--ignored", "--exclude-standard" },
                ct).ConfigureAwait(false);
            foreach (var line in SplitLines(ignoredRun.Stdout))
            {
                if (ignored.Count >= IgnoredFilesCap)
                {
                    break;
                }
                ignored.Add(line);
            }

            // Ahead/behind requires an upstream. Try @{upstream}; if
            // there is no upstream the rev-list call exits non-zero.
            var aheadBehind = await RunInDirAsync(
                workingTreePath,
                new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" },
                ct).ConfigureAwait(false);
            if (aheadBehind.ExitCode == 0)
            {
                var parts = aheadBehind.Stdout.Trim().Split(
                    new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2
                    && int.TryParse(parts[0], out var a)
                    && int.TryParse(parts[1], out var b))
                {
                    ahead = a;
                    behind = b;
                }
            }

            return new GitStatusBuckets(
                Modified: modified,
                Staged: staged,
                UntrackedNotIgnored: untracked,
                IgnoredFiles: ignored,
                AheadOfOrigin: ahead,
                BehindOrigin: behind);
        }

        public async Task<RelocationGitState> ReadRelocationStateAsync(
            string workingTreePath,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(workingTreePath) || !Directory.Exists(workingTreePath))
            {
                return RelocationGitState.Failure("Source working tree is unavailable.");
            }

            var branchRun = await RunInDirAsync(
                workingTreePath,
                new[] { "symbolic-ref", "--quiet", "--short", "HEAD" },
                ct).ConfigureAwait(false);
            var branch = branchRun.Stdout.Trim();
            if (branchRun.ExitCode != 0 || string.IsNullOrWhiteSpace(branch))
            {
                return RelocationGitState.Failure(
                    "Source HEAD is detached or its named branch could not be read.");
            }

            var headRun = await RunInDirAsync(
                workingTreePath,
                new[] { "rev-parse", "--verify", "HEAD^{commit}" },
                ct).ConfigureAwait(false);
            var headSha = headRun.Stdout.Trim();
            if (headRun.ExitCode != 0 || !IsFullObjectId(headSha))
            {
                return RelocationGitState.Failure(
                    "Source HEAD SHA could not be read as a full Git object ID.");
            }

            var operationState = await ReadActiveGitOperationAsync(
                workingTreePath,
                ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(operationState.Error))
            {
                return RelocationGitState.Failure(operationState.Error);
            }
            if (!string.IsNullOrEmpty(operationState.Operation))
            {
                return RelocationGitState.Failure(
                    $"Source has an active Git {operationState.Operation} operation.");
            }

            var statusRun = await RunInDirAsync(
                workingTreePath,
                new[] { "status", "--porcelain=v2", "--branch", "--untracked-files=normal" },
                ct).ConfigureAwait(false);
            if (statusRun.ExitCode != 0)
            {
                return RelocationGitState.Failure(
                    "Source status command failed: " + DescribeFailure(statusRun));
            }

            var modified = new List<string>();
            var staged = new List<string>();
            var untracked = new List<string>();
            var ignored = new List<string>();
            if (!ParsePorcelainV2(
                statusRun.Stdout,
                modified,
                staged,
                untracked,
                out var statusParseError))
            {
                return RelocationGitState.Failure(statusParseError);
            }
            ParseBranchHeaders(
                statusRun.Stdout,
                out var statusBranch,
                out var upstream,
                out var ahead,
                out var behind);

            if (!string.Equals(statusBranch, branch, StringComparison.Ordinal))
            {
                return RelocationGitState.Failure(
                    "Source status did not confirm the checked-out branch.");
            }

            var expectedUpstream = "origin/" + branch;
            if (!string.Equals(upstream, expectedUpstream, StringComparison.Ordinal))
            {
                return RelocationGitState.Failure(
                    $"Source branch '{branch}' must track '{expectedUpstream}'.");
            }

            if (!ahead.HasValue || !behind.HasValue)
            {
                return RelocationGitState.Failure(
                    "Source ahead/behind counts relative to origin could not be established.");
            }

            var ignoredRun = await RunInDirAsync(
                workingTreePath,
                new[] { "ls-files", "--others", "--ignored", "--exclude-standard" },
                ct).ConfigureAwait(false);
            if (ignoredRun.ExitCode != 0)
            {
                return RelocationGitState.Failure(
                    "Source ignored-file inventory failed: " + DescribeFailure(ignoredRun));
            }
            bool ignoredInventoryComplete = true;
            foreach (var line in SplitLines(ignoredRun.Stdout))
            {
                if (ignored.Count >= IgnoredFilesCap)
                {
                    ignoredInventoryComplete = false;
                    break;
                }
                ignored.Add(line);
            }

            var branchRef = "refs/heads/" + branch;
            var remoteHeadRun = await RunInDirAsync(
                workingTreePath,
                new[] { "ls-remote", "--heads", "--", "origin", branchRef },
                ct).ConfigureAwait(false);
            if (remoteHeadRun.ExitCode != 0
                || !TryReadLsRemoteSha(remoteHeadRun.Stdout, branchRef, out var originHeadSha))
            {
                return RelocationGitState.Failure(
                    "Origin branch HEAD could not be read: " + DescribeFailure(remoteHeadRun));
            }

            return new RelocationGitState(
                true,
                string.Empty,
                new GitStatusBuckets(modified, staged, untracked, ignored, ahead, behind),
                branch,
                headSha.ToLowerInvariant(),
                originHeadSha,
                upstream,
                null)
            {
                IgnoredFilesInventoryComplete = ignoredInventoryComplete
            };
        }

        public string GetRemoteIdentity(string remote)
        {
            var canonical = CanonicalizeRemote(remote);
            if (string.IsNullOrEmpty(canonical))
            {
                return string.Empty;
            }

            // CanonicalizeRemote always emits `scheme://[user@]host[:port]/path`
            // — strip the scheme prefix and any user-info to get a pure
            // host/path identity. We deliberately also drop the SSH user
            // here: dedupe is host-and-path only. The same repo cloned as
            // git@github.com/.../x and as https://github.com/.../x must hash
            // to the same identity.
            var schemeIdx = canonical.IndexOf("://", StringComparison.Ordinal);
            var after = schemeIdx >= 0 ? canonical.Substring(schemeIdx + 3) : canonical;

            var atIdx = after.IndexOf('@');
            if (atIdx >= 0)
            {
                after = after.Substring(atIdx + 1);
            }

            return after;
        }

        public string CanonicalizeRemote(string remote)
        {
            if (string.IsNullOrWhiteSpace(remote))
            {
                return string.Empty;
            }

            var trimmed = remote.Trim();

            // Strip a trailing slash for normalisation.
            while (trimmed.Length > 1 && (trimmed[trimmed.Length - 1] == '/' || trimmed[trimmed.Length - 1] == '\\'))
            {
                trimmed = trimmed.Substring(0, trimmed.Length - 1);
            }

            // scp-like form: user@host:path  (no scheme, single ':' separating host and path)
            var scpMatch = Regex.Match(
                trimmed,
                @"^(?<user>[^@/:\s]+)@(?<host>[^:/\s]+):(?<path>[^\s].*)$");
            if (scpMatch.Success && !trimmed.Contains("://"))
            {
                var user = scpMatch.Groups["user"].Value;
                var host = scpMatch.Groups["host"].Value.ToLowerInvariant();
                var path = NormalisePath(scpMatch.Groups["path"].Value);
                return "ssh://" + user + "@" + host + "/" + path;
            }

            // Anything with a scheme is parsed as a URI.
            if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme.ToLowerInvariant();
                var host = uri.Host.ToLowerInvariant();
                var port = uri.IsDefaultPort ? string.Empty : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                var pathOnly = NormalisePath(uri.AbsolutePath.TrimStart('/'));

                // For ssh:// keep the user (it's part of the identity);
                // for https:// strip user-info (it is credential).
                if (scheme == "ssh")
                {
                    var user = string.IsNullOrEmpty(uri.UserInfo) ? string.Empty : uri.UserInfo + "@";
                    return "ssh://" + user + host + port + "/" + pathOnly;
                }

                return scheme + "://" + host + port + "/" + pathOnly;
            }

            return trimmed;
        }

        private async Task<GitRunResult> RunInDirAsync(
            string workingDirectory, string[] args, CancellationToken ct)
        {
            // Prepend `-c safe.bareRepository=all` so the inspector can
            // probe bare repos even when the user's git config has the
            // (now default) `safe.bareRepository=explicit` setting. The
            // inspector is read-only; this override is scoped to a single
            // invocation via -c and does not modify any config file.
            var safeArgs = new List<string>(args.Length + 2)
            {
                "-c", "safe.bareRepository=all"
            };
            safeArgs.AddRange(args);
            return await _adapter.RunAsync(
                safeArgs, workingDirectory, DefaultTimeout, progress: null, ct).ConfigureAwait(false);
        }

        private static string ResolveGitDir(string repoPath, string relOrAbsGitDir)
        {
            if (string.IsNullOrWhiteSpace(relOrAbsGitDir))
            {
                return System.IO.Path.Combine(repoPath, ".git");
            }

            try
            {
                if (System.IO.Path.IsPathRooted(relOrAbsGitDir))
                {
                    return System.IO.Path.GetFullPath(relOrAbsGitDir);
                }
                return System.IO.Path.GetFullPath(System.IO.Path.Combine(repoPath, relOrAbsGitDir));
            }
            catch
            {
                return relOrAbsGitDir;
            }
        }

        private async Task<IReadOnlyList<GitRemote>> ReadRemotesAsync(string path, CancellationToken ct)
        {
            var run = await RunInDirAsync(path, new[] { "remote", "-v" }, ct).ConfigureAwait(false);
            if (run.ExitCode != 0 || string.IsNullOrWhiteSpace(run.Stdout))
            {
                return Array.Empty<GitRemote>();
            }

            var byName = new Dictionary<string, (string fetch, string push)>(StringComparer.Ordinal);

            foreach (var line in SplitLines(run.Stdout))
            {
                // origin<TAB>https://x/y.git (fetch)
                var tabIdx = line.IndexOf('\t');
                if (tabIdx <= 0)
                {
                    continue;
                }

                var name = line.Substring(0, tabIdx);
                var rest = line.Substring(tabIdx + 1);
                var spaceIdx = rest.LastIndexOf(' ');
                if (spaceIdx < 0)
                {
                    continue;
                }

                var url = rest.Substring(0, spaceIdx);
                var kind = rest.Substring(spaceIdx + 1).Trim('(', ')').Trim();

                (string fetch, string push) current = byName.TryGetValue(name, out var v)
                    ? v
                    : (string.Empty, string.Empty);
                if (string.Equals(kind, "fetch", StringComparison.OrdinalIgnoreCase))
                {
                    current.fetch = url;
                }
                else if (string.Equals(kind, "push", StringComparison.OrdinalIgnoreCase))
                {
                    current.push = url;
                }
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

        private async Task<bool> IsPartialCloneAsync(string path, CancellationToken ct)
        {
            // A partial clone keeps the filter under remote.<name>.partialclonefilter.
            var run = await RunInDirAsync(
                path, new[] { "config", "--get-regexp", "^remote\\..*\\.partialclonefilter$" }, ct)
                .ConfigureAwait(false);
            return run.ExitCode == 0 && !string.IsNullOrWhiteSpace(run.Stdout);
        }

        private async Task<bool> HasAdditionalWorktreesAsync(string path, CancellationToken ct)
        {
            var run = await RunInDirAsync(
                path, new[] { "worktree", "list", "--porcelain" }, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                return false;
            }

            int worktreeCount = 0;
            foreach (var line in SplitLines(run.Stdout))
            {
                if (line.StartsWith("worktree ", StringComparison.Ordinal))
                {
                    worktreeCount++;
                }
            }
            return worktreeCount > 1;
        }

        private async Task<(string Operation, string Error)> ReadActiveGitOperationAsync(
            string workingTreePath,
            CancellationToken ct)
        {
            var gitDirRun = await RunInDirAsync(
                workingTreePath,
                new[] { "rev-parse", "--absolute-git-dir" },
                ct).ConfigureAwait(false);
            if (gitDirRun.ExitCode != 0)
            {
                return (
                    string.Empty,
                    "Source Git operation state could not be inspected: "
                    + DescribeFailure(gitDirRun));
            }

            var gitDirLines = SplitLines(gitDirRun.Stdout).ToList();
            if (gitDirLines.Count != 1
                || string.IsNullOrWhiteSpace(gitDirLines[0])
                || !Path.IsPathRooted(gitDirLines[0]))
            {
                return (
                    string.Empty,
                    "Source Git operation state could not be inspected safely.");
            }

            var gitDir = gitDirLines[0];
            if (!Directory.Exists(gitDir))
            {
                return (
                    string.Empty,
                    "Source Git operation directory is unavailable.");
            }

            try
            {
                foreach (var marker in ActiveOperationMarkers)
                {
                    var markerPath = Path.Combine(gitDir, marker.RelativePath);
                    bool exists = marker.IsDirectory
                        ? Directory.Exists(markerPath)
                        : File.Exists(markerPath);
                    if (exists)
                    {
                        return (marker.Operation, string.Empty);
                    }
                }
            }
            catch (Exception ex)
            {
                return (
                    string.Empty,
                    "Source Git operation state could not be inspected: "
                    + ex.Message);
            }

            return (string.Empty, string.Empty);
        }

        private static bool ParsePorcelainV2(
            string stdout,
            List<string> modified,
            List<string> staged,
            List<string> untracked,
            out string error)
        {
            error = string.Empty;

            // Reference: git status --porcelain=v2 documentation.
            // Lines we care about start with:
            //   "1 XY ..." or "2 XY ..."  — changed/renamed tracked entries
            //   "u XY ..."                 — unresolved conflict
            //   "? path"                  — untracked-not-ignored
            //   "! path"                  — ignored (only when --ignored requested; we don't)
            foreach (var line in SplitLines(stdout))
            {
                if (line[0] == '#')
                {
                    continue;
                }

                if (line.Length < 2)
                {
                    if (string.IsNullOrEmpty(error))
                    {
                        error = "Source status contained a malformed porcelain-v2 record.";
                    }
                    continue;
                }

                var prefix = line[0];
                if (prefix == '?')
                {
                    // "? path"
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
                    // Format: "1 XY sub mH mI mW hH hI path"
                    //     or: "2 XY sub mH mI mW hH hI X<score> path<tab>orig_path"
                    //     or: "u XY sub m1 m2 m3 mW h1 h2 h3 path"
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

                    char x = xy[0]; // index (staged)
                    char y = xy[1]; // worktree (unstaged)

                    int pathStart = prefix == '1' ? 8 : prefix == '2' ? 9 : 10;
                    var path = string.Join(' ', parts, pathStart, parts.Length - pathStart);
                    // For rename ("2"), path may contain a tab separating new and old; keep new.
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

                    if (x != '.' && x != '?')
                    {
                        staged.Add(path);
                    }

                    if (y != '.' && y != '?')
                    {
                        modified.Add(path);
                    }
                    continue;
                }

                if (string.IsNullOrEmpty(error))
                {
                    error = "Source status contained an unsupported porcelain-v2 record type.";
                }
            }

            return string.IsNullOrEmpty(error);
        }

        private static void ParseBranchHeaders(
            string stdout,
            out string branch,
            out string upstream,
            out int? ahead,
            out int? behind)
        {
            branch = string.Empty;
            upstream = string.Empty;
            ahead = null;
            behind = null;

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
                else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
                {
                    var parts = line.Substring("# branch.ab ".Length)
                        .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2
                        && int.TryParse(parts[0].TrimStart('+'), out var parsedAhead)
                        && int.TryParse(parts[1].TrimStart('-'), out var parsedBehind))
                    {
                        ahead = parsedAhead;
                        behind = parsedBehind;
                    }
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

        private static string DescribeFailure(GitRunResult run)
        {
            var detail = string.IsNullOrWhiteSpace(run.Stderr) ? run.Stdout : run.Stderr;
            return string.IsNullOrWhiteSpace(detail)
                ? $"git exited with code {run.ExitCode}."
                : detail.Trim();
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                yield break;
            }

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.Length > 0)
                {
                    yield return line;
                }
            }
        }

        private static string NormalisePath(string path)
        {
            var normalised = (path ?? string.Empty).Replace('\\', '/').TrimEnd('/');
            // Strip a single trailing .git extension if present.
            if (normalised.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            {
                normalised = normalised.Substring(0, normalised.Length - 4);
            }
            return normalised;
        }
    }
}
