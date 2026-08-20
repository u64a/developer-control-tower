using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Ssh;

namespace ControlTower.Tests;

/// <summary>
/// Tests for <see cref="SshGitInspector"/>. Uses a hand-rolled fake
/// <see cref="ISshService"/> that returns canned outputs keyed by a
/// substring of the issued command. Verifies that classify / status
/// parse correctly across the WorkingTree, BareRepo, and NotARepo
/// shapes, including the porcelain=v2 <c># branch.ab</c> ahead/behind
/// line.
/// </summary>
public class SshGitInspectorTests
{
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task ClassifyAsync_NoGitDir_ReturnsNotARepo()
    {
        var ssh = new FakeSshService();
        ssh.Map("rev-parse --git-dir", SshResult.Fail("not a git repo"));

        var sut = new SshGitInspector(ssh);
        var result = await sut.ClassifyAsync("h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.IsType<NotARepo>(result);
    }

    [Fact]
    public async Task ClassifyAsync_BareRepo_DetectsBare()
    {
        var ssh = new FakeSshService();
        ssh.Map("rev-parse --git-dir", SshResult.Ok("."));
        ssh.Map("rev-parse --is-inside-work-tree", SshResult.Ok("false"));
        ssh.Map("remote -v", SshResult.Ok(""));

        var sut = new SshGitInspector(ssh);
        var result = await sut.ClassifyAsync("h", 22, "u", "p", "/srv/bare", CancellationToken.None);

        Assert.IsType<BareRepo>(result);
    }

    [Fact]
    public async Task ClassifyAsync_WorkingTree_ParsesOriginAndBranch()
    {
        var ssh = new FakeSshService();
        ssh.Map("rev-parse --git-dir", SshResult.Ok(".git"));
        ssh.Map("rev-parse --is-inside-work-tree", SshResult.Ok("true"));
        ssh.Map("symbolic-ref", SshResult.Ok("main"));
        ssh.Map("rev-parse --is-shallow-repository", SshResult.Ok("false"));
        ssh.Map("worktree list", SshResult.Ok("worktree /srv/foo\n"));
        ssh.Map("config --get-regexp", SshResult.Fail(""));
        ssh.Map("remote -v", SshResult.Ok(
            "origin\thttps://github.com/x/y.git (fetch)\n" +
            "origin\thttps://github.com/x/y.git (push)\n"));
        // sparse / submodules probes resolve to "N"
        ssh.MapDefault(SshResult.Ok("N"));

        var sut = new SshGitInspector(ssh);
        var result = await sut.ClassifyAsync("h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        var working = Assert.IsType<WorkingTreeRepo>(result);
        Assert.Equal("main", working.Branch);
        Assert.False(working.IsDetached);
        Assert.False(working.IsShallow);
        Assert.False(working.HasSubmodules);
        Assert.False(working.HasWorktrees);
        Assert.Equal("https://github.com/x/y.git", working.OriginUrl);
    }

    [Fact]
    public async Task ClassifyAsync_DetachedHead_FlagsDetached()
    {
        var ssh = new FakeSshService();
        ssh.Map("rev-parse --git-dir", SshResult.Ok(".git"));
        ssh.Map("rev-parse --is-inside-work-tree", SshResult.Ok("true"));
        ssh.Map("symbolic-ref", SshResult.Fail(""));
        ssh.Map("rev-parse --verify --quiet HEAD", SshResult.Ok("deadbeefcafebabe1234567890abcdef12345678"));
        ssh.Map("rev-parse --is-shallow-repository", SshResult.Ok("false"));
        ssh.Map("worktree list", SshResult.Ok("worktree /srv/foo\n"));
        ssh.Map("config --get-regexp", SshResult.Fail(""));
        ssh.Map("remote -v", SshResult.Ok(""));
        ssh.MapDefault(SshResult.Ok("N"));

        var sut = new SshGitInspector(ssh);
        var result = await sut.ClassifyAsync("h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        var working = Assert.IsType<WorkingTreeRepo>(result);
        Assert.True(working.IsDetached);
    }

    [Fact]
    public async Task ReadStatusAsync_CleanRepoWithUpstream_ParsesAheadBehind()
    {
        var ssh = new FakeSshService();
        // Clean tree, but 2 commits ahead, 1 commit behind upstream.
        ssh.Map("status --porcelain=v2",
            SshResult.Ok("# branch.oid abc\n# branch.head main\n# branch.upstream origin/main\n# branch.ab +2 -1\n"));
        ssh.Map("ls-files --others --ignored", SshResult.Ok(""));
        ssh.MapDefault(SshResult.Ok(""));

        var sut = new SshGitInspector(ssh);
        var result = await sut.ReadStatusAsync("h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.Empty(result.Modified);
        Assert.Empty(result.Staged);
        Assert.Empty(result.UntrackedNotIgnored);
        Assert.Equal(2, result.AheadOfOrigin);
        Assert.Equal(1, result.BehindOrigin);
    }

    [Fact]
    public async Task ReadStatusAsync_DirtyTree_ParsesModifiedStagedUntracked()
    {
        var ssh = new FakeSshService();
        // 1 entry: ".M" → workspace-modified (y='M'). "M." → staged (x='M').
        // "? new.txt" → untracked.
        ssh.Map("status --porcelain=v2", SshResult.Ok(
            "# branch.head main\n" +
            "1 .M N... 100644 100644 100644 abc def src/edited.cs\n" +
            "1 M. N... 100644 100644 100644 abc def src/staged.cs\n" +
            "? unkn/new.txt\n"));
        ssh.Map("ls-files --others --ignored", SshResult.Ok("bin/Debug/app.exe\nobj/cache.txt\n"));
        ssh.MapDefault(SshResult.Ok(""));

        var sut = new SshGitInspector(ssh);
        var result = await sut.ReadStatusAsync("h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.Single(result.Modified);
        Assert.Equal("src/edited.cs", result.Modified[0]);
        Assert.Single(result.Staged);
        Assert.Equal("src/staged.cs", result.Staged[0]);
        Assert.Single(result.UntrackedNotIgnored);
        Assert.Equal("unkn/new.txt", result.UntrackedNotIgnored[0]);
        Assert.Equal(2, result.IgnoredFiles.Count);
    }

    [Fact]
    public async Task ReadStatusAsync_NoUpstream_LeavesAheadBehindNull()
    {
        var ssh = new FakeSshService();
        // No "# branch.ab" line → no upstream configured.
        ssh.Map("status --porcelain=v2", SshResult.Ok("# branch.head main\n"));
        ssh.Map("ls-files --others --ignored", SshResult.Ok(""));
        ssh.MapDefault(SshResult.Ok(""));

        var sut = new SshGitInspector(ssh);
        var result = await sut.ReadStatusAsync("h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.Null(result.AheadOfOrigin);
        Assert.Null(result.BehindOrigin);
    }

    [Fact]
    public async Task ReadRelocationStateAsync_HeadCaptureFailure_FailsClosed()
    {
        var ssh = new FakeSshService();
        ssh.Map("echo %OS%", SshResult.Ok("%OS%"));
        ssh.Map("uname -s", SshResult.Ok("Linux"));
        ssh.Map("symbolic-ref", SshResult.Ok("main"));
        ssh.Map("rev-parse --verify HEAD", SshResult.Fail("cannot resolve HEAD"));

        var sut = new SshGitInspector(ssh);
        var result = await sut.ReadRelocationStateAsync(
            "h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("HEAD SHA", result.ErrorMessage);
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
        var path = "conflict-" + xy + ".txt";
        var ssh = CreateRelocationSsh(
            CleanRelocationStatus()
            + $"u {xy} N... 100644 100644 100644 100644 aaaaa bbbbb ccccc {path}\n");
        var sut = new SshGitInspector(ssh);

        var state = await sut.ReadRelocationStateAsync(
            "h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.True(state.Success, state.ErrorMessage);
        Assert.Contains(path, state.Status.Modified);
        Assert.Contains(path, state.Status.Staged);
        Assert.False(state.Status.IsClean);
    }

    [Theory]
    [InlineData("merge")]
    [InlineData("rebase")]
    [InlineData("cherry-pick")]
    [InlineData("revert")]
    [InlineData("cherry-pick/revert")]
    [InlineData("bisect")]
    public async Task ReadRelocationStateAsync_ActiveOperation_FailsClosed(
        string operation)
    {
        var ssh = CreateRelocationSsh(
            CleanRelocationStatus(),
            operation);
        var sut = new SshGitInspector(ssh);

        var state = await sut.ReadRelocationStateAsync(
            "h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.False(state.Success);
        Assert.Contains(operation, state.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadRelocationStateAsync_UnsupportedPorcelainRecord_FailsClosed()
    {
        var ssh = CreateRelocationSsh(
            CleanRelocationStatus() + "x future-record\n");
        var sut = new SshGitInspector(ssh);

        var state = await sut.ReadRelocationStateAsync(
            "h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.False(state.Success);
        Assert.Contains("unsupported", state.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadRelocationStateAsync_IgnoredInventoryOverCap_IsMarkedIncomplete()
    {
        var ignored = string.Join(
            "\n",
            Enumerable.Range(0, 1001).Select(index => $"ignored/{index:D4}.tmp"))
            + "\n";
        var ssh = CreateRelocationSsh(
            CleanRelocationStatus(),
            ignoredFiles: ignored);
        var sut = new SshGitInspector(ssh);

        var state = await sut.ReadRelocationStateAsync(
            "h", 22, "u", "p", "/srv/foo", CancellationToken.None);

        Assert.True(state.Success, state.ErrorMessage);
        Assert.Equal(1000, state.Status.IgnoredFiles.Count);
        Assert.False(state.IgnoredFilesInventoryComplete);
    }

    private static FakeSshService CreateRelocationSsh(
        string status,
        string activeOperation = "none",
        string ignoredFiles = "")
    {
        var ssh = new FakeSshService();
        ssh.Map("symbolic-ref", SshResult.Ok("main"));
        ssh.Map("rev-parse --verify HEAD", SshResult.Ok(HeadSha));
        ssh.Map("rev-parse --absolute-git-dir", SshResult.Ok("/srv/foo/.git"));
        ssh.Map("test -e ", SshResult.Ok(activeOperation));
        ssh.Map("status --porcelain=v2", SshResult.Ok(status));
        ssh.Map("ls-files --others --ignored", SshResult.Ok(ignoredFiles));
        ssh.Map(
            "ls-remote --heads",
            SshResult.Ok(HeadSha + "\trefs/heads/main\n"));
        ssh.MapDefault(SshResult.Fail("Unexpected SSH command."));
        return ssh;
    }

    private static string CleanRelocationStatus()
    {
        return "# branch.oid " + HeadSha + "\n"
            + "# branch.head main\n"
            + "# branch.upstream origin/main\n"
            + "# branch.ab +0 -0\n";
    }

    /// <summary>
    /// Test fake for <see cref="ISshService"/>. Returns canned outputs
    /// keyed by a substring match of the issued command. The first
    /// registered match wins.
    /// </summary>
    private sealed class FakeSshService : ISshService
    {
        private readonly List<(string needle, SshResult result)> _map = new();
        private SshResult _default = SshResult.Ok(string.Empty);

        public void Map(string needle, SshResult result) => _map.Add((needle, result));
        public void MapDefault(SshResult result) => _default = result;

        public SshResult TestConnection(string host, int port, string user, string password)
            => SshResult.Ok();

        public SshResult CreateDirectory(string host, int port, string user, string password, string remotePath)
            => SshResult.Ok();

        public SshResult RunCommand(string host, int port, string user, string password, string command)
        {
            foreach (var (needle, result) in _map)
            {
                if (command.IndexOf(needle, StringComparison.Ordinal) >= 0)
                {
                    return result;
                }
            }
            if (command == "echo %OS%") return SshResult.Ok("%OS%");
            if (command == "uname -s") return SshResult.Ok("Linux");
            return _default;
        }
    }
}
