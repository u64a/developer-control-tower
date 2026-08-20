using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

/// <summary>
/// Tests for <see cref="RestoreOrchestrator"/> using a fake
/// <see cref="IAsyncCloneService"/> and a fake
/// <see cref="IQuarantineService"/>. The real
/// <see cref="LongRunningGitOperationLock"/> is used so concurrency is
/// exercised end-to-end.
/// </summary>
public class RestoreOrchestratorTests
{
    [Fact]
    public async Task RestoreAsync_HonoursTheLock_SecondConcurrentCallWaits()
    {
        var lockImpl = new LongRunningGitOperationLock();
        var clone = new FakeCloneService();
        var quarantine = new FakeQuarantineService();
        var sut = new RestoreOrchestrator(clone, quarantine, lockImpl);

        // Hold the lock manually so the orchestrator must wait.
        var holder = await lockImpl.AcquireAsync(CancellationToken.None);

        var selections = new[] { MakeSelection("p", RestoreClassification.Missing, RestoreAction.Clone) };
        var collected = new List<RestoreRowUpdate>();
        var progress = new SyncProgress(collected.Add);

        var task = sut.RestoreAsync(selections, progress, CancellationToken.None);

        await Task.Delay(50);
        Assert.False(task.IsCompleted, "must wait while the lock is held externally");
        Assert.Empty(collected);

        holder.Dispose();
        await task;

        Assert.Contains(collected, u => u.State == RestoreRowState.Done);
    }

    [Fact]
    public async Task RestoreAsync_MissingCandidate_EmitsCloningThenDone()
    {
        var sut = NewSut(out var clone, out _);

        var selections = new[] { MakeSelection("p", RestoreClassification.Missing, RestoreAction.Clone) };
        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        Assert.Single(clone.Calls);
        Assert.Contains(collected, u => u.State == RestoreRowState.Cloning);
        Assert.Equal(RestoreRowState.Done, collected.Last().State);
    }

    [Fact]
    public async Task RestoreAsync_EmptyFolderCandidate_TreatedSameAsMissing()
    {
        var sut = NewSut(out var clone, out _);

        var selections = new[] { MakeSelection("p", RestoreClassification.EmptyFolder, RestoreAction.Clone) };
        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        Assert.Single(clone.Calls);
        Assert.Equal(RestoreRowState.Done, collected.Last().State);
    }

    [Fact]
    public async Task RestoreAsync_AlreadyClonedCandidate_NoCloneEmitsAlreadyCloned()
    {
        var sut = NewSut(out var clone, out var quarantine);

        var selections = new[] { MakeSelection("p", RestoreClassification.AlreadyCloned, RestoreAction.Clone) };
        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        Assert.Empty(clone.Calls);
        Assert.Empty(quarantine.Calls);
        Assert.Equal(RestoreRowState.AlreadyCloned, collected.Last().State);
    }

    [Fact]
    public async Task RestoreAsync_UnsafeExistingCandidate_NoCloneEmitsUnsafe()
    {
        var sut = NewSut(out var clone, out _);

        var selections = new[] { MakeSelection("p", RestoreClassification.UnsafeExisting, RestoreAction.Clone) };
        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        Assert.Empty(clone.Calls);
        Assert.Equal(RestoreRowState.UnsafeExisting, collected.Last().State);
    }

    [Fact]
    public async Task RestoreAsync_ConflictQuarantineAndClone_QuarantinesThenClones()
    {
        var sut = NewSut(out var clone, out var quarantine);

        var selections = new[]
        {
            MakeSelection("p", RestoreClassification.ConflictNonEmpty, RestoreAction.QuarantineAndClone)
        };
        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        Assert.Single(quarantine.Calls);
        Assert.Single(clone.Calls);
        Assert.Contains(collected, u => u.State == RestoreRowState.Quarantining);
        Assert.Contains(collected, u => u.State == RestoreRowState.Cloning);
        Assert.Equal(RestoreRowState.Done, collected.Last().State);
        Assert.Equal("p", quarantine.Calls[0].slug);
    }

    [Fact]
    public async Task RestoreAsync_ConflictSkip_NoQuarantineNoClone()
    {
        var sut = NewSut(out var clone, out var quarantine);

        var selections = new[]
        {
            MakeSelection("p", RestoreClassification.ConflictNonEmpty, RestoreAction.Skip)
        };
        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        Assert.Empty(quarantine.Calls);
        Assert.Empty(clone.Calls);
        Assert.Equal(RestoreRowState.Skipped, collected.Last().State);
    }

    [Fact]
    public async Task RestoreAsync_FailedClone_EmitsFailedWithCloneErrorCode()
    {
        var sut = NewSut(out var clone, out _);
        clone.NextResult = CloneResult.Failure(CloneError.CommandFailed, "git boom");

        var selections = new[] { MakeSelection("p", RestoreClassification.Missing, RestoreAction.Clone) };
        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        var terminal = collected.Last();
        Assert.Equal(RestoreRowState.Failed, terminal.State);
        Assert.Equal(nameof(CloneError.CommandFailed), terminal.ErrorCode);
        Assert.Contains("git boom", terminal.ErrorMessage);
    }

    [Fact]
    public async Task RestoreAsync_CredentialBearingUrl_FailedWithCredentialErrorAndNoClone()
    {
        var sut = NewSut(out var clone, out _);

        var candidate = NewCandidate("p", RestoreClassification.Missing,
            remoteUrl: "https:/" + "/user:pat@github.com/example/repo.git");
        var selections = new[] { new RestoreSelection(candidate, RestoreAction.Clone) };

        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        Assert.Empty(clone.Calls);
        var terminal = collected.Last();
        Assert.Equal(RestoreRowState.Failed, terminal.State);
        Assert.Equal(nameof(CloneError.CredentialInUrl), terminal.ErrorCode);
        // The error message must not leak the secret.
        Assert.DoesNotContain("pat", terminal.ErrorMessage ?? string.Empty);
    }

    [Fact]
    public async Task RestoreAsync_Cancellation_StopsAfterCurrentRowAndSkipsRest()
    {
        var sut = NewSut(out var clone, out _);
        using var cts = new CancellationTokenSource();

        // Configure the clone service to honor cancellation: it will see the
        // token cancel during its execution and return a Cancelled result.
        clone.OnInvoke = (_, ct) =>
        {
            cts.Cancel();
            return CloneResult.CancelledResult("Cancelled mid-clone.");
        };

        var selections = new[]
        {
            MakeSelection("p1", RestoreClassification.Missing, RestoreAction.Clone),
            MakeSelection("p2", RestoreClassification.Missing, RestoreAction.Clone),
            MakeSelection("p3", RestoreClassification.Missing, RestoreAction.Clone),
        };

        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), cts.Token);

        Assert.Single(clone.Calls); // only the first row started a clone
        var p1 = collected.Where(u => u.ProjectId == "p1").Last();
        Assert.Equal(RestoreRowState.Failed, p1.State);
        Assert.Equal("restore/cancelled", p1.ErrorCode);

        // p2 and p3 should be marked Skipped (batch cancelled) with no Cloning state.
        Assert.DoesNotContain(collected, u => u.ProjectId == "p2" && u.State == RestoreRowState.Cloning);
        Assert.DoesNotContain(collected, u => u.ProjectId == "p3" && u.State == RestoreRowState.Cloning);
        Assert.Contains(collected, u => u.ProjectId == "p2" && u.State == RestoreRowState.Skipped);
        Assert.Contains(collected, u => u.ProjectId == "p3" && u.State == RestoreRowState.Skipped);
    }

    [Fact]
    public async Task RestoreAsync_QuarantineFails_EmitsFailedWithoutCloning()
    {
        var sut = NewSut(out var clone, out var quarantine);
        quarantine.NextException = new System.IO.IOException("locked");

        var selections = new[]
        {
            MakeSelection("p", RestoreClassification.ConflictNonEmpty, RestoreAction.QuarantineAndClone)
        };
        var collected = new List<RestoreRowUpdate>();
        await sut.RestoreAsync(selections, new SyncProgress(collected.Add), CancellationToken.None);

        Assert.Empty(clone.Calls);
        var terminal = collected.Last();
        Assert.Equal(RestoreRowState.Failed, terminal.State);
        Assert.Equal("restore/quarantine-failed", terminal.ErrorCode);
        Assert.Contains("locked", terminal.ErrorMessage);
    }

    // ---- helpers ----

    private static RestoreOrchestrator NewSut(
        out FakeCloneService clone, out FakeQuarantineService quarantine)
    {
        clone = new FakeCloneService();
        quarantine = new FakeQuarantineService();
        var lockImpl = new LongRunningGitOperationLock();
        return new RestoreOrchestrator(clone, quarantine, lockImpl);
    }

    private static RestoreSelection MakeSelection(
        string projectId, RestoreClassification classification, RestoreAction action)
    {
        return new RestoreSelection(NewCandidate(projectId, classification), action);
    }

    private static RestoreCandidate NewCandidate(
        string projectId, RestoreClassification classification,
        string remoteUrl = "https://github.com/example/repo.git")
    {
        return new RestoreCandidate(
            ProjectId: projectId,
            ProjectName: projectId,
            Slug: projectId,
            ExpectedPath: System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ct-orch-" + projectId),
            RemoteUrl: remoteUrl,
            CanonicalRemoteUrl: remoteUrl,
            Classification: classification,
            Detail: string.Empty);
    }

    private sealed class FakeCloneService : IAsyncCloneService
    {
        public ConcurrentBag<CloneRequest> Calls { get; } = new();
        public CloneResult NextResult { get; set; } = CloneResult.Ok("main", "abc1234", "Cloned.");
        public Func<CloneRequest, CancellationToken, CloneResult>? OnInvoke { get; set; }

        public Task<CloneResult> CloneAsync(
            CloneRequest request, IProgress<CloneProgress>? progress, CancellationToken ct)
        {
            Calls.Add(request);
            progress?.Report(new CloneProgress("starting", null, "Cloning into '" + request.DestinationPath + "'..."));
            progress?.Report(new CloneProgress("receiving", 50, "Receiving objects: 50% (1/2)"));
            var result = OnInvoke != null ? OnInvoke(request, ct) : NextResult;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeQuarantineService : IQuarantineService
    {
        public List<(string source, string slug)> Calls { get; } = new();
        public Exception? NextException { get; set; }

        public Task<string> QuarantineAsync(string sourcePath, string slug, CancellationToken ct)
        {
            Calls.Add((sourcePath, slug));
            if (NextException != null) throw NextException;
            return Task.FromResult(sourcePath + "-quarantined");
        }
    }

    /// <summary>
    /// Synchronous IProgress&lt;T&gt; that invokes the callback inline on
    /// the reporting thread. The default Progress&lt;T&gt; marshals to
    /// the captured SynchronizationContext, which xUnit tests don't
    /// reliably pump — using a sync sink keeps the assertions reliable.
    /// </summary>
    private sealed class SyncProgress : IProgress<RestoreRowUpdate>
    {
        private readonly Action<RestoreRowUpdate> _onReport;
        public SyncProgress(Action<RestoreRowUpdate> onReport) { _onReport = onReport; }
        public void Report(RestoreRowUpdate value) => _onReport(value);
    }
}
