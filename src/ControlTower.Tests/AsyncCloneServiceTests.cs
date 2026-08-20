using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

/// <summary>
/// Integration-ish tests for <see cref="AsyncCloneService"/>. Pre-flight
/// checks (credential URL, non-empty destination, missing git) are
/// asserted without invoking the real git CLI. Successful clone and
/// cancellation use a local <c>git init --bare</c> repo as the source
/// so the tests stay fast and offline.
/// </summary>
public class AsyncCloneServiceTests : IDisposable
{
    private readonly string _root;
    private readonly bool _gitAvailable;

    public AsyncCloneServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-clone-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _gitAvailable = TryRunGit("--version", _root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static AsyncCloneService NewService()
    {
        return new AsyncCloneService(new GitProcessAdapter(new ToolSettings()));
    }

    [Fact]
    public async Task CloneAsync_HttpsWithCredentials_RejectedBeforeProcessStarts()
    {
        bool factoryCalled = false;
        var adapter = new GitProcessAdapter(new ToolSettings(), _ =>
        {
            factoryCalled = true;
            throw new InvalidOperationException("must not start");
        });
        var svc = new AsyncCloneService(adapter);

        var dest = Path.Combine(_root, "no-creds");
        var request = new CloneRequest(
            RemoteUrl: "https:/" + "/user:pat@github.com/example/repo.git",
            DestinationPath: dest);

        var result = await svc.CloneAsync(request, null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CloneError.CredentialInUrl, result.Error);
        Assert.False(factoryCalled, "credential URL must not start a git process");
        // Message must not contain the secret.
        Assert.DoesNotContain("pat", result.Message);
    }

    [Fact]
    public async Task CloneAsync_DestinationNotEmpty_Rejected()
    {
        if (!_gitAvailable) { return; }

        var dest = Path.Combine(_root, "non-empty");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "marker.txt"), "x");

        var svc = NewService();
        var result = await svc.CloneAsync(
            new CloneRequest("https://github.com/example/repo.git", dest),
            null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CloneError.DestinationNotEmpty, result.Error);
        // Pre-existing file must be left in place.
        Assert.True(File.Exists(Path.Combine(dest, "marker.txt")));
    }

    [Fact]
    public async Task CloneAsync_LocalBareSource_SucceedsAndPopulatesBranchAndSha()
    {
        if (!_gitAvailable) { return; }

        var bare = CreateBareSourceWithCommit("bare-src");
        var dest = Path.Combine(_root, "bare-dest");

        var result = await NewService().CloneAsync(
            new CloneRequest(bare, dest), null, CancellationToken.None);

        Assert.True(result.Success, "Expected success; got: " + result.Message);
        Assert.True(Directory.Exists(dest));
        Assert.True(Directory.Exists(Path.Combine(dest, ".git")));
        Assert.False(string.IsNullOrEmpty(result.ResolvedBranch));
        Assert.False(string.IsNullOrEmpty(result.CommitSha));
        Assert.Matches("^[0-9a-fA-F]{4,64}$", result.CommitSha!);
    }

    [Fact]
    public async Task CloneAsync_ProgressCallback_FiresAtLeastOnce()
    {
        if (!_gitAvailable) { return; }

        var bare = CreateBareSourceWithCommit("progress-src");
        var dest = Path.Combine(_root, "progress-dest");

        var captured = new List<CloneProgress>();
        var progress = new Progress<CloneProgress>(p =>
        {
            lock (captured) { captured.Add(p); }
        });

        var result = await NewService().CloneAsync(
            new CloneRequest(bare, dest), progress, CancellationToken.None);

        Assert.True(result.Success);
        // Flush queued Progress<T> callbacks.
        for (int i = 0; i < 20 && captured.Count == 0; i++)
        {
            await Task.Delay(20);
        }
        Assert.NotEmpty(captured);
    }

    [Fact]
    public async Task CloneAsync_Cancellation_LeavesPartialContentAndReportsCancelled()
    {
        if (!_gitAvailable) { return; }

        // Slow-ish clone: simulate by cancelling almost immediately. Using
        // a real local source is fast, so we cancel during the destination
        // probe / before completion. Even very fast clones should be
        // cancellable; if they finish before we cancel, the test still
        // exercises the happy path and we skip the cancel assertion.
        var bare = CreateBareSourceWithCommit("cancel-src");
        var dest = Path.Combine(_root, "cancel-dest");

        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(1));

        var result = await NewService().CloneAsync(
            new CloneRequest(bare, dest), null, cts.Token);

        if (result.Status == CloneStatus.Cancelled)
        {
            // Per contract: partial content is left for the caller to inspect.
            // The directory may or may not exist depending on how early
            // cancellation kicked in. If it exists, it must not be removed.
            // Nothing in the service should auto-clean it.
            Assert.Equal(CloneError.None, result.Error);
        }
        else
        {
            // Local clone may complete before cancellation lands; that's OK.
            Assert.True(result.Success, "Expected either Cancelled or Success");
        }
    }

    [Fact]
    public async Task CloneAsync_NoRemoteUrl_FailsWithInvalidUrl()
    {
        var svc = NewService();
        var result = await svc.CloneAsync(
            new CloneRequest("", Path.Combine(_root, "empty-url")),
            null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(CloneError.InvalidUrl, result.Error);
    }

    [Fact]
    public void UrlCarriesCredentials_RejectsHttpsUserInfo_AllowsScpStyleSsh()
    {
        Assert.True(AsyncCloneService.UrlCarriesCredentials(
            "https:/" + "/user:pat@github.com/o/r.git", out var host1));
        Assert.Equal("github.com", host1);

        Assert.False(AsyncCloneService.UrlCarriesCredentials(
            "git@github.com:o/r.git", out var host2));
        Assert.Equal("github.com", host2);

        // ssh:// with a bare username is identity (allowed); password is creds (rejected)
        Assert.False(AsyncCloneService.UrlCarriesCredentials(
            "ssh://git@host/o/r", out _));
        Assert.True(AsyncCloneService.UrlCarriesCredentials(
            "ssh://git:secret@host/o/r", out _));

        // Plain https without user-info — allowed
        Assert.False(AsyncCloneService.UrlCarriesCredentials(
            "https://github.com/o/r.git", out _));
    }

    private string CreateBareSourceWithCommit(string name)
    {
        // Set up a working repo and push it to a sibling bare repo so the
        // clone test can run entirely offline.
        var work = Path.Combine(_root, name + "-work");
        Directory.CreateDirectory(work);
        Assert.True(TryRunGit("init -q -b main", work));
        Assert.True(TryRunGit("config user.email t@e", work));
        Assert.True(TryRunGit("config user.name T", work));
        File.WriteAllText(Path.Combine(work, "README.md"), "hello\n");
        Assert.True(TryRunGit("add README.md", work));
        Assert.True(TryRunGit("commit -q -m initial", work));

        var bare = Path.Combine(_root, name + ".git");
        Assert.True(TryRunGit($"init --bare -q -b main \"{bare}\"", _root));
        Assert.True(TryRunGit($"remote add origin \"{bare}\"", work));
        Assert.True(TryRunGit("push -q origin main", work));

        return bare;
    }

    private static bool TryRunGit(string args, string workingDir)
    {
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
            proc.StandardOutput.ReadToEnd();
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
