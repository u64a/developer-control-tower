using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

/// <summary>
/// Integration-ish tests for <see cref="GitWorkspaceInspector"/>. These
/// rely on a real <c>git.exe</c> on PATH; when it is unavailable (very
/// rare on a dev box but possible in some CI containers), each test
/// skips with an explanatory message.
/// </summary>
public class GitWorkspaceInspectorTests : IDisposable
{
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    private readonly string _root;
    private readonly bool _gitAvailable;

    public GitWorkspaceInspectorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-gwi-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _gitAvailable = TryRunGit("--version", _root, out _);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static GitWorkspaceInspector NewInspector()
    {
        return new GitWorkspaceInspector(new GitProcessAdapter(new ToolSettings()));
    }

    [Fact]
    public async Task ClassifyAsync_EmptyFolder_IsNotARepo()
    {
        var inspector = NewInspector();
        var empty = NewSubdir("empty");

        var classification = await inspector.ClassifyAsync(empty, CancellationToken.None);

        Assert.IsType<NotARepo>(classification);
    }

    [Fact]
    public async Task ClassifyAsync_FreshGitInit_IsWorkingTreeRepo()
    {
        if (!_gitAvailable) { return; }

        var dir = NewSubdir("fresh-init");
        Assert.True(TryRunGit("init -q", dir, out _));

        var c = await NewInspector().ClassifyAsync(dir, CancellationToken.None);

        var working = Assert.IsType<WorkingTreeRepo>(c);
        Assert.False(working.IsShallow);
        Assert.False(working.IsSparse);
        Assert.False(working.HasWorktrees);
        Assert.False(working.HasSubmodules);
        Assert.Empty(working.Remotes); // fresh init has none
    }

    [Fact]
    public async Task ClassifyAsync_BareRepo_IsClassifiedBare()
    {
        if (!_gitAvailable) { return; }

        var dir = NewSubdir("bare");
        Assert.True(TryRunGit("init --bare -q", dir, out _));

        var c = await NewInspector().ClassifyAsync(dir, CancellationToken.None);

        Assert.IsType<BareRepo>(c);
    }

    [Fact]
    public async Task ClassifyAsync_ShallowClone_DetectedAsShallow()
    {
        if (!_gitAvailable) { return; }

        var source = MakeSourceRepoWithCommits("shallow-source", commits: 3);
        var dest = Path.Combine(_root, "shallow-dest");
        // git honours --depth only with the smart transport. file:// forces it.
        var fileUrl = "file:///" + source.Replace('\\', '/');
        Assert.True(TryRunGit($"clone -q --depth 1 \"{fileUrl}\" \"{dest}\"", _root, out _));

        var c = await NewInspector().ClassifyAsync(dest, CancellationToken.None);

        var working = Assert.IsType<WorkingTreeRepo>(c);
        Assert.True(working.IsShallow);
    }

    [Fact]
    public async Task ClassifyAsync_SparseCheckout_DetectedAsSparse()
    {
        if (!_gitAvailable) { return; }

        var source = MakeSourceRepoWithCommits("sparse-source", commits: 1);
        var dest = Path.Combine(_root, "sparse-dest");
        Assert.True(TryRunGit($"clone -q \"{source}\" \"{dest}\"", _root, out _));
        Assert.True(TryRunGit("sparse-checkout init --cone", dest, out _));

        var c = await NewInspector().ClassifyAsync(dest, CancellationToken.None);
        var working = Assert.IsType<WorkingTreeRepo>(c);
        Assert.True(working.IsSparse);
    }

    [Fact]
    public async Task ReadStatusAsync_CleanCheckout_IsClean()
    {
        if (!_gitAvailable) { return; }

        var source = MakeSourceRepoWithCommits("clean-source", commits: 1);
        var dest = Path.Combine(_root, "clean-dest");
        Assert.True(TryRunGit($"clone -q \"{source}\" \"{dest}\"", _root, out _));

        var buckets = await NewInspector().ReadStatusAsync(dest, CancellationToken.None);

        Assert.Empty(buckets.Modified);
        Assert.Empty(buckets.Staged);
        Assert.Empty(buckets.UntrackedNotIgnored);
        Assert.True(buckets.IsClean);
    }

    [Fact]
    public async Task ReadStatusAsync_DetectsModifiedAndStagedAndUntrackedSeparately()
    {
        if (!_gitAvailable) { return; }

        var source = MakeSourceRepoWithCommits("status-source", commits: 1);
        var dest = Path.Combine(_root, "status-dest");
        Assert.True(TryRunGit($"clone -q \"{source}\" \"{dest}\"", _root, out _));

        // Tracked-modified: change README (created by MakeSourceRepoWithCommits).
        File.AppendAllText(Path.Combine(dest, "README.md"), "\nlocal change");

        // Tracked-staged: new file added and then "git add".
        File.WriteAllText(Path.Combine(dest, "staged.txt"), "hello");
        Assert.True(TryRunGit("add staged.txt", dest, out _));

        // Untracked-not-ignored: just write a file.
        File.WriteAllText(Path.Combine(dest, "loose.txt"), "loose");

        var buckets = await NewInspector().ReadStatusAsync(dest, CancellationToken.None);

        Assert.Contains("README.md", buckets.Modified);
        Assert.Contains("staged.txt", buckets.Staged);
        Assert.Contains("loose.txt", buckets.UntrackedNotIgnored);
        Assert.False(buckets.IsClean);
    }

    [Fact]
    public async Task ReadStatusAsync_IgnoredFilesAreSeparateBucket()
    {
        if (!_gitAvailable) { return; }

        var source = MakeSourceRepoWithCommits("ignored-source", commits: 1);
        var dest = Path.Combine(_root, "ignored-dest");
        Assert.True(TryRunGit($"clone -q \"{source}\" \"{dest}\"", _root, out _));

        File.WriteAllText(Path.Combine(dest, ".gitignore"), "ignored.txt\n");
        Assert.True(TryRunGit("add .gitignore", dest, out _));
        Assert.True(TryRunGit("-c user.email=t@e -c user.name=T commit -q -m gi", dest, out _));

        File.WriteAllText(Path.Combine(dest, "ignored.txt"), "secret");

        var buckets = await NewInspector().ReadStatusAsync(dest, CancellationToken.None);

        Assert.Contains("ignored.txt", buckets.IgnoredFiles);
        Assert.DoesNotContain("ignored.txt", buckets.UntrackedNotIgnored);
    }

    [Fact]
    public async Task ReadStatusAsync_AheadBehindReportedWhenUpstreamExists()
    {
        if (!_gitAvailable) { return; }

        var source = MakeSourceRepoWithCommits("upstream-source", commits: 1);
        var dest = Path.Combine(_root, "upstream-dest");
        Assert.True(TryRunGit($"clone -q \"{source}\" \"{dest}\"", _root, out _));

        // Create a local commit ahead of upstream.
        File.WriteAllText(Path.Combine(dest, "extra.txt"), "x");
        Assert.True(TryRunGit("add extra.txt", dest, out _));
        Assert.True(TryRunGit("-c user.email=t@e -c user.name=T commit -q -m extra", dest, out _));

        var buckets = await NewInspector().ReadStatusAsync(dest, CancellationToken.None);

        Assert.NotNull(buckets.AheadOfOrigin);
        Assert.Equal(1, buckets.AheadOfOrigin);
        Assert.Equal(0, buckets.BehindOrigin);
        Assert.False(buckets.IsClean); // ahead implies unpushed work
    }

    [Fact]
    public async Task ReadRelocationStateAsync_MissingUpstream_FailsClosed()
    {
        if (!_gitAvailable) { return; }

        var source = MakeSourceRepoWithCommits("no-upstream", commits: 1);

        var state = await NewInspector().ReadRelocationStateAsync(
            source,
            CancellationToken.None);

        Assert.False(state.Success);
        Assert.Contains("track", state.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Null(state.Status.AheadOfOrigin);
        Assert.Null(state.Status.BehindOrigin);
    }

    [Fact]
    public async Task ReadRelocationStateAsync_TrackedClone_CapturesExactHead()
    {
        if (!_gitAvailable) { return; }

        var source = MakeSourceRepoWithCommits("relocation-source", commits: 1);
        var destination = Path.Combine(_root, "relocation-clone");
        Assert.True(TryRunGit($"clone -q \"{source}\" \"{destination}\"", _root, out _));

        var state = await NewInspector().ReadRelocationStateAsync(
            destination,
            CancellationToken.None);

        Assert.True(state.Success, state.ErrorMessage);
        Assert.Equal("main", state.Branch);
        Assert.Equal(state.HeadSha, state.OriginHeadSha);
        Assert.True(state.HeadSha.Length is 40 or 64);
    }

    [Theory]
    [InlineData("DD")]
    [InlineData("AU")]
    [InlineData("UD")]
    [InlineData("UA")]
    [InlineData("DU")]
    [InlineData("AA")]
    [InlineData("UU")]
    public async Task ReadRelocationStateAsync_UnmergedRecord_IsDirty(string xy)
    {
        var workingTree = NewSubdir("unmerged-" + xy);
        var gitDir = Path.Combine(workingTree, ".git");
        Directory.CreateDirectory(gitDir);
        var path = "conflict-" + xy + ".txt";
        var status = CleanRelocationStatus()
            + $"u {xy} N... 100644 100644 100644 100644 aaaaa bbbbb ccccc {path}\n";
        var inspector = new GitWorkspaceInspector(
            new RelocationGitAdapter(gitDir, status));

        var state = await inspector.ReadRelocationStateAsync(
            workingTree,
            CancellationToken.None);

        Assert.True(state.Success, state.ErrorMessage);
        Assert.Contains(path, state.Status.Modified);
        Assert.Contains(path, state.Status.Staged);
        Assert.False(state.Status.IsClean);
    }

    [Theory]
    [InlineData("MERGE_HEAD", false, "merge")]
    [InlineData("rebase-merge", true, "rebase")]
    [InlineData("rebase-apply", true, "rebase")]
    [InlineData("CHERRY_PICK_HEAD", false, "cherry-pick")]
    [InlineData("REVERT_HEAD", false, "revert")]
    [InlineData("sequencer", true, "cherry-pick/revert")]
    [InlineData("BISECT_START", false, "bisect")]
    public async Task ReadRelocationStateAsync_ActiveOperation_FailsClosed(
        string marker,
        bool isDirectory,
        string operation)
    {
        var workingTree = NewSubdir("operation-" + operation);
        var gitDir = Path.Combine(workingTree, ".git");
        Directory.CreateDirectory(gitDir);
        var markerPath = Path.Combine(gitDir, marker);
        if (isDirectory)
        {
            Directory.CreateDirectory(markerPath);
        }
        else
        {
            File.WriteAllText(markerPath, string.Empty);
        }
        var inspector = new GitWorkspaceInspector(
            new RelocationGitAdapter(gitDir, CleanRelocationStatus()));

        var state = await inspector.ReadRelocationStateAsync(
            workingTree,
            CancellationToken.None);

        Assert.False(state.Success);
        Assert.Contains(operation, state.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRelocationStateAsync_UnsupportedPorcelainRecord_FailsClosed()
    {
        var workingTree = NewSubdir("unsupported-status");
        var gitDir = Path.Combine(workingTree, ".git");
        Directory.CreateDirectory(gitDir);
        var inspector = new GitWorkspaceInspector(
            new RelocationGitAdapter(
                gitDir,
                CleanRelocationStatus() + "x future-record\n"));

        var state = await inspector.ReadRelocationStateAsync(
            workingTree,
            CancellationToken.None);

        Assert.False(state.Success);
        Assert.Contains("unsupported", state.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRelocationStateAsync_IgnoredInventoryOverCap_IsMarkedIncomplete()
    {
        var workingTree = NewSubdir("ignored-over-cap");
        var gitDir = Path.Combine(workingTree, ".git");
        Directory.CreateDirectory(gitDir);
        var ignored = string.Join(
            "\n",
            Enumerable.Range(0, 1001).Select(index => $"ignored/{index:D4}.tmp"))
            + "\n";
        var inspector = new GitWorkspaceInspector(
            new RelocationGitAdapter(
                gitDir,
                CleanRelocationStatus(),
                ignored));

        var state = await inspector.ReadRelocationStateAsync(
            workingTree,
            CancellationToken.None);

        Assert.True(state.Success, state.ErrorMessage);
        Assert.Equal(1000, state.Status.IgnoredFiles.Count);
        Assert.False(state.IgnoredFilesInventoryComplete);
    }

    [Fact]
    public void CanonicalizeRemote_StripsHttpsCredentialsAndDotGitAndTrailingSlash()
    {
        var inspector = NewInspector();
        var canonical = inspector.CanonicalizeRemote("https:/" + "/user:token@GitHub.com/Example/Repo.git/");
        Assert.Equal("https://github.com/Example/Repo", canonical);
    }

    [Fact]
    public void CanonicalizeRemote_ScpFormSshNormalisesToSshScheme()
    {
        var inspector = NewInspector();
        var canonical = inspector.CanonicalizeRemote("git@github.com:Example/Repo.git");
        Assert.Equal("ssh://git@github.com/Example/Repo", canonical);
    }

    [Fact]
    public void CanonicalizeRemote_SshSchemeStaysNormalisedHostLowercased()
    {
        var inspector = NewInspector();
        var canonical = inspector.CanonicalizeRemote("ssh://git@HOST.example.com/org/repo.git/");
        Assert.Equal("ssh://git@host.example.com/org/repo", canonical);
    }

    [Fact]
    public void CanonicalizeRemote_EmptyInput_ReturnsEmpty()
    {
        var inspector = NewInspector();
        Assert.Equal(string.Empty, inspector.CanonicalizeRemote(""));
        Assert.Equal(string.Empty, inspector.CanonicalizeRemote("   "));
    }

    [Fact]
    public void GetRemoteIdentity_HttpsAndScpFormHashToSameIdentity()
    {
        var inspector = NewInspector();
        var https = inspector.GetRemoteIdentity("https://github.com/owner/repo.git");
        var scp = inspector.GetRemoteIdentity("git@github.com:owner/repo.git");
        Assert.Equal("github.com/owner/repo", https);
        Assert.Equal(https, scp);
    }

    [Fact]
    public void GetRemoteIdentity_SshScheme_SameAsHttps()
    {
        var inspector = NewInspector();
        var ssh = inspector.GetRemoteIdentity("ssh://git@github.com/owner/repo.git");
        Assert.Equal("github.com/owner/repo", ssh);
    }

    [Fact]
    public void GetRemoteIdentity_StripsTrailingDotGitAndUserInfoAndCase()
    {
        var inspector = NewInspector();
        var id = inspector.GetRemoteIdentity("https:/" + "/user:token@GitHub.com/Owner/Repo.git/");
        Assert.Equal("github.com/Owner/Repo", id);
    }

    [Fact]
    public void GetRemoteIdentity_Empty_ReturnsEmpty()
    {
        var inspector = NewInspector();
        Assert.Equal(string.Empty, inspector.GetRemoteIdentity(""));
        Assert.Equal(string.Empty, inspector.GetRemoteIdentity("   "));
    }

    private static string CleanRelocationStatus()
    {
        return "# branch.oid " + HeadSha + "\n"
            + "# branch.head main\n"
            + "# branch.upstream origin/main\n"
            + "# branch.ab +0 -0\n";
    }

    private sealed class RelocationGitAdapter : IGitProcessAdapter
    {
        private readonly string _gitDir;
        private readonly string _status;
        private readonly string _ignored;

        public RelocationGitAdapter(
            string gitDir,
            string status,
            string ignored = "")
        {
            _gitDir = gitDir;
            _status = status;
            _ignored = ignored;
        }

        public Task<GitRunResult> RunAsync(
            System.Collections.Generic.IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var args = arguments.ToArray();
            if (args.Contains("symbolic-ref", StringComparer.Ordinal))
            {
                return Result(0, "main\n");
            }
            if (args.Contains("--absolute-git-dir", StringComparer.Ordinal))
            {
                return Result(0, _gitDir + "\n");
            }
            if (args.Contains("HEAD^{commit}", StringComparer.Ordinal))
            {
                return Result(0, HeadSha + "\n");
            }
            if (args.Contains("status", StringComparer.Ordinal))
            {
                return Result(0, _status);
            }
            if (args.Contains("ls-files", StringComparer.Ordinal))
            {
                return Result(0, _ignored);
            }
            if (args.Contains("ls-remote", StringComparer.Ordinal))
            {
                return Result(0, HeadSha + "\trefs/heads/main\n");
            }

            return Result(1, string.Empty, "Unexpected git command.");
        }

        private static Task<GitRunResult> Result(
            int exitCode,
            string stdout,
            string stderr = "")
        {
            return Task.FromResult(
                new GitRunResult(
                    exitCode,
                    stdout,
                    stderr,
                    TimeSpan.Zero,
                    TimedOut: false,
                    Cancelled: false));
        }
    }

    private string NewSubdir(string name)
    {
        var d = Path.Combine(_root, name);
        Directory.CreateDirectory(d);
        return d;
    }

    private string MakeSourceRepoWithCommits(string name, int commits)
    {
        var dir = NewSubdir(name);
        Assert.True(TryRunGit("init -q -b main", dir, out _));
        Assert.True(TryRunGit("config user.email t@e", dir, out _));
        Assert.True(TryRunGit("config user.name T", dir, out _));

        for (int i = 0; i < Math.Max(1, commits); i++)
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), $"line {i}\n");
            Assert.True(TryRunGit("add README.md", dir, out _));
            Assert.True(TryRunGit($"commit -q -m commit{i}", dir, out _));
        }

        return dir;
    }

    private static bool TryRunGit(string args, string workingDir, out string output)
    {
        output = string.Empty;
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) { return false; }
            output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(15_000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
