using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

public class GitProcessAdapterTests
{
    [Fact]
    public async Task RunAsync_ArgsArePassedExactly_NoConcatenation()
    {
        ProcessStartInfo? captured = null;
        var fake = new FakeHandle("ok", "", exitCode: 0);
        var adapter = new GitProcessAdapter(new ToolSettings(), psi =>
        {
            captured = psi;
            return fake;
        });

        // Force the handle to "exit" before we call WaitForExit so the test
        // is deterministic and quick.
        fake.Complete(0);

        var cwd = Directory.GetCurrentDirectory();
        var args = new List<string> { "rev-parse", "--abbrev-ref", "HEAD with space", "--", "arg=with space" };
        var result = await adapter.RunAsync(args, cwd, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        Assert.NotNull(captured);
        Assert.Equal(5, captured!.ArgumentList.Count);
        Assert.Equal("rev-parse", captured.ArgumentList[0]);
        Assert.Equal("--abbrev-ref", captured.ArgumentList[1]);
        Assert.Equal("HEAD with space", captured.ArgumentList[2]);
        Assert.Equal("--", captured.ArgumentList[3]);
        Assert.Equal("arg=with space", captured.ArgumentList[4]);
        Assert.Equal(string.Empty, captured.Arguments); // never concatenated
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_StdoutAndStderrAreRedacted_BeforeReturning()
    {
        // Token shape the redaction list catches outright: ghp_<base62>
        const string secret = "ghp_" + "AbCdEfGhIjKlMnOpQrStUvWxYz1234567890";
        var fake = new FakeHandle(
            stdout: $"output contains {secret} keep going\n",
            stderr: $"warning: token={secret} leaked\n",
            exitCode: 0);
        fake.Complete(0);

        var adapter = new GitProcessAdapter(new ToolSettings(), _ => fake);

        var cwd = Directory.GetCurrentDirectory();
        var result = await adapter.RunAsync(
            new[] { "status" }, cwd, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        Assert.DoesNotContain(secret, result.Stdout);
        Assert.DoesNotContain(secret, result.Stderr);
        Assert.Contains("[redacted", result.Stdout);
        Assert.Contains("[redacted", result.Stderr);
    }

    [Fact]
    public async Task RunAsync_ProgressLines_AreRedacted()
    {
        const string secret = "ghp_" + "AbCdEfGhIjKlMnOpQrStUvWxYz1234567890";
        var fake = new FakeHandle(
            stdout: $"first line with {secret}\nsecond clean line\n",
            stderr: string.Empty,
            exitCode: 0);
        fake.Complete(0);

        var adapter = new GitProcessAdapter(new ToolSettings(), _ => fake);
        var collected = new List<string>();
        var progress = new Progress<string>(l => collected.Add(l));

        var cwd = Directory.GetCurrentDirectory();
        var result = await adapter.RunAsync(
            new[] { "status" }, cwd, TimeSpan.FromSeconds(5), progress, CancellationToken.None);

        // Progress is async; flush by yielding a couple of times.
        await Task.Yield();
        for (int i = 0; i < 5 && collected.Count == 0; i++)
        {
            await Task.Delay(10);
        }

        Assert.True(collected.Count >= 1, "progress callback never fired");
        foreach (var line in collected)
        {
            Assert.DoesNotContain(secret, line);
        }
        Assert.Equal(0, result.ExitCode);
    }

    [Fact]
    public async Task RunAsync_Cancellation_CancelsMidRunAndKillsProcess()
    {
        var fake = new FakeHandle("", "", exitCode: 0);
        // Intentionally do NOT call Complete — the handle stays "running".
        var adapter = new GitProcessAdapter(new ToolSettings(), _ => fake);

        var cwd = Directory.GetCurrentDirectory();
        using var cts = new CancellationTokenSource();
        var runTask = adapter.RunAsync(
            new[] { "fetch" }, cwd, TimeSpan.FromSeconds(30), null, cts.Token);

        // Give the adapter a moment to start waiting.
        await Task.Delay(50);
        cts.Cancel();

        var result = await runTask;

        Assert.True(result.Cancelled);
        Assert.False(result.TimedOut);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(fake.KillCalled, "Kill was not invoked on cancellation");
    }

    [Fact]
    public async Task RunAsync_Timeout_ReturnsTimedOutAndKills()
    {
        var fake = new FakeHandle("", "", exitCode: 0);
        // Never call Complete — let the soft timeout fire.
        var adapter = new GitProcessAdapter(new ToolSettings(), _ => fake);

        var cwd = Directory.GetCurrentDirectory();
        var result = await adapter.RunAsync(
            new[] { "fetch" }, cwd, TimeSpan.FromMilliseconds(50), null, CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.False(result.Cancelled);
        Assert.Equal(-1, result.ExitCode);
        Assert.True(fake.KillCalled);
    }

    [Fact]
    public async Task RunAsync_MissingWorkingDirectory_ThrowsEarlyWithoutStartingProcess()
    {
        bool factoryCalled = false;
        var adapter = new GitProcessAdapter(new ToolSettings(), _ =>
        {
            factoryCalled = true;
            return new FakeHandle("", "", 0);
        });

        var bogus = Path.Combine(Path.GetTempPath(), "ct-nonexistent-" + Guid.NewGuid().ToString("N"));

        await Assert.ThrowsAsync<DirectoryNotFoundException>(async () =>
            await adapter.RunAsync(
                new[] { "status" }, bogus, TimeSpan.FromSeconds(5), null, CancellationToken.None));

        Assert.False(factoryCalled, "process must not start when working dir is missing");
    }

    [Fact]
    public async Task RunAsync_GitNotFound_SurfacesGitNotFoundException()
    {
        var settings = new ToolSettings { GitCommand = "definitely-not-git-" + Guid.NewGuid().ToString("N") };
        var adapter = new GitProcessAdapter(settings); // default real handle factory

        var cwd = Directory.GetCurrentDirectory();
        await Assert.ThrowsAsync<GitNotFoundException>(async () =>
            await adapter.RunAsync(
                new[] { "status" }, cwd, TimeSpan.FromSeconds(2), null, CancellationToken.None));
    }

    [Fact]
    public async Task RunAsync_NullArguments_Throws()
    {
        var adapter = new GitProcessAdapter(new ToolSettings(), _ => new FakeHandle("", "", 0));
        var cwd = Directory.GetCurrentDirectory();

        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await adapter.RunAsync(null!, cwd, TimeSpan.FromSeconds(2), null, CancellationToken.None));
    }

    private sealed class FakeHandle : IGitProcessHandle
    {
        private readonly TaskCompletionSource _exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public FakeHandle(string stdout, string stderr, int exitCode)
        {
            StandardOutput = new StringReader(stdout);
            StandardError = new StringReader(stderr);
            _pendingExitCode = exitCode;
        }

        private int _pendingExitCode;
        public TextReader StandardOutput { get; }
        public TextReader StandardError { get; }
        public bool HasExited { get; private set; }
        public int ExitCode { get; private set; }
        public bool KillCalled { get; private set; }

        public void Complete(int exitCode)
        {
            ExitCode = exitCode;
            HasExited = true;
            _exit.TrySetResult();
        }

        public Task WaitForExitAsync(CancellationToken ct)
        {
            return _exit.Task.WaitAsync(ct);
        }

        public void Kill(bool entireProcessTree)
        {
            KillCalled = true;
            HasExited = true;
            ExitCode = _pendingExitCode;
            _exit.TrySetResult();
        }

        public void Dispose()
        {
            StandardOutput.Dispose();
            StandardError.Dispose();
        }
    }
}
