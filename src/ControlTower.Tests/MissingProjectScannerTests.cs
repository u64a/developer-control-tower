using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

/// <summary>
/// Verifies <see cref="MissingProjectScanner"/> classifies each input
/// project against its expected path correctly. Real git is invoked
/// through <see cref="GitWorkspaceInspector"/>; tests skip silently if
/// <c>git.exe</c> is not on PATH (the same convention used by the
/// existing GitWorkspaceInspectorTests).
/// </summary>
public class MissingProjectScannerTests : IDisposable
{
    private readonly string _root;
    private readonly bool _gitAvailable;
    private readonly IGitWorkspaceInspector _inspector;

    public MissingProjectScannerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _gitAvailable = TryRunGit("--version", _root, out _);
        _inspector = new GitWorkspaceInspector(new GitProcessAdapter(new ToolSettings()));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private MissingProjectScanner NewScanner() => new MissingProjectScanner(_inspector);

    [Fact]
    public async Task ScanAsync_MissingFolder_ClassifiedMissing()
    {
        var expected = Path.Combine(_root, "absent");
        var input = LocalProject("p1", expected, "https://github.com/org/repo.git");

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal(RestoreClassification.Missing, candidate.Classification);
        Assert.Equal("p1", candidate.ProjectId);
    }

    [Fact]
    public async Task ScanAsync_EmptyFolder_ClassifiedEmpty()
    {
        var expected = Path.Combine(_root, "empty");
        Directory.CreateDirectory(expected);
        var input = LocalProject("p2", expected, "https://github.com/org/repo.git");

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Equal(RestoreClassification.EmptyFolder, results[0].Classification);
    }

    [Fact]
    public async Task ScanAsync_NonEmptyNoGit_ClassifiedConflict()
    {
        var expected = Path.Combine(_root, "conflict");
        Directory.CreateDirectory(expected);
        File.WriteAllText(Path.Combine(expected, "stray.txt"), "x");
        var input = LocalProject("p3", expected, "https://github.com/org/repo.git");

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Equal(RestoreClassification.ConflictNonEmpty, results[0].Classification);
    }

    [Fact]
    public async Task ScanAsync_AlreadyClonedMatchingOrigin_ClassifiedAlreadyCloned()
    {
        if (!_gitAvailable) return;

        var existing = MakeCloneWithOrigin("https://github.com/org/repo.git");
        var input = LocalProject("p4", existing,
            // Different surface form (uppercase host, trailing slash, embedded user)
            // — canonicalisation must still match.
            "https:/" + "/Org-Owner@GitHub.com/org/repo.git/");

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Equal(RestoreClassification.AlreadyCloned, results[0].Classification);
    }

    [Fact]
    public async Task ScanAsync_RepoWithDifferentOrigin_ClassifiedUnsafe()
    {
        if (!_gitAvailable) return;

        var existing = MakeCloneWithOrigin("https://github.com/someone-else/other-repo.git");
        var input = LocalProject("p5", existing, "https://github.com/org/repo.git");

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Equal(RestoreClassification.UnsafeExisting, results[0].Classification);
        Assert.Contains("origin", results[0].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_BareRepo_ClassifiedUnsafe()
    {
        if (!_gitAvailable) return;

        var existing = Path.Combine(_root, "bare-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(existing);
        Assert.True(TryRunGit("init --bare -q", existing, out _));

        var input = LocalProject("p6", existing, "https://github.com/org/repo.git");

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Equal(RestoreClassification.UnsafeExisting, results[0].Classification);
    }

    [Fact]
    public async Task ScanAsync_ShallowClone_ClassifiedUnsafe()
    {
        if (!_gitAvailable) return;

        var source = MakeSourceWithCommits("shallow-src", commits: 3);
        var existing = Path.Combine(_root, "shallow-dest-" + Guid.NewGuid().ToString("N"));
        var fileUrl = "file:///" + source.Replace('\\', '/');
        Assert.True(TryRunGit($"clone -q --depth 1 \"{fileUrl}\" \"{existing}\"", _root, out _));

        var input = LocalProject("p7", existing, "https://github.com/org/repo.git");

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Equal(RestoreClassification.UnsafeExisting, results[0].Classification);
        Assert.Contains("shallow", results[0].Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ScanAsync_MissingFolderWithEmptyRemoteUrl_ClassifiedNeedsUrl()
    {
        var expected = Path.Combine(_root, "absent-needs-url");
        var input = LocalProject("p-needs-url", expected, remoteUrl: string.Empty);

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal(RestoreClassification.MissingNeedsUrl, candidate.Classification);
        Assert.Equal(string.Empty, candidate.RemoteUrl);
    }

    [Fact]
    public async Task ScanAsync_EmptyFolderWithNoRemoteUrl_ClassifiedNeedsUrl()
    {
        var expected = Path.Combine(_root, "empty-needs-url");
        Directory.CreateDirectory(expected);
        var input = LocalProject("p-empty-needs", expected, remoteUrl: string.Empty);

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Equal(RestoreClassification.MissingNeedsUrl, results[0].Classification);
    }

    [Fact]
    public async Task ScanAsync_ConflictFolderWithNoRemoteUrl_ClassifiedNeedsUrl()
    {
        var expected = Path.Combine(_root, "conflict-needs-url");
        Directory.CreateDirectory(expected);
        File.WriteAllText(Path.Combine(expected, "stray.txt"), "x");
        var input = LocalProject("p-conflict-needs", expected, remoteUrl: string.Empty);

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Equal(RestoreClassification.MissingNeedsUrl, results[0].Classification);
    }

    [Fact]
    public async Task ScanAsync_WorkingTreeWithNoInputUrl_SurfacesLiveOriginAndAlreadyCloned()
    {
        if (!_gitAvailable) return;

        var existing = MakeCloneWithOrigin("https://github.com/org/discovered-repo.git");
        var input = LocalProject("p-discover", existing, remoteUrl: string.Empty);

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal(RestoreClassification.AlreadyCloned, candidate.Classification);
        // Scanner should surface the live origin so the VM can persist it.
        Assert.Equal("https://github.com/org/discovered-repo.git", candidate.RemoteUrl);
    }

    [Fact]
    public async Task ScanAsync_ProjectWithEmptyRemoteUrl_AppearsAsNeedsUrlNotFiltered()
    {
        var expected = Path.Combine(_root, "no-remote");
        Directory.CreateDirectory(expected);
        var input = LocalProject("no-remote", expected, remoteUrl: string.Empty);

        var results = await NewScanner().ScanAsync(new[] { input }, CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(RestoreClassification.MissingNeedsUrl, results[0].Classification);
    }

    [Fact]
    public async Task ScanAsync_SshStoreProjects_NotInResult()
    {
        var sshInput = new ProjectRestoreInput(
            ProjectId: "ssh-proj",
            ProjectName: "ssh-proj",
            Slug: "ssh-proj",
            ExpectedPath: "devbox:/srv/repos/ssh-proj",
            RemoteUrl: "https://github.com/org/repo.git",
            IsLocalStore: false);

        var localInput = LocalProject("local-proj", Path.Combine(_root, "missing-local"),
            "https://github.com/org/repo.git");

        var results = await NewScanner().ScanAsync(new[] { sshInput, localInput }, CancellationToken.None);

        var candidate = Assert.Single(results);
        Assert.Equal("local-proj", candidate.ProjectId);
    }

    private ProjectRestoreInput LocalProject(string id, string expectedPath, string remoteUrl)
    {
        return new ProjectRestoreInput(
            ProjectId: id,
            ProjectName: id,
            Slug: id,
            ExpectedPath: expectedPath,
            RemoteUrl: remoteUrl,
            IsLocalStore: true);
    }

    private string MakeCloneWithOrigin(string originUrl)
    {
        // Create an isolated working repo whose only remote is the
        // string supplied. We don't need to actually fetch from it.
        var dir = Path.Combine(_root, "clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.True(TryRunGit("init -q -b main", dir, out _));
        Assert.True(TryRunGit($"remote add origin \"{originUrl}\"", dir, out _));
        Assert.True(TryRunGit("config user.email t@e", dir, out _));
        Assert.True(TryRunGit("config user.name T", dir, out _));
        File.WriteAllText(Path.Combine(dir, "README.md"), "seed\n");
        Assert.True(TryRunGit("add README.md", dir, out _));
        Assert.True(TryRunGit("commit -q -m seed", dir, out _));
        return dir;
    }

    private string MakeSourceWithCommits(string name, int commits)
    {
        var dir = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Assert.True(TryRunGit("init -q -b main", dir, out _));
        Assert.True(TryRunGit("config user.email t@e", dir, out _));
        Assert.True(TryRunGit("config user.name T", dir, out _));

        for (int i = 0; i < Math.Max(1, commits); i++)
        {
            File.WriteAllText(Path.Combine(dir, "README.md"), $"line {i}\n");
            Assert.True(TryRunGit("add README.md", dir, out _));
            Assert.True(TryRunGit($"commit -q -m c{i}", dir, out _));
        }
        return dir;
    }

    private static bool TryRunGit(string args, string workingDir, out string stdout)
    {
        stdout = string.Empty;
        var psi = new ProcessStartInfo("git", args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        try
        {
            using var proc = Process.Start(psi);
            if (proc == null) return false;
            stdout = proc.StandardOutput.ReadToEnd();
            proc.StandardError.ReadToEnd();
            proc.WaitForExit(30_000);
            return proc.HasExited && proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }
}
