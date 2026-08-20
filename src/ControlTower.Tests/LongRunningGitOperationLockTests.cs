using System;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

/// <summary>
/// Verifies the SemaphoreSlim-based <see cref="LongRunningGitOperationLock"/>
/// behaves as a single-permit mutex with cancellable AcquireAsync and
/// Dispose-based release.
/// </summary>
public class LongRunningGitOperationLockTests
{
    [Fact]
    public async Task AcquireAsync_OnlyOneHolderAtATime()
    {
        var sut = new LongRunningGitOperationLock();
        Assert.False(sut.IsHeld);

        var firstHandle = await sut.AcquireAsync(CancellationToken.None);
        Assert.True(sut.IsHeld);

        var secondTask = sut.AcquireAsync(CancellationToken.None);

        // Give the runtime a chance to run the second task. It must not
        // complete while the first holder still owns the lock.
        await Task.Delay(50);
        Assert.False(secondTask.IsCompleted, "second acquire must wait while first holder owns the lock");

        firstHandle.Dispose();

        var secondHandle = await secondTask;
        Assert.True(sut.IsHeld);
        secondHandle.Dispose();
        Assert.False(sut.IsHeld);
    }

    [Fact]
    public async Task AcquireAsync_DisposingHandleReleasesLock()
    {
        var sut = new LongRunningGitOperationLock();

        var handle = await sut.AcquireAsync(CancellationToken.None);
        Assert.True(sut.IsHeld);
        handle.Dispose();
        Assert.False(sut.IsHeld);

        // Double-dispose must not leak permits.
        handle.Dispose();
        Assert.False(sut.IsHeld);

        // Re-acquire still works.
        var second = await sut.AcquireAsync(CancellationToken.None);
        Assert.True(sut.IsHeld);
        second.Dispose();
    }

    [Fact]
    public async Task AcquireAsync_CancellationWhileWaitingThrows()
    {
        var sut = new LongRunningGitOperationLock();
        var firstHandle = await sut.AcquireAsync(CancellationToken.None);

        using var cts = new CancellationTokenSource();
        var waiter = sut.AcquireAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        // Lock is still held by the first owner; releasing it lets a
        // fresh acquirer through.
        firstHandle.Dispose();
        var fresh = await sut.AcquireAsync(CancellationToken.None);
        fresh.Dispose();
    }
}
