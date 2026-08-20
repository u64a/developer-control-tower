using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

/// <summary>
/// Tests for <see cref="RelocateProjectService"/>. Each Preflight test
/// drives the service with focused fakes that produce one specific
/// classification (dirty, ahead-of-origin, etc.) and asserts the
/// expected Issue surfaces. A happy-path Relocate test exercises the
/// full state machine end-to-end through a fake clone service.
/// </summary>
public class RelocateProjectServiceTests : IDisposable
{
    private const string HeadSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string OtherSha = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private readonly string _root;

    public RelocateProjectServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ctrelocate-svc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    // ---- Preflight rejects ----

    [Fact]
    public async Task PreflightAsync_FolderNameInvalid_RejectsWithMessage()
    {
        var sut = MakeService(out _, out _, out _, out _);

        var req = NewRequest("source", "store-b", "bad name with spaces");
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.Contains("Folder name"));
    }

    [Fact]
    public async Task PreflightAsync_TargetStoreMissing_RejectsWithMessage()
    {
        var sut = MakeService(out _, out _, out _, out _);

        var req = NewRequest("source", targetStoreId: "missing-store");
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.Contains("Target store"));
    }

    [Fact]
    public async Task PreflightAsync_NoOrigin_RejectsWithMessage()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src1");
        inspector.NextClassification = MakeWorking(src, originUrl: null);
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("source", "store-b", "relocated");
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.Contains("no origin URL"));
    }

    [Fact]
    public async Task PreflightAsync_OriginEmbedsCredentials_Rejects()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src2");
        inspector.NextClassification = MakeWorking(src, originUrl: "https:/" + "/u:tok@github.com/x/y.git");
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("source", "store-b", "relocated");
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.IndexOf("credentials", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [Fact]
    public async Task PreflightAsync_DirtyTree_Rejects()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src3");
        inspector.NextClassification = MakeWorking(src);
        inspector.NextStatus = new GitStatusBuckets(
            Modified: new[] { "a.cs" }, Staged: Array.Empty<string>(),
            UntrackedNotIgnored: Array.Empty<string>(), IgnoredFiles: Array.Empty<string>(),
            AheadOfOrigin: 0, BehindOrigin: 0);

        var req = NewRequest("source", "store-b", "relocated");
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.Contains("uncommitted"));
    }

    [Fact]
    public async Task PreflightAsync_AheadOfOriginCleanTree_FlagsNeedsPush()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src4");
        inspector.NextClassification = MakeWorking(src);
        inspector.NextStatus = new GitStatusBuckets(
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
            AheadOfOrigin: 2, BehindOrigin: 0);

        var req = NewRequest("source", "store-b", "relocated");
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.True(result.NeedsPush);
        Assert.Contains(result.Issues, m => m.Contains("ahead of origin"));
    }

    [Fact]
    public async Task PreflightAsync_SubmodulesSparseDetached_AllSurfaceTogether()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src5");
        inspector.NextClassification = new WorkingTreeRepo(
            Path: src, GitDir: ".git", Branch: "HEAD",
            IsDetached: true, IsShallow: true, IsSparse: true, IsPartialClone: true,
            HasWorktrees: true, HasSubmodules: true,
            OriginUrl: "https://github.com/x/y.git",
            Remotes: Array.Empty<GitRemote>());
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("source", "store-b", "relocated");
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.Contains("submodules"));
        Assert.Contains(result.Issues, m => m.Contains("worktrees"));
        Assert.Contains(result.Issues, m => m.Contains("shallow"));
        Assert.Contains(result.Issues, m => m.Contains("sparse"));
        Assert.Contains(result.Issues, m => m.Contains("partial"));
        Assert.Contains(result.Issues, m => m.Contains("detached"));
    }

    [Fact]
    public async Task PreflightAsync_BareRepoSource_Rejects()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src6");
        inspector.NextClassification = new BareRepo(src, ".git", Array.Empty<GitRemote>());
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("source", "store-b", "relocated");
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.Contains("bare"));
    }

    [Fact]
    public async Task PreflightAsync_NotARepoSource_Rejects()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src7");
        inspector.NextClassification = new NotARepo(src);
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("source", "store-b", "relocated");
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.IndexOf("not a git", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    [Fact]
    public async Task PreflightAsync_SshToSsh_RefusedEarly()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "ssh-a", Type = "ssh", Host = "host1", User = "u", Root = "/srv/a" },
            new RepoStore { Id = "ssh-b", Type = "ssh", Host = "host2", User = "u", Root = "/srv/b" });
        var sut = MakeServiceWithStores(stores, out _, out _, out _, out _);

        var req = NewRequest("source", "ssh-b", "relocated");
        req.SourceSshTarget = "u@host1:/srv/a/source";
        req.SourceStoreId = "ssh-a";
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.IndexOf("SSH→SSH", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public async Task PreflightAsync_IdenticalSourceAndTarget_Rejects()
    {
        var src = MakeSourceFolder("identical");
        // Make a store rooted exactly where the project lives so target == source.
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-x", Type = "local", Root = Path.GetDirectoryName(src) ?? string.Empty });
        var sut = MakeServiceWithStores(stores, out var inspector, out _, out _, out _);
        inspector.NextClassification = MakeWorking(src);
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("source", "store-x", Path.GetFileName(src));
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, m => m.Contains("identical"));
    }

    [Fact]
    public async Task PreflightAsync_HappyPathLocalToLocal_OkAndCarriesOriginAndBranch()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src-ok");
        inspector.NextClassification = MakeWorking(src, originUrl: "https://github.com/x/y.git", branch: "main");
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("happy", "store-b", "relocated");
        req.SourceLocalPath = src;
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.True(result.OkToRelocate, "preflight should pass; issues: " + string.Join(",", result.Issues));
        Assert.Equal("https://github.com/x/y.git", result.OriginUrl);
        Assert.Equal("main", result.SourceBranch);
        Assert.Equal(HeadSha, result.SourceHeadSha);
        Assert.False(result.SourceIsSsh);
        Assert.False(result.TargetIsSsh);
    }

    [Fact]
    public async Task PreflightAsync_UnknownUpstreamCounts_Blocks()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("unknown-upstream");
        inspector.NextClassification = MakeWorking(src);
        inspector.NextStatus = new GitStatusBuckets(
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            null,
            null);

        var req = NewRequest("unknown-upstream");
        req.SourceLocalPath = src;

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, issue => issue.Contains("ahead/behind"));
    }

    [Fact]
    public async Task PreflightAsync_LocalSourceHeadCaptureFailure_Blocks()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("head-failure");
        inspector.NextClassification = MakeWorking(src);
        inspector.NextRelocationState = RelocationGitState.Failure(
            "Source HEAD SHA could not be read as a full Git object ID.");

        var req = NewRequest("head-failure");
        req.SourceLocalPath = src;

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, issue => issue.Contains("HEAD SHA"));
    }

    [Fact]
    public async Task PreflightAsync_CurrentOriginHeadMismatch_Blocks()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("origin-head-mismatch");
        inspector.NextClassification = MakeWorking(src);
        inspector.NextRelocationState = SuccessfulState(
            "main",
            HeadSha,
            originSha: OtherSha);

        var req = NewRequest("origin-head-mismatch");
        req.SourceLocalPath = src;

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, issue => issue.Contains("current origin/main"));
    }

    [Fact]
    public async Task PreflightAsync_SshSourceHeadCaptureFailure_Blocks()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "ssh-a", Type = "ssh", Host = "source", User = "u", Root = "/srv/source" },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        var inspector = new FakeInspector();
        var sshInspector = new FakeSshInspector
        {
            NextClassification = MakeWorking("/srv/source/project"),
            NextRelocationState = RelocationGitState.Failure(
                "Source HEAD SHA could not be read as a full Git object ID.",
                remoteIsWindows: false)
        };
        var registration = new FakeRegistration();
        var sut = CreateService(
            stores,
            inspector,
            new FakeCloneService(),
            registration,
            new LongRunningGitOperationLock(),
            sshInspector,
            new RecordingSshService());
        var req = NewRequest("ssh-head-failure");
        req.SourceStoreId = "ssh-a";
        req.SourceSshTarget = "u@source:/srv/source/project";

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, issue => issue.Contains("HEAD SHA"));
    }

    [Fact]
    public async Task PreflightAsync_SourceStoreDoesNotMatchSshTarget_Blocks()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "ssh-a", Type = "ssh", Host = "source-a", User = "u", Root = "/srv/source" },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        var sut = MakeServiceWithStores(stores, out _, out _, out _, out _);
        var req = NewRequest("ssh-store-mismatch");
        req.SourceStoreId = "ssh-a";
        req.SourceSshTarget = "u@source-b:/srv/source/project";

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(
            result.Issues,
            issue => issue.Contains("credentials match the source target"));
    }

    [Fact]
    public async Task PreflightAsync_DeleteLocalDriveRoot_Blocks()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var sourceRoot = Path.GetPathRoot(_root)!;
        inspector.NextClassification = MakeWorking(sourceRoot);
        inspector.NextRelocationState = SuccessfulState("main", HeadSha);
        var req = NewRequest("local-root-delete");
        req.SourceLocalPath = sourceRoot;
        req.DeleteSourceAfterSuccess = true;

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, issue => issue.Contains("Source deletion is not allowed"));
    }

    [Fact]
    public async Task PreflightAsync_DeleteSshRoot_Blocks()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "ssh-a", Type = "ssh", Host = "source", User = "u", Root = "/" },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        var sshInspector = new FakeSshInspector
        {
            NextClassification = MakeWorking("/"),
            NextRelocationState = SuccessfulState(
                "main",
                HeadSha,
                remoteIsWindows: false)
        };
        var sut = CreateService(
            stores,
            new FakeInspector(),
            new FakeCloneService(),
            new FakeRegistration(),
            new LongRunningGitOperationLock(),
            sshInspector,
            new RecordingSshService());
        var req = NewRequest("ssh-root-delete");
        req.SourceStoreId = "ssh-a";
        req.SourceSshTarget = "u@source:/";
        req.DeleteSourceAfterSuccess = true;

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.OkToRelocate);
        Assert.Contains(result.Issues, issue => issue.Contains("Source deletion is not allowed"));
    }

    [Fact]
    public async Task PreflightAsync_PosixTarget_QuotesOriginAndJoinsWithSlash()
    {
        const string origin = "https://example.test/org/repo$(touch-owned).git";
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "ssh-b", Type = "ssh", Host = "target", User = "u", Root = "/srv/repos" });
        var inspector = new FakeInspector
        {
            NextClassification = MakeWorking("unused", originUrl: origin)
        };
        var ssh = new RecordingSshService();
        var sut = CreateService(
            stores,
            inspector,
            new FakeCloneService(),
            new FakeRegistration(),
            new LongRunningGitOperationLock(),
            new FakeSshInspector(),
            ssh);
        var src = MakeSourceFolder("posix-target");
        var req = NewRequest("posix-target", "ssh-b", "relocated");
        req.SourceLocalPath = src;

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.True(result.OkToRelocate, string.Join("; ", result.Issues));
        Assert.Equal("u@target:/srv/repos/relocated", result.ResolvedTargetPath);
        var probe = Assert.Single(
            ssh.Commands,
            command => command.StartsWith("git ls-remote --heads ", StringComparison.Ordinal));
        Assert.Contains("'" + origin + "'", probe);
        Assert.DoesNotContain("\"" + origin + "\"", probe);
    }

    // Regression: OverviewComposer emits "Not configured" as the display
    // sentinel for projects without an SSH target. If that string leaks into
    // SourceSshTarget, the service must treat the source as local — not as
    // a malformed SSH target and not as an SSH source eligible for SSH→SSH
    // refusal when the target is also SSH.
    [Fact]
    public async Task PreflightAsync_SshTargetIsDisplaySentinel_TreatedAsLocalSource()
    {
        var sut = MakeService(out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src-sentinel");
        inspector.NextClassification = MakeWorking(src, originUrl: "https://github.com/x/y.git", branch: "main");
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("sentinel", "store-b", "relocated");
        req.SourceLocalPath = src;
        req.SourceSshTarget = "Not configured"; // OverviewComposer sentinel
        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.True(result.OkToRelocate, "preflight should pass; issues: " + string.Join(",", result.Issues));
        Assert.False(result.SourceIsSsh);
        Assert.DoesNotContain(result.Issues, m => m.Contains("user@host:path"));
        Assert.DoesNotContain(result.Issues, m => m.IndexOf("SSH→SSH", StringComparison.Ordinal) >= 0);
    }

    // Regression: same sentinel on the SSH→SSH refusal path — if SshTarget
    // is "Not configured" and the target store is SSH, this must NOT be
    // refused as SSH→SSH.
    [Fact]
    public async Task PreflightAsync_SshTargetIsDisplaySentinel_AndTargetIsSsh_DoesNotRefuseSshToSsh()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "ssh-b", Type = "ssh", Host = "host2", User = "u", Root = "/srv/b" });
        var sut = MakeServiceWithStores(stores, out var inspector, out _, out _, out _);
        var src = MakeSourceFolder("src-sentinel-sshtgt");
        inspector.NextClassification = MakeWorking(src, originUrl: "https://github.com/x/y.git", branch: "main");
        inspector.NextStatus = CleanStatus();

        var req = NewRequest("sentinel2", "ssh-b", "relocated");
        req.SourceLocalPath = src;
        req.SourceSshTarget = "Not configured"; // OverviewComposer sentinel

        var result = await sut.PreflightAsync(req, CancellationToken.None);

        Assert.False(result.SourceIsSsh, "sentinel must not be interpreted as a real SSH target");
        Assert.DoesNotContain(result.Issues, m => m.IndexOf("SSH→SSH", StringComparison.Ordinal) >= 0);
    }

    [Fact]
    public async Task RelocateAsync_HappyPathLocalToLocal_RunsAllStepsAndRebinds()
    {
        var sut = MakeService(out var inspector, out var clone, out var registration, out var lockImpl);
        var src = MakeSourceFolder("src-hp");
        // Metadata is centralized now, so nothing is copied between repos during
        // relocate; the source .controltower is irrelevant to the step outcome.

        inspector.NextClassification = MakeWorking(src, originUrl: "https://github.com/x/y.git", branch: "main");
        inspector.NextStatus = CleanStatus();
        // The fake clone service writes a .git folder into the destination so
        // the destination Classify step finds a working tree. Hook it via the
        // OnCloneCallback below.
        clone.OnInvoke = (req, ct) =>
        {
            Directory.CreateDirectory(req.DestinationPath);
            Directory.CreateDirectory(Path.Combine(req.DestinationPath, ".git"));
            return CloneResult.Ok("main", "abcdef1", "Cloned.");
        };

        var req = NewRequest("happy", "store-b", "relocated");
        req.SourceLocalPath = src;

        var updates = new List<RelocateStepUpdate>();
        var progress = new SyncProgress<RelocateStepUpdate>(updates.Add);
        var result = await sut.RelocateAsync(req, progress, CancellationToken.None);

        Assert.True(result.Success, "Relocate should succeed; failed at " + result.FailedStep + ": " + result.ErrorMessage);
        Assert.False(lockImpl.IsHeld, "lock must be released after RelocateAsync completes");
        Assert.Contains(updates, u => u.Step == RelocateStep.CloneOrigin && u.State == RelocateStepState.Done);
        Assert.Contains(updates, u => u.Step == RelocateStep.MigrateMetadata && u.State == RelocateStepState.Done);
        Assert.Contains(updates, u => u.Step == RelocateStep.RebindPortfolio && u.State == RelocateStepState.Done);
        Assert.True(registration.RegisterCalled, "RegisterProject must be invoked at the Rebind step");
        Assert.True(registration.LastRequest.AllowOverwrite);
        Assert.Equal(result.FinalTargetPath, registration.LastRequest.LocalPath);
        Assert.Equal("main", clone.LastRequest?.Branch);
        Assert.True(clone.LastRequest?.SingleBranch);
    }

    [Fact]
    public async Task RelocateAsync_SshTargetClone_SelectsSourceBranch()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "ssh-b", Type = "ssh", Host = "target", User = "u", Root = "/srv/repos" });
        var inspector = new FakeInspector();
        var src = MakeSourceFolder("ssh-clone-branch");
        inspector.NextClassification = MakeWorking(src, branch: "release");
        inspector.RelocationStateFactory = _ => SuccessfulState("release", HeadSha);
        var ssh = new RecordingSshService { Branch = "release" };
        var sshInspector = new FakeSshInspector
        {
            NextClassification = MakeWorking("/srv/repos/relocated", branch: "release"),
            NextRelocationState = SuccessfulState("release", HeadSha, remoteIsWindows: false)
        };
        var sut = CreateService(
            stores,
            inspector,
            new FakeCloneService(),
            new FakeRegistration(),
            new LongRunningGitOperationLock(),
            sshInspector,
            ssh);
        var req = NewRequest("ssh-clone-branch", "ssh-b", "relocated");
        req.SourceLocalPath = src;

        var result = await sut.RelocateAsync(req, progress: null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        var cloneCommand = Assert.Single(
            ssh.Commands,
            command => command.StartsWith("git clone ", StringComparison.Ordinal));
        Assert.Contains("--branch 'release' --single-branch", cloneCommand);
        Assert.Contains("'/srv/repos/relocated'", cloneCommand);
    }

    [Fact]
    public async Task RelocateAsync_OverFiveHundredIgnoredFiles_DoesNotAuthorizeDeletion()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        var inspector = new FakeInspector();
        var clone = new FakeCloneService();
        var registration = new FakeRegistration();
        var transfer = new InMemoryFileTransfer();
        var src = MakeSourceFolder("ignored-overflow");
        var destination = Path.Combine(_root, "store-b", "relocated");
        var ignored = Enumerable.Range(0, 501)
            .Select(index => $"ignored/{index:D3}.tmp")
            .ToArray();
        var sourceStatus = new GitStatusBuckets(
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            ignored,
            0,
            0);
        inspector.NextClassification = MakeWorking(src);
        inspector.RelocationStateFactory = path =>
            string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)
                ? SuccessfulState("main", HeadSha)
                : SuccessfulState("main", HeadSha, status: sourceStatus);
        clone.OnInvoke = (request, _) =>
        {
            Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"));
            return CloneResult.Ok("main", HeadSha, "Cloned.");
        };
        var deleteCalled = false;
        var sut = CreateService(
            stores,
            inspector,
            clone,
            registration,
            new LongRunningGitOperationLock(),
            new FakeSshInspector(),
            new RecordingSshService(),
            (_, _) =>
            {
                deleteCalled = true;
                return true;
            },
            transfer);
        var req = NewRequest("ignored-overflow");
        req.SourceLocalPath = src;
        req.CopyIgnoredFiles = true;
        req.DeleteSourceAfterSuccess = true;
        var updates = new List<RelocateStepUpdate>();

        var result = await sut.RelocateAsync(
            req,
            new SyncProgress<RelocateStepUpdate>(updates.Add),
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(0, transfer.CopyFilesCallCount);
        Assert.False(deleteCalled);
        Assert.Contains(
            updates,
            update => update.Step == RelocateStep.CopyIgnoredFiles
                && update.State == RelocateStepState.Warning);
        Assert.Contains(
            updates,
            update => update.Step == RelocateStep.DeleteSource
                && update.State == RelocateStepState.Skipped);
    }

    [Fact]
    public async Task RelocateAsync_UnprovenIgnoredTransferTotal_DoesNotAuthorizeDeletion()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        var inspector = new FakeInspector();
        var clone = new FakeCloneService();
        var registration = new FakeRegistration();
        var transfer = new InMemoryFileTransfer
        {
            CopyFilesResult = new RelocateTransferResult
            {
                Success = true,
                FilesCopied = 1,
                FilesSkipped = 0
            }
        };
        var src = MakeSourceFolder("ignored-accounting");
        var destination = Path.Combine(_root, "store-b", "relocated");
        var sourceStatus = new GitStatusBuckets(
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { "one.tmp", "two.tmp" },
            0,
            0);
        inspector.NextClassification = MakeWorking(src);
        inspector.RelocationStateFactory = path =>
            string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)
                ? SuccessfulState("main", HeadSha)
                : SuccessfulState("main", HeadSha, status: sourceStatus);
        clone.OnInvoke = (request, _) =>
        {
            Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"));
            return CloneResult.Ok("main", HeadSha, "Cloned.");
        };
        var deleteCalled = false;
        var sut = CreateService(
            stores,
            inspector,
            clone,
            registration,
            new LongRunningGitOperationLock(),
            new FakeSshInspector(),
            new RecordingSshService(),
            (_, _) =>
            {
                deleteCalled = true;
                return true;
            },
            transfer);
        var req = NewRequest("ignored-accounting");
        req.SourceLocalPath = src;
        req.CopyIgnoredFiles = true;
        req.DeleteSourceAfterSuccess = true;
        var updates = new List<RelocateStepUpdate>();

        var result = await sut.RelocateAsync(
            req,
            new SyncProgress<RelocateStepUpdate>(updates.Add),
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, transfer.CopyFilesCallCount);
        Assert.False(deleteCalled);
        Assert.Contains(
            updates,
            update => update.Step == RelocateStep.CopyIgnoredFiles
                && update.State == RelocateStepState.Warning
                && update.Detail.Contains("1 of 2", StringComparison.Ordinal));
        Assert.Contains(
            updates,
            update => update.Step == RelocateStep.DeleteSource
                && update.State == RelocateStepState.Skipped);
    }

    [Fact]
    public async Task RelocateAsync_ProvenIgnoredTransfer_AllowsDeletion()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        var inspector = new FakeInspector();
        var clone = new FakeCloneService();
        var registration = new FakeRegistration();
        var transfer = new InMemoryFileTransfer();
        var src = MakeSourceFolder("ignored-proven");
        var destination = Path.Combine(_root, "store-b", "relocated");
        var sourceStatus = new GitStatusBuckets(
            Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            new[] { "one.tmp", "two.tmp" },
            0,
            0);
        inspector.NextClassification = MakeWorking(src);
        inspector.RelocationStateFactory = path =>
            string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)
                ? SuccessfulState("main", HeadSha)
                : SuccessfulState("main", HeadSha, status: sourceStatus);
        clone.OnInvoke = (request, _) =>
        {
            Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"));
            return CloneResult.Ok("main", HeadSha, "Cloned.");
        };
        var deleteCalled = false;
        var sut = CreateService(
            stores,
            inspector,
            clone,
            registration,
            new LongRunningGitOperationLock(),
            new FakeSshInspector(),
            new RecordingSshService(),
            (_, _) =>
            {
                deleteCalled = true;
                return true;
            },
            transfer);
        var req = NewRequest("ignored-proven");
        req.SourceLocalPath = src;
        req.CopyIgnoredFiles = true;
        req.DeleteSourceAfterSuccess = true;
        var updates = new List<RelocateStepUpdate>();

        var result = await sut.RelocateAsync(
            req,
            new SyncProgress<RelocateStepUpdate>(updates.Add),
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(1, transfer.CopyFilesCallCount);
        Assert.True(deleteCalled);
        Assert.Contains(
            updates,
            update => update.Step == RelocateStep.CopyIgnoredFiles
                && update.State == RelocateStepState.Done);
        Assert.Contains(
            updates,
            update => update.Step == RelocateStep.DeleteSource
                && update.State == RelocateStepState.Done);
    }

    [Fact]
    public async Task RelocateAsync_IncompleteIgnoredInventory_DoesNotAuthorizeDeletion()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        var inspector = new FakeInspector();
        var clone = new FakeCloneService();
        var registration = new FakeRegistration();
        var src = MakeSourceFolder("ignored-incomplete");
        var destination = Path.Combine(_root, "store-b", "relocated");
        inspector.NextClassification = MakeWorking(src);
        inspector.RelocationStateFactory = path =>
            string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)
                ? SuccessfulState("main", HeadSha)
                : SuccessfulState(
                    "main",
                    HeadSha,
                    ignoredFilesInventoryComplete: false);
        clone.OnInvoke = (request, _) =>
        {
            Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"));
            return CloneResult.Ok("main", HeadSha, "Cloned.");
        };
        var deleteCalled = false;
        var sut = CreateService(
            stores,
            inspector,
            clone,
            registration,
            new LongRunningGitOperationLock(),
            new FakeSshInspector(),
            new RecordingSshService(),
            (_, _) =>
            {
                deleteCalled = true;
                return true;
            });
        var req = NewRequest("ignored-incomplete");
        req.SourceLocalPath = src;
        req.DeleteSourceAfterSuccess = true;
        var updates = new List<RelocateStepUpdate>();

        var result = await sut.RelocateAsync(
            req,
            new SyncProgress<RelocateStepUpdate>(updates.Add),
            CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.False(deleteCalled);
        Assert.Contains(
            updates,
            update => update.Step == RelocateStep.DeleteSource
                && update.State == RelocateStepState.Warning
                && update.Detail.Contains("inventory is incomplete", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("other", HeadSha)]
    [InlineData("main", OtherSha)]
    public async Task RelocateAsync_DestinationBranchOrShaMismatch_DoesNotRebindOrDelete(
        string destinationBranch,
        string destinationSha)
    {
        var sut = MakeService(out var inspector, out var clone, out var registration, out _);
        var src = MakeSourceFolder("mismatch-" + destinationBranch + destinationSha[0]);
        var destination = Path.Combine(_root, "store-b", "relocated");
        inspector.NextClassification = MakeWorking(src);
        inspector.RelocationStateFactory = path =>
            string.Equals(path, destination, StringComparison.OrdinalIgnoreCase)
                ? SuccessfulState(destinationBranch, destinationSha)
                : SuccessfulState("main", HeadSha);
        clone.OnInvoke = (request, _) =>
        {
            Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"));
            return CloneResult.Ok("main", HeadSha, "Cloned.");
        };
        var deleteCalled = false;
        sut = CreateService(
            new StubStoreProvider(
                new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
                new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") }),
            inspector,
            clone,
            registration,
            new LongRunningGitOperationLock(),
            new FakeSshInspector(),
            new RecordingSshService(),
            (_, _) =>
            {
                deleteCalled = true;
                return true;
            });
        var req = NewRequest("mismatch");
        req.SourceLocalPath = src;
        req.DeleteSourceAfterSuccess = true;

        var result = await sut.RelocateAsync(req, progress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(RelocateStep.VerifyDestination, result.FailedStep);
        Assert.False(registration.RegisterCalled);
        Assert.False(deleteCalled);
    }

    [Fact]
    public async Task RelocateAsync_SourceChangesAfterClone_DoesNotRebindOrDelete()
    {
        var sut = MakeService(out var inspector, out var clone, out var registration, out _);
        var src = MakeSourceFolder("source-changed");
        var destination = Path.Combine(_root, "store-b", "relocated");
        var sourceReads = 0;
        inspector.NextClassification = MakeWorking(src);
        inspector.RelocationStateFactory = path =>
        {
            if (string.Equals(path, destination, StringComparison.OrdinalIgnoreCase))
            {
                return SuccessfulState("main", HeadSha);
            }

            sourceReads++;
            return sourceReads == 1
                ? SuccessfulState("main", HeadSha)
                : SuccessfulState("main", OtherSha);
        };
        clone.OnInvoke = (request, _) =>
        {
            Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"));
            return CloneResult.Ok("main", HeadSha, "Cloned.");
        };
        var deleteCalled = false;
        sut = CreateService(
            new StubStoreProvider(
                new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
                new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") }),
            inspector,
            clone,
            registration,
            new LongRunningGitOperationLock(),
            new FakeSshInspector(),
            new RecordingSshService(),
            (_, _) =>
            {
                deleteCalled = true;
                return true;
            });
        var req = NewRequest("source-changed");
        req.SourceLocalPath = src;
        req.DeleteSourceAfterSuccess = true;

        var result = await sut.RelocateAsync(req, progress: null, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(RelocateStep.VerifyDestination, result.FailedStep);
        Assert.Contains("Source changed", result.ErrorMessage);
        Assert.False(registration.RegisterCalled);
        Assert.False(deleteCalled);
    }

    [Fact]
    public async Task RelocateAsync_MatchingDestinationSha_RebindsThenDeletes()
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        var inspector = new FakeInspector();
        var clone = new FakeCloneService();
        var registration = new FakeRegistration();
        var src = MakeSourceFolder("matching-proof");
        inspector.NextClassification = MakeWorking(src);
        inspector.RelocationStateFactory = _ => SuccessfulState("main", HeadSha);
        clone.OnInvoke = (request, _) =>
        {
            Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"));
            return CloneResult.Ok("main", HeadSha, "Cloned.");
        };
        var deleteCalled = false;
        var sut = CreateService(
            stores,
            inspector,
            clone,
            registration,
            new LongRunningGitOperationLock(),
            new FakeSshInspector(),
            new RecordingSshService(),
            (_, _) =>
            {
                deleteCalled = true;
                return true;
            });
        var req = NewRequest("matching-proof");
        req.SourceLocalPath = src;
        req.DeleteSourceAfterSuccess = true;

        var result = await sut.RelocateAsync(req, progress: null, CancellationToken.None);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(registration.RegisterCalled);
        Assert.True(deleteCalled);
    }

    // ---- Helpers ----

    private string MakeSourceFolder(string name)
    {
        var p = Path.Combine(_root, name);
        Directory.CreateDirectory(p);
        return p;
    }

    private RelocateProjectService MakeService(
        out FakeInspector inspector,
        out FakeCloneService clone,
        out FakeRegistration registration,
        out LongRunningGitOperationLock lockImpl)
    {
        var stores = new StubStoreProvider(
            new RepoStore { Id = "store-a", Type = "local", Root = Path.Combine(_root, "store-a") },
            new RepoStore { Id = "store-b", Type = "local", Root = Path.Combine(_root, "store-b") });
        return MakeServiceWithStores(stores, out inspector, out clone, out registration, out lockImpl);
    }

    private RelocateProjectService MakeServiceWithStores(
        StubStoreProvider stores,
        out FakeInspector inspector,
        out FakeCloneService clone,
        out FakeRegistration registration,
        out LongRunningGitOperationLock lockImpl)
    {
        inspector = new FakeInspector();
        clone = new FakeCloneService();
        registration = new FakeRegistration();
        lockImpl = new LongRunningGitOperationLock();
        return CreateService(
            stores,
            inspector,
            clone,
            registration,
            lockImpl,
            new FakeSshInspector(),
            new RecordingSshService());
    }

    private RelocateProjectService CreateService(
        StubStoreProvider stores,
        FakeInspector inspector,
        FakeCloneService clone,
        FakeRegistration registration,
        LongRunningGitOperationLock lockImpl,
        ISshGitInspector sshInspector,
        ISshService sshService,
        Func<string, string, bool>? deleteToRecycleBin = null,
        IRelocateFileTransfer? fileTransfer = null)
    {
        return new RelocateProjectService(
            gitAdapter: new NoopGitAdapter(),
            workspaceInspector: inspector,
            sshGitInspector: sshInspector,
            cloneService: clone,
            sshService: sshService,
            credentialStore: new NoopCredentialStore(),
            fileTransfer: fileTransfer ?? new InMemoryFileTransfer(),
            registrationService: registration,
            storeProvider: stores,
            gitOperationLock: lockImpl,
            deleteToRecycleBin: deleteToRecycleBin);
    }

    private static RelocateRequest NewRequest(string projectId, string targetStoreId = "store-b", string folder = "relocated")
    {
        return new RelocateRequest
        {
            ProjectId = projectId,
            DisplayName = "Sample",
            Summary = "x",
            LifecycleState = "active",
            TargetStoreId = targetStoreId,
            TargetFolder = folder
        };
    }

    private static WorkingTreeRepo MakeWorking(string path, string? originUrl = "https://github.com/x/y.git", string branch = "main")
    {
        return new WorkingTreeRepo(
            Path: path, GitDir: Path.Combine(path, ".git"), Branch: branch,
            IsDetached: false, IsShallow: false, IsSparse: false, IsPartialClone: false,
            HasWorktrees: false, HasSubmodules: false,
            OriginUrl: originUrl,
            Remotes: Array.Empty<GitRemote>());
    }

    private static GitStatusBuckets CleanStatus()
        => new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), 0, 0);

    private static RelocationGitState SuccessfulState(
        string branch,
        string sha,
        bool? remoteIsWindows = null,
        GitStatusBuckets? status = null,
        string? originSha = null,
        bool ignoredFilesInventoryComplete = true)
        => new(
            true,
            string.Empty,
            status ?? CleanStatus(),
            branch,
            sha,
            originSha ?? sha,
            "origin/" + branch,
            remoteIsWindows)
        {
            IgnoredFilesInventoryComplete = ignoredFilesInventoryComplete
        };

    // ---- Fakes ----

    private sealed class StubStoreProvider : IStoreProvider
    {
        private readonly List<RepoStore> _stores;
        public StubStoreProvider(params RepoStore[] stores) { _stores = stores.ToList(); }
        public IReadOnlyList<RepoStore> GetStores() => _stores;
        public RepoStore GetStore(string storeId)
            => _stores.FirstOrDefault(s => string.Equals(s.Id, storeId, StringComparison.OrdinalIgnoreCase))!;
        public string ResolveProjectPath(string storeId, string projectId, string folder)
            => Path.Combine(GetStore(storeId)?.Root ?? string.Empty, folder);
    }

    private sealed class FakeInspector : IGitWorkspaceInspector
    {
        public GitWorkspaceClassification NextClassification { get; set; } = new NotARepo(string.Empty);
        public GitStatusBuckets NextStatus { get; set; } = new(Array.Empty<string>(), Array.Empty<string>(),
            Array.Empty<string>(), Array.Empty<string>(), 0, 0);
        public RelocationGitState? NextRelocationState { get; set; }
        public Func<string, RelocationGitState>? RelocationStateFactory { get; set; }

        public Task<GitWorkspaceClassification> ClassifyAsync(string path, CancellationToken ct)
            => Task.FromResult(NextClassification is NotARepo
                ? (GitWorkspaceClassification)new NotARepo(path)
                : NextClassification);

        public Task<GitStatusBuckets> ReadStatusAsync(string workingTreePath, CancellationToken ct)
            => Task.FromResult(NextStatus);

        public Task<RelocationGitState> ReadRelocationStateAsync(
            string workingTreePath,
            CancellationToken ct)
        {
            if (RelocationStateFactory != null)
            {
                return Task.FromResult(RelocationStateFactory(workingTreePath));
            }
            if (NextRelocationState != null)
            {
                return Task.FromResult(NextRelocationState);
            }
            var branch = NextClassification is WorkingTreeRepo working
                ? working.Branch
                : "main";
            return Task.FromResult(SuccessfulState(branch, HeadSha, status: NextStatus));
        }

        public string CanonicalizeRemote(string remote) => remote ?? string.Empty;
        public string GetRemoteIdentity(string remote) => (remote ?? string.Empty).Trim();
    }

    private sealed class FakeSshInspector : ISshGitInspector
    {
        public GitWorkspaceClassification NextClassification { get; set; } =
            new NotARepo(string.Empty);
        public RelocationGitState NextRelocationState { get; set; } =
            SuccessfulState("main", HeadSha, remoteIsWindows: false);

        public Task<GitWorkspaceClassification> ClassifyAsync(string host, int port, string user, string password, string remotePath, CancellationToken ct)
            => Task.FromResult(NextClassification is NotARepo
                ? (GitWorkspaceClassification)new NotARepo(remotePath)
                : NextClassification);
        public Task<GitStatusBuckets> ReadStatusAsync(string host, int port, string user, string password, string remotePath, CancellationToken ct)
            => Task.FromResult(NextRelocationState.Status);
        public Task<RelocationGitState> ReadRelocationStateAsync(string host, int port, string user, string password, string remotePath, CancellationToken ct)
            => Task.FromResult(NextRelocationState);
    }

    private sealed class RecordingSshService : ISshService
    {
        public string Branch { get; set; } = "main";
        public List<string> Commands { get; } = new();

        public SshResult TestConnection(string host, int port, string user, string password) => SshResult.Ok();
        public SshResult CreateDirectory(string host, int port, string user, string password, string remotePath) => SshResult.Ok();
        public SshResult RunCommand(string host, int port, string user, string password, string command)
        {
            Commands.Add(command);
            if (command == "echo %OS%") return SshResult.Ok("%OS%");
            if (command == "uname -s") return SshResult.Ok("Linux");
            if (command == "git --version") return SshResult.Ok("git version 2.50.0");
            if (command.StartsWith("test -d ", StringComparison.Ordinal)) return SshResult.Ok("NOTFOUND");
            if (command.StartsWith("git ls-remote --heads ", StringComparison.Ordinal))
            {
                return SshResult.Ok(HeadSha + "\trefs/heads/" + Branch);
            }
            return SshResult.Ok();
        }
    }

    private sealed class NoopCredentialStore : ICredentialStore
    {
        public string GetPassword(string target) => string.Empty;
        public void SetPassword(string target, string password) { }
        public void DeletePassword(string target) { }
    }

    private sealed class NoopGitAdapter : IGitProcessAdapter
    {
        public Task<GitRunResult> RunAsync(IEnumerable<string> arguments, string workingDirectory,
            TimeSpan timeout, IProgress<string>? progress, CancellationToken ct)
            => Task.FromResult(new GitRunResult(0, string.Empty, string.Empty, TimeSpan.Zero, false, false));
    }

    private sealed class InMemoryFileTransfer : IRelocateFileTransfer
    {
        public int CopyFilesCallCount { get; private set; }
        public RelocateTransferResult? CopyFilesResult { get; set; }

        public Task<RelocateTransferResult> CopyDirectoryAsync(
            RelocateEndpoint source, RelocateEndpoint destination,
            string relativeSubdir, long maxFileBytes, CancellationToken ct)
        {
            // Mirror the real copy for local→local so VerifyDestination
            // can find a project.yml. Skip everything else.
            var result = new RelocateTransferResult();
            if (!source.IsSsh && !destination.IsSsh && Directory.Exists(source.LocalPath))
            {
                var srcDir = Path.Combine(source.LocalPath, relativeSubdir);
                var tgtDir = Path.Combine(destination.LocalPath, relativeSubdir);
                if (Directory.Exists(srcDir))
                {
                    Directory.CreateDirectory(tgtDir);
                    foreach (var f in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
                    {
                        var rel = Path.GetRelativePath(srcDir, f);
                        var dst = Path.Combine(tgtDir, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(f, dst, overwrite: true);
                        result.FilesCopied++;
                    }
                }
            }
            result.Success = true;
            return Task.FromResult(result);
        }

        public Task<RelocateTransferResult> CopyFilesAsync(
            RelocateEndpoint source, RelocateEndpoint destination,
            IEnumerable<string> relativePaths, long maxFileBytes, CancellationToken ct)
        {
            CopyFilesCallCount++;
            var paths = relativePaths.ToList();
            return Task.FromResult(
                CopyFilesResult
                ?? new RelocateTransferResult
                {
                    Success = true,
                    FilesCopied = paths.Count
                });
        }
    }

    private sealed class FakeCloneService : IAsyncCloneService
    {
        public Func<CloneRequest, CancellationToken, CloneResult>? OnInvoke { get; set; }
        public CloneRequest? LastRequest { get; private set; }
        public Task<CloneResult> CloneAsync(CloneRequest request, IProgress<CloneProgress>? progress, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(OnInvoke?.Invoke(request, ct) ?? CloneResult.Ok("main", "abc", "Cloned."));
        }
    }

    private sealed class FakeRegistration : IProjectRegistrationService
    {
        public bool RegisterCalled { get; private set; }
        public ProjectRegistrationRequest LastRequest { get; private set; } = new();

        public ProjectRegistrationResult RegisterProject(ProjectRegistrationRequest request)
        {
            RegisterCalled = true;
            LastRequest = request;
            return new ProjectRegistrationResult { Success = true, ProjectId = request.ProjectId, Message = "ok" };
        }

        public ProjectRegistrationResult RemoveProject(string projectId)
            => new() { Success = true, ProjectId = projectId, Message = "ok" };
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _on;
        public SyncProgress(Action<T> on) { _on = on; }
        public void Report(T value) => _on(value);
    }
}
