#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Launch;
using ControlTower.Infrastructure.Update;

namespace ControlTower.Tests;

/// <summary>
/// Tests for <see cref="UpdateService"/> covering repo-root resolution,
/// the check decision table, and script-generation / launch flow. All
/// git interaction goes through a fake adapter; no real <c>git.exe</c>
/// invocation happens here.
/// </summary>
public class UpdateServiceTests : IDisposable
{
    private readonly string _scratchRoot;
    private readonly List<(string Level, string Message)> _logSink = new();
    private readonly Action<string, string, Exception?> _logger;

    public UpdateServiceTests()
    {
        _scratchRoot = Path.Combine(Path.GetTempPath(), "ct-upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
        _logger = (level, message, _) => _logSink.Add((level, message));
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchRoot, recursive: true); } catch { }
    }

    // ---------- repo-root resolution ----------

    [Fact]
    public void TryResolveRepoRoot_OverrideMissingDotGit_ReturnsRepoNotFound()
    {
        var override_ = Path.Combine(_scratchRoot, "no-git");
        Directory.CreateDirectory(override_);

        var service = NewBareService();
        var ok = service.TryResolveRepoRoot(
            new UpdateOptions("main", true, override_),
            out var resolved, out var status, out var message);

        Assert.False(ok);
        Assert.Equal(UpdateStatus.RepoNotFound, status);
        Assert.Contains(".git", message);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveRepoRoot_OverrideMissingSolution_ReturnsInvalidRepoRoot()
    {
        var override_ = Path.Combine(_scratchRoot, "no-sln");
        Directory.CreateDirectory(override_);
        Directory.CreateDirectory(Path.Combine(override_, ".git"));

        var service = NewBareService();
        var ok = service.TryResolveRepoRoot(
            new UpdateOptions("main", true, override_),
            out var resolved, out var status, out _);

        Assert.False(ok);
        Assert.Equal(UpdateStatus.InvalidRepoRoot, status);
        Assert.Equal(override_, resolved);
    }

    [Fact]
    public void TryResolveRepoRoot_ValidOverride_Succeeds()
    {
        var override_ = Path.Combine(_scratchRoot, "good");
        Directory.CreateDirectory(override_);
        Directory.CreateDirectory(Path.Combine(override_, ".git"));
        File.WriteAllText(Path.Combine(override_, "DeveloperControlTower.sln"), "stub");

        var service = NewBareService();
        var ok = service.TryResolveRepoRoot(
            new UpdateOptions("main", true, override_),
            out var resolved, out _, out _);

        Assert.True(ok);
        Assert.Equal(override_, resolved);
    }

    [Fact]
    public void TryResolveRepoRoot_NoOverrideAndNoSentinel_ReportsRepoNotFound()
    {
        // Walk-up-from-exe auto-discovery was removed in the
        // install/update architecture rework: an installed app lives
        // OUTSIDE the source clone (e.g. C:\Program Files\Development
        // Tower) and gets the repo path from the update-repo-root.txt
        // sentinel that the installer drops next to the .exe. When
        // neither the override nor the sentinel is available we now
        // surface a clean RepoNotFound rather than silently picking up
        // an unrelated .git folder somewhere up the path.
        var fakeExeDir = Path.Combine(_scratchRoot, "no-sentinel");
        Directory.CreateDirectory(fakeExeDir);
        var fakeExe = Path.Combine(fakeExeDir, "ControlTower.Desktop.exe");
        File.WriteAllText(fakeExe, "stub");

        var service = new UpdateService(
            new FakeGitAdapter(), new FakeShellLauncher(),
            executablePathProvider: () => fakeExe,
            currentProcessIdProvider: () => 4242,
            tempPathProvider: () => _scratchRoot,
            logFolderProvider: () => _scratchRoot,
            logger: _logger);

        var ok = service.TryResolveRepoRoot(
            new UpdateOptions("main", true, string.Empty),
            out _, out var status, out var message);

        Assert.False(ok);
        Assert.Equal(UpdateStatus.RepoNotFound, status);
        Assert.Contains("update-repo-root.txt", message);
        Assert.Contains("Repo root override", message);
    }

    [Fact]
    public void TryResolveRepoRoot_SentinelFile_ResolvesToConfiguredClone()
    {
        // The publish/install script drops update-repo-root.txt next to
        // the installed .exe pointing back at the user's source clone.
        // The resolver must read it, validate the target is a real clone
        // (has .git + DeveloperControlTower.sln) and return that path.
        var repoRoot = MakeValidRepoRoot("sentinel-clone");
        var installDir = Path.Combine(_scratchRoot, "install-via-sentinel");
        Directory.CreateDirectory(installDir);
        var fakeExe = Path.Combine(installDir, "ControlTower.Desktop.exe");
        File.WriteAllText(fakeExe, "stub");
        File.WriteAllText(
            Path.Combine(installDir, "update-repo-root.txt"),
            "# Developer Control Tower update sentinel\n" +
            "# Source clone path:\n" +
            repoRoot + "\n");

        var service = new UpdateService(
            new FakeGitAdapter(), new FakeShellLauncher(),
            executablePathProvider: () => fakeExe,
            currentProcessIdProvider: () => 4242,
            tempPathProvider: () => _scratchRoot,
            logFolderProvider: () => _scratchRoot,
            logger: _logger);

        var ok = service.TryResolveRepoRoot(
            new UpdateOptions("main", true, string.Empty),
            out var resolved, out _, out _);

        Assert.True(ok);
        Assert.Equal(repoRoot, resolved);
    }

    // ---------- check pipeline ----------

    [Fact]
    public async Task CheckForUpdates_RepoNotFound_BubblesUp()
    {
        var override_ = Path.Combine(_scratchRoot, "missing");
        var service = NewBareService();

        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, override_), CancellationToken.None);

        Assert.Equal(UpdateStatus.RepoNotFound, result.Status);
        Assert.Equal("main", result.ConfiguredBranch);
    }

    [Fact]
    public async Task CheckForUpdates_WrongBranch_ReturnsWrongBranchWithoutFetching()
    {
        var repoRoot = MakeValidRepoRoot("wrong-branch");
        var git = new FakeGitAdapter();
        git.SetResponse(new[] { "rev-parse", "HEAD" }, ok("aaaaaaaaaaaaaaaa"));
        git.SetResponse(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ok("feature-x"));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.WrongBranch, result.Status);
        Assert.Equal("feature-x", result.Branch);
        Assert.Equal("main", result.ConfiguredBranch);
        // Should not have called fetch.
        Assert.DoesNotContain(git.RecordedCalls, c => c.Length > 0 && c[0] == "fetch");
    }

    [Fact]
    public async Task CheckForUpdates_NoUpstream_ReturnsNoUpstream()
    {
        var repoRoot = MakeValidRepoRoot("no-upstream");
        var git = new FakeGitAdapter();
        git.SetResponse(new[] { "rev-parse", "HEAD" }, ok("aaaaaaaaaaaaaaaa"));
        git.SetResponse(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ok("main"));
        git.SetResponse(new[] { "rev-parse", "--symbolic-full-name", "@{upstream}" }, fail(128));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.NoUpstream, result.Status);
    }

    [Fact]
    public async Task CheckForUpdates_UpstreamBranchDoesNotMatchConfiguredBranch_Blocks()
    {
        var repoRoot = MakeValidRepoRoot("wrong-upstream-branch");
        var git = new FakeGitAdapter();
        WireHappyPath(git, currentSha: "aaaaaaaaaaaaaaaa", branch: "main");
        git.SetResponse(
            new[] { "rev-parse", "--symbolic-full-name", "@{upstream}" },
            ok("refs/remotes/origin/other"));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.NoUpstream, result.Status);
        Assert.DoesNotContain(
            git.RecordedCalls,
            call => call.Length > 0 && call[0] == "fetch");
    }

    [Fact]
    public async Task CheckForUpdates_FetchFails_ReturnsFetchFailed()
    {
        var repoRoot = MakeValidRepoRoot("fetch-failed");
        var git = new FakeGitAdapter();
        WireHappyPath(git, currentSha: "aaaaaaaaaaaaaaaa", branch: "main");
        git.SetResponse(new[] { "fetch", "origin", "main" }, fail(1));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.FetchFailed, result.Status);
    }

    [Fact]
    public async Task CheckForUpdates_AheadBehindCommandFails_ReturnsFetchFailed()
    {
        var repoRoot = MakeValidRepoRoot("counts-failed");
        var git = new FakeGitAdapter();
        WireHappyPath(git, currentSha: "aaaaaaaaaaaaaaaa", branch: "main");
        git.SetResponse(
            new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" },
            fail(128));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.FetchFailed, result.Status);
        Assert.Contains("compare", result.Message);
    }

    [Fact]
    public async Task CheckForUpdates_UpToDate_ReturnsUpToDate()
    {
        var repoRoot = MakeValidRepoRoot("up-to-date");
        var git = new FakeGitAdapter();
        WireHappyPath(git, currentSha: "aaaaaaaaaaaaaaaa", branch: "main");
        git.SetResponse(new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" }, ok("0\t0"));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.UpToDate, result.Status);
        Assert.Equal(0, result.CommitsAhead);
        Assert.Equal(0, result.CommitsBehind);

        // The "Up to date" path must still leave a trail in the app log so
        // a user clicking Update when no update is available can see the
        // check actually ran.
        Assert.Contains(_logSink, e => e.Level == "INFO" && e.Message.StartsWith("Update check started.", StringComparison.Ordinal));
        Assert.Contains(_logSink, e =>
            e.Level == "INFO" &&
            e.Message.StartsWith("Update check complete.", StringComparison.Ordinal) &&
            e.Message.Contains("status=UpToDate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CheckForUpdates_LogsEveryStatusThroughInjectedLogger()
    {
        // Every check, regardless of outcome, must emit both a start and a
        // complete line via the injected logger - never the static
        // AppLogger - so unit tests cannot pollute the real %LOCALAPPDATA%
        // app log file.
        var override_ = Path.Combine(_scratchRoot, "definitely-missing");
        var service = NewBareService();

        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("develop", true, override_), CancellationToken.None);

        Assert.Equal(UpdateStatus.RepoNotFound, result.Status);

        var started = _logSink.FirstOrDefault(e =>
            e.Level == "INFO" && e.Message.StartsWith("Update check started.", StringComparison.Ordinal));
        Assert.NotEqual(default, started);
        Assert.Contains("branch=develop", started.Message);
        Assert.Contains("repoOverride=" + override_, started.Message);

        var completed = _logSink.FirstOrDefault(e =>
            e.Level == "INFO" && e.Message.StartsWith("Update check complete.", StringComparison.Ordinal));
        Assert.NotEqual(default, completed);
        Assert.Contains("status=RepoNotFound", completed.Message);
    }

    [Fact]
    public async Task CheckForUpdates_BehindAndClean_ReturnsUpdateAvailable()
    {
        var repoRoot = MakeValidRepoRoot("avail");
        var git = new FakeGitAdapter();
        WireHappyPath(git, currentSha: "aaaaaaaaaaaaaaaa", branch: "main");
        git.SetResponse(new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" }, ok("0\t3"));
        git.SetResponse(new[] { "status", "--porcelain" }, ok(string.Empty));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(3, result.CommitsBehind);
        Assert.Equal(0, result.CommitsAhead);
    }

    [Fact]
    public async Task CheckForUpdates_BehindAndDirty_ReturnsDirtyTree()
    {
        var repoRoot = MakeValidRepoRoot("dirty");
        var git = new FakeGitAdapter();
        WireHappyPath(git, currentSha: "aaaaaaaaaaaaaaaa", branch: "main");
        git.SetResponse(new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" }, ok("0\t2"));
        git.SetResponse(new[] { "status", "--porcelain" }, ok(" M src/foo.cs"));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.DirtyTree, result.Status);
    }

    [Fact]
    public async Task CheckForUpdates_AheadOnly_ReturnsAheadOfOrigin()
    {
        var repoRoot = MakeValidRepoRoot("ahead");
        var git = new FakeGitAdapter();
        WireHappyPath(git, currentSha: "aaaaaaaaaaaaaaaa", branch: "main");
        git.SetResponse(new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" }, ok("4\t0"));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.AheadOfOrigin, result.Status);
        Assert.Equal(4, result.CommitsAhead);
    }

    [Fact]
    public async Task CheckForUpdates_AheadAndBehind_ReturnsDiverged_NotDirtyTree()
    {
        var repoRoot = MakeValidRepoRoot("diverged");
        var git = new FakeGitAdapter();
        WireHappyPath(git, currentSha: "aaaaaaaaaaaaaaaa", branch: "main");
        git.SetResponse(new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" }, ok("2\t3"));
        // Even if dirty, Diverged must win — the decision table runs ahead/behind first.
        git.SetResponse(new[] { "status", "--porcelain" }, ok(" M src/foo.cs"));

        var service = NewService(git);
        var result = await service.CheckForUpdatesAsync(
            new UpdateOptions("main", true, repoRoot), CancellationToken.None);

        Assert.Equal(UpdateStatus.Diverged, result.Status);
        Assert.Equal(2, result.CommitsAhead);
        Assert.Equal(3, result.CommitsBehind);
    }

    // ---------- launch flow ----------

    [Fact]
    public async Task LaunchUpdate_RejectsWhenStatusNotUpdateAvailable()
    {
        var repoRoot = MakeValidRepoRoot("reject-status");
        var service = NewService(new FakeGitAdapter());

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpToDate,
                CurrentSha: "a", RemoteSha: "a", Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 0, CommitsAhead: 0,
                RepoRoot: repoRoot, ExecutablePath: "ignored", Message: "x"),
            CancellationToken.None);

        Assert.False(result.Spawned);
        Assert.Contains("not available", result.Message);
    }

    [Fact]
    public async Task LaunchUpdate_AbortsWhenTreeBecameDirty()
    {
        var repoRoot = MakeValidRepoRoot("abort-dirty");
        var git = new FakeGitAdapter();
        git.SetResponse(new[] { "status", "--porcelain" }, ok(" M foo"));

        var shell = new FakeShellLauncher();
        var service = NewService(git, shell);
        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "aaaaaaaa", RemoteSha: "bbbbbbbb", Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 2, CommitsAhead: 0,
                RepoRoot: repoRoot, ExecutablePath: "ignored", Message: "x"),
            CancellationToken.None);

        Assert.False(result.Spawned);
        Assert.Equal(0, shell.LaunchUpdateConsoleCalls);
        Assert.Equal(0, shell.LaunchUpdateConsoleElevatedCalls);
    }

    [Fact]
    public async Task LaunchUpdate_WritesScriptAndSpawnsConsole()
    {
        var repoRoot = MakeValidRepoRoot("happy-launch");
        var tempRoot = Path.Combine(_scratchRoot, "tempdir");
        var logRoot = Path.Combine(_scratchRoot, "logdir");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(logRoot);

        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "0", behind: "2");

        var shell = new FakeShellLauncher { ReturnedPid = 12345 };
        var service = new UpdateService(
            git, shell,
            executablePathProvider: () => Path.Combine(repoRoot, "src", "ControlTower.Desktop", "bin", "Release", "net8.0-windows", "ControlTower.Desktop.exe"),
            currentProcessIdProvider: () => 7777,
            tempPathProvider: () => tempRoot,
            logFolderProvider: () => logRoot,
            logger: _logger,
            installDirWritableProbe: _ => true);

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "abcdef1234567890", RemoteSha: "1234567890abcdef",
                Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 2, CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
                Message: "x"),
            CancellationToken.None);

        Assert.True(result.Spawned);
        // The install dir is writable by the current user, so the update
        // runs in-context (no UAC). The elevated path must not be invoked.
        Assert.Equal(1, shell.LaunchUpdateConsoleCalls);
        Assert.Equal(0, shell.LaunchUpdateConsoleElevatedCalls);
        Assert.NotNull(shell.LastScriptPath);
        Assert.True(File.Exists(shell.LastScriptPath));

        var contents = File.ReadAllText(shell.LastScriptPath!);
        Assert.Contains("git fetch origin main", contents);
        Assert.Contains("git merge-base --is-ancestor HEAD FETCH_HEAD", contents);
        Assert.Contains("git merge --ff-only FETCH_HEAD", contents);
        Assert.DoesNotContain("git pull --ff-only", contents);
        Assert.Contains("git symbolic-ref --quiet HEAD", contents);
        Assert.Contains(
            "findstr /x /l /c:\"refs/heads/%BRANCH%\" \"%TEEFILE%\"",
            contents);
        // The build step is now a publish into a staging folder that
        // robocopy mirrors over the install dir. dotnet build alone is
        // no longer used because it leaves stale assemblies behind.
        Assert.Contains("dotnet publish \"", contents);
        Assert.Contains("robocopy ", contents);
        Assert.Contains("/XD library", contents);
        Assert.Contains(
            "/XF update-repo-root.txt .developer-control-tower-install",
            contents);
        Assert.Contains("PID 7777", contents);
        // exe path appears for the relaunch step.
        Assert.Contains("ControlTower.Desktop.exe", contents);

        // Logging contract: the script must wire up the app log folder, compute
        // the daily file name itself, and bracket the run with begin/end markers
        // so a single tail of the app log tells the whole story.
        Assert.Contains("setlocal EnableExtensions", contents);
        Assert.Contains("set \"LOG_DIR=" + logRoot + "\"", contents);
        Assert.Contains("[DateTime]::UtcNow.ToString('yyyyMMdd')", contents);
        Assert.Contains("set \"LOG=%LOG_DIR%\\app-%TODAY%.log\"", contents);
        Assert.Contains("========== Update script begin (PID 7777, branch main, from abcdef12 to 12345678) ==========", contents);
        Assert.Contains("========== Update script end: SUCCESS", contents);
        Assert.Contains("========== Update script end: FAILURE (step=%STEP% exit=%RC%) ==========", contents);
        Assert.Contains(":fail", contents);
        Assert.Contains("endlocal & exit /b 0", contents);
        Assert.Contains("endlocal & exit /b %RC%", contents);

        // The destructive mirror is gated by ownership. Existing installs
        // must have either a well-formed marker or the legacy exe+sentinel
        // pair, and the marker is preserved and refreshed after robocopy.
        var ownershipCheck = contents.IndexOf(
            "set \"STEP=verify install ownership\"",
            StringComparison.Ordinal);
        var robocopy = contents.IndexOf(
            "robocopy \"%STAGE%\" \"%INSTALL%\"",
            StringComparison.Ordinal);
        var markerRefresh = contents.IndexOf(
            ">\"%OWNER%\" echo Developer Control Tower managed install v1",
            StringComparison.Ordinal);
        Assert.True(ownershipCheck >= 0 && ownershipCheck < robocopy);
        Assert.True(markerRefresh > robocopy);
        Assert.Contains("[IO.FileAttributes]::ReparsePoint", contents);
        Assert.Contains(
            "if not exist \"%INSTALL%\\ControlTower.Desktop.exe\" goto invalid_install_ownership",
            contents);
        Assert.Contains(
            "if not exist \"%SENTINEL%\" goto invalid_install_ownership",
            contents);
        Assert.Contains("[IO.File]::ReadAllLines($env:SENTINEL)", contents);
        Assert.Contains(
            "[string]::Equals($actual,$expected,[StringComparison]::OrdinalIgnoreCase)",
            contents);

        // The handoff + spawned-PID lines must reach the injected logger,
        // not the static AppLogger - otherwise the test process pollutes
        // the real user app log file.
        Assert.Contains(_logSink, e =>
            e.Level == "INFO" &&
            e.Message.Contains("Launching non-elevated update script", StringComparison.Ordinal) &&
            e.Message.Contains(logRoot, StringComparison.Ordinal));
        Assert.Contains(_logSink, e =>
            e.Level == "INFO" &&
            e.Message.Contains("Spawned update console PID 12345", StringComparison.Ordinal) &&
            e.Message.Contains("elevated=False", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchUpdate_ElevatesWhenInstallDirNotWritable()
    {
        var repoRoot = MakeValidRepoRoot("elevate-launch");
        var tempRoot = Path.Combine(_scratchRoot, "tempdir-elev");
        var logRoot = Path.Combine(_scratchRoot, "logdir-elev");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(logRoot);

        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "0", behind: "2");

        var shell = new FakeShellLauncher { ReturnedPid = 999 };
        var service = new UpdateService(
            git, shell,
            executablePathProvider: () => Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
            currentProcessIdProvider: () => 7777,
            tempPathProvider: () => tempRoot,
            logFolderProvider: () => logRoot,
            logger: _logger,
            installDirWritableProbe: _ => false);

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "abcdef1234567890", RemoteSha: "1234567890abcdef",
                Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 2, CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
                Message: "x"),
            CancellationToken.None);

        Assert.True(result.Spawned);
        // The install dir is not writable (e.g. Program Files), so the
        // update must be elevated. The non-elevated path must not be used.
        Assert.Equal(1, shell.LaunchUpdateConsoleElevatedCalls);
        Assert.Equal(0, shell.LaunchUpdateConsoleCalls);
        Assert.Contains(_logSink, e =>
            e.Level == "INFO" &&
            e.Message.Contains("Launching elevated update script", StringComparison.Ordinal));
        Assert.Contains(_logSink, e =>
            e.Level == "INFO" &&
            e.Message.Contains("Spawned update console PID 999", StringComparison.Ordinal) &&
            e.Message.Contains("elevated=True", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LaunchUpdate_ScriptTeesEveryCommandToAppLog()
    {
        var repoRoot = MakeValidRepoRoot("tee-launch");
        var tempRoot = Path.Combine(_scratchRoot, "tempdir2");
        var logRoot = Path.Combine(_scratchRoot, "logdir2");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(logRoot);

        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "0", behind: "1");

        var shell = new FakeShellLauncher { ReturnedPid = 42 };
        var service = new UpdateService(
            git, shell,
            executablePathProvider: () => Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
            currentProcessIdProvider: () => 1,
            tempPathProvider: () => tempRoot,
            logFolderProvider: () => logRoot,
            logger: _logger);

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "aaaaaaaa", RemoteSha: "bbbbbbbb",
                Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 1, CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
                Message: "x"),
            CancellationToken.None);

        Assert.True(result.Spawned);
        var contents = File.ReadAllText(shell.LastScriptPath!);

        // Each major command must use the temp-file tee pattern so the user
        // sees output AND the same lines reach the daily app log AND the
        // command's exit code survives.
        Assert.Contains("=== git fetch origin main ===", contents);
        Assert.Contains("git fetch \"%REMOTE%\" \"%BRANCH%\" > \"%TEEFILE%\" 2>&1", contents);
        Assert.Contains("=== git fetch origin main exit=%RC% ===", contents);
        Assert.Contains("=== git merge --ff-only FETCH_HEAD ===", contents);
        Assert.Contains("git merge --ff-only FETCH_HEAD > \"%TEEFILE%\" 2>&1", contents);
        Assert.Contains("=== git merge --ff-only FETCH_HEAD exit=%RC% ===", contents);
        // dotnet publish wraps the csproj path in quotes and writes into
        // a temp staging folder pointed at by %STAGE%.
        Assert.Contains("dotnet publish \"", contents);
        Assert.Contains("--no-self-contained --nologo -o \"%STAGE%\" > \"%TEEFILE%\" 2>&1", contents);

        // After every teed command the script captures errorlevel and pipes
        // the captured output to both stdout and the app log. The new
        // pipeline runs more teed commands (status, fetch, merge, publish,
        // robocopy, clean) so we just require the minimum we had before.
        var teeOccurrences = CountOccurrences(contents, "type \"%TEEFILE%\" >> \"%LOG%\"");
        Assert.True(teeOccurrences >= 4,
            $"Expected at least 4 tee-to-log lines; found {teeOccurrences}.");

        // Step labels are set before each command so the FAILURE marker
        // identifies which step blew up.
        Assert.Contains("set \"STEP=git fetch\"", contents);
        Assert.Contains("set \"STEP=git fast-forward\"", contents);
        Assert.Contains("set \"STEP=dotnet publish\"", contents);
        Assert.Contains("set \"STEP=robocopy install\"", contents);

        // Every failure goes through the single :fail tail (no per-step
        // duplicate cleanup blocks) which writes the FAILURE end marker.
        var gotoFails = CountOccurrences(contents, "goto fail");
        Assert.True(gotoFails >= 5,
            $"Expected at least 5 goto fail branches; found {gotoFails}.");
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public async Task LaunchUpdate_ScriptEscapesParensInsideIfBlocks_RobocopyMessage()
    {
        // Regression for the silent-abort bug: a raw "(s)" inside a
        // parenthesised cmd IF block aborts cmd.exe with
        //   ". was unexpected at this time."
        // and the console closes BEFORE any failure marker reaches the log.
        // AppendLoggedLine must escape "(" and ")" to "^(" and "^)" for
        // indented (in-block) echoes so cmd's paren matcher does not
        // mis-detect the end of the surrounding block.
        var repoRoot = MakeValidRepoRoot("paren-escape");
        var tempRoot = Path.Combine(_scratchRoot, "tempdir-paren");
        var logRoot = Path.Combine(_scratchRoot, "logdir-paren");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(logRoot);

        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "0", behind: "1");

        var shell = new FakeShellLauncher { ReturnedPid = 7 };
        var service = new UpdateService(
            git, shell,
            executablePathProvider: () => Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
            currentProcessIdProvider: () => 1,
            tempPathProvider: () => tempRoot,
            logFolderProvider: () => logRoot,
            logger: _logger);

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "aaaaaaaa", RemoteSha: "bbbbbbbb",
                Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 1, CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
                Message: "x"),
            CancellationToken.None);

        Assert.True(result.Spawned);
        var contents = File.ReadAllText(shell.LastScriptPath!);

        // The robocopy warning lives inside an `if %RC% GEQ 8 (` block.
        // Its parentheses must be caret-escaped so cmd's parser keeps
        // counting the surrounding block correctly when it is skipped.
        Assert.Contains("Robocopy failed ^(exit %RC%^).", contents);
        Assert.DoesNotContain("Robocopy failed (exit %RC%).", contents);
        // Top-level (non-indented) lines like the begin marker should keep
        // their raw parens untouched - they are not inside any IF block.
        Assert.Contains("Update script begin (PID 1, branch main, from", contents);
    }

    [Fact]
    public async Task LaunchUpdate_ScriptSupportsProgramFilesX86StylePaths()
    {
        var repoRoot = MakeValidRepoRoot(
            Path.Combine("Program Files (x86)", "Developer Control Tower source"));
        var tempRoot = Path.Combine(_scratchRoot, "tempdir-paren2");
        var logRoot = Path.Combine(_scratchRoot, "logdir-paren2");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(logRoot);

        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "0", behind: "1");

        var shell = new FakeShellLauncher { ReturnedPid = 8 };
        var service = new UpdateService(
            git, shell,
            executablePathProvider: () => Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
            currentProcessIdProvider: () => 1,
            tempPathProvider: () => tempRoot,
            logFolderProvider: () => logRoot,
            logger: _logger);

        await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "aaaaaaaa", RemoteSha: "bbbbbbbb",
                Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 1, CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
                Message: "x"),
            CancellationToken.None);

        var contents = File.ReadAllText(shell.LastScriptPath!);
        Assert.Contains("set \"REPO=" + repoRoot + "\"", contents);
        Assert.Contains("set \"EXE=" + Path.Combine(repoRoot, "ControlTower.Desktop.exe") + "\"", contents);
        Assert.Contains("cd /d \"%REPO%\"", contents);
        Assert.Contains("start \"\" \"%EXE%\"", contents);
    }

    [Fact]
    public async Task LaunchUpdate_WritesScriptIntoPathWithSpacesSafely()
    {
        var repoRoot = MakeValidRepoRoot("path with space");
        var tempRoot = Path.Combine(_scratchRoot, "temp with space");
        var logRoot = Path.Combine(_scratchRoot, "log with space");
        Directory.CreateDirectory(tempRoot);
        Directory.CreateDirectory(logRoot);

        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "0", behind: "1");

        var shell = new FakeShellLauncher { ReturnedPid = 99 };
        var service = new UpdateService(
            git, shell,
            executablePathProvider: () => Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
            currentProcessIdProvider: () => 1,
            tempPathProvider: () => tempRoot,
            logFolderProvider: () => logRoot,
            logger: _logger);

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "aaaaaaaaaaaaaaaa", RemoteSha: "bbbbbbbbbbbbbbbb",
                Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 1, CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
                Message: "x"),
            CancellationToken.None);

        Assert.True(result.Spawned);
        Assert.NotNull(shell.LastScriptPath);
        Assert.Contains("temp with space", shell.LastScriptPath);

        var contents = File.ReadAllText(shell.LastScriptPath!);
        // Command-bound paths are stored with quoted SET syntax and consumed
        // through quoted variable expansion.
        Assert.Contains("set \"REPO=" + repoRoot + "\"", contents);
        Assert.Contains("cd /d \"%REPO%\"", contents);
        // The log folder embed and the LOG variable expansion must both be
        // quoted so a spaced log path works too.
        Assert.Contains("set \"LOG_DIR=" + logRoot + "\"", contents);
        Assert.Contains(">>\"%LOG%\" echo", contents);
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("remote")]
    [InlineData("repo")]
    [InlineData("exe")]
    [InlineData("log")]
    public void WriteUpdateScript_RejectsHostileInterpolatedValuesBeforeCreatingScript(
        string hostileField)
    {
        var repoRoot = Path.Combine(_scratchRoot, "safe-repo");
        var solutionPath = Path.Combine(repoRoot, "DeveloperControlTower.sln");
        var exePath = Path.Combine(_scratchRoot, "safe-install", "ControlTower.Desktop.exe");
        var branch = "main";
        var remote = "origin";
        var logFolder = Path.Combine(_scratchRoot, "safe-log");
        const string payload = "&echo owned";

        switch (hostileField)
        {
            case "branch":
                branch += payload;
                break;
            case "remote":
                remote += payload;
                break;
            case "repo":
                repoRoot += payload;
                solutionPath = Path.Combine(repoRoot, "DeveloperControlTower.sln");
                break;
            case "exe":
                exePath += payload;
                break;
            case "log":
                logFolder += payload;
                break;
        }

        var service = NewBareService();
        var ex = Assert.Throws<ArgumentException>(() => service.WriteUpdateScript(
            repoRoot,
            solutionPath,
            exePath,
            branch,
            remote,
            "aaaaaaaa",
            "bbbbbbbb",
            123,
            logFolder));

        Assert.Contains("unsafe", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(
            _scratchRoot,
            "*.cmd",
            SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("\0")]
    [InlineData("\r")]
    [InlineData("\n")]
    [InlineData("\"")]
    [InlineData("&")]
    [InlineData("|")]
    [InlineData("<")]
    [InlineData(">")]
    [InlineData("^")]
    [InlineData("%")]
    [InlineData("!")]
    public void WriteUpdateScript_RejectsEveryCmdControlCharacter(string unsafeValue)
    {
        var repoRoot = Path.Combine(_scratchRoot, "safe-repo");
        var service = NewBareService();

        Assert.Throws<ArgumentException>(() => service.WriteUpdateScript(
            repoRoot,
            Path.Combine(repoRoot, "DeveloperControlTower.sln"),
            Path.Combine(_scratchRoot, "safe-install", "ControlTower.Desktop.exe"),
            "main",
            "origin",
            "aaaaaaaa",
            "bbbbbbbb",
            123,
            Path.Combine(_scratchRoot, "log" + unsafeValue + "payload")));

        Assert.Empty(Directory.GetFiles(
            _scratchRoot,
            "*.cmd",
            SearchOption.AllDirectories));
    }

    [Theory]
    [InlineData("branch")]
    [InlineData("sha")]
    public async Task LaunchUpdate_HostileMetadataFailsVisiblyWithoutLaunching(
        string hostileField)
    {
        var repoRoot = MakeValidRepoRoot("hostile-branch");
        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "0", behind: "1");
        var shell = new FakeShellLauncher();
        var service = new UpdateService(
            git,
            shell,
            executablePathProvider: () => Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
            currentProcessIdProvider: () => 123,
            tempPathProvider: () => _scratchRoot,
            logFolderProvider: () => _scratchRoot,
            logger: _logger,
            installDirWritableProbe: _ => true);
        var configuredBranch = hostileField == "branch"
            ? "main&echo owned"
            : "main";
        var currentSha = hostileField == "sha"
            ? "aaaaaaaa&echo owned"
            : "aaaaaaaa";

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: currentSha,
                RemoteSha: "bbbbbbbb",
                Branch: "main",
                ConfiguredBranch: configuredBranch,
                CommitsBehind: 1,
                CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: Path.Combine(repoRoot, "ControlTower.Desktop.exe"),
                Message: "x"),
            CancellationToken.None);

        Assert.False(result.Spawned);
        Assert.Contains(
            hostileField == "branch" ? "branch" : "currentSha",
            result.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, shell.LaunchUpdateConsoleCalls);
        Assert.Equal(0, shell.LaunchUpdateConsoleElevatedCalls);
        Assert.Empty(Directory.GetFiles(
            _scratchRoot,
            "*.cmd",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task LaunchUpdate_AbortsWhenLocalIsAheadAfterRecheck()
    {
        var repoRoot = MakeValidRepoRoot("abort-ahead");
        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "2", behind: "3");

        var shell = new FakeShellLauncher();
        var service = NewService(git, shell);

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "a", RemoteSha: "b", Branch: "main", ConfiguredBranch: "main",
                CommitsBehind: 3, CommitsAhead: 0,
                RepoRoot: repoRoot, ExecutablePath: "ignored", Message: "x"),
            CancellationToken.None);

        Assert.False(result.Spawned);
        Assert.Contains("ahead", result.Message);
        // Neither the elevated nor the legacy console launcher should be
        // touched on early bail-out paths.
        Assert.Equal(0, shell.LaunchUpdateConsoleCalls);
        Assert.Equal(0, shell.LaunchUpdateConsoleElevatedCalls);
    }

    [Fact]
    public async Task LaunchUpdate_AbortsWhenCurrentBranchChangedAfterCheck()
    {
        var repoRoot = MakeValidRepoRoot("branch-changed");
        var git = new FakeGitAdapter();
        WireLaunchRecheck(
            git,
            ahead: "0",
            behind: "1",
            branch: "feature",
            upstream: "refs/remotes/origin/feature");
        var shell = new FakeShellLauncher();
        var service = NewService(git, shell);

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "aaaaaaaa",
                RemoteSha: "bbbbbbbb",
                Branch: "main",
                ConfiguredBranch: "main",
                CommitsBehind: 1,
                CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: "ignored",
                Message: "x"),
            CancellationToken.None);

        Assert.False(result.Spawned);
        Assert.Contains("changed", result.Message);
        Assert.Equal(0, shell.LaunchUpdateConsoleCalls);
        Assert.Equal(0, shell.LaunchUpdateConsoleElevatedCalls);
    }

    [Fact]
    public async Task LaunchUpdate_CommitCountFailureDoesNotLookUpToDate()
    {
        var repoRoot = MakeValidRepoRoot("count-recheck-failed");
        var git = new FakeGitAdapter();
        WireLaunchRecheck(git, ahead: "0", behind: "1");
        git.SetResponse(
            new[] { "rev-list", "@{upstream}..HEAD", "--count" },
            fail(128));
        var shell = new FakeShellLauncher();
        var service = NewService(git, shell);

        var result = await service.LaunchUpdateAsync(
            new UpdateCheckResult(
                Status: UpdateStatus.UpdateAvailable,
                CurrentSha: "aaaaaaaa",
                RemoteSha: "bbbbbbbb",
                Branch: "main",
                ConfiguredBranch: "main",
                CommitsBehind: 1,
                CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: "ignored",
                Message: "x"),
            CancellationToken.None);

        Assert.False(result.Spawned);
        Assert.Contains("safely re-check", result.Message);
        Assert.Equal(0, shell.LaunchUpdateConsoleCalls);
        Assert.Equal(0, shell.LaunchUpdateConsoleElevatedCalls);
    }

    // ---------- settings round trip ----------

    [Fact]
    public void ToolSettings_UpdatesBlockLoadsAndOverridesDefaults()
    {
        var tempDir = Path.Combine(_scratchRoot, "settings");
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        File.WriteAllText(settingsFile, @"updates:
  branch: develop
  auto_check_on_launch: false
  repo_root_override: 'C:\elsewhere\repo'
");

        var provider = new ToolSettingsProvider();
        var settings = provider.Load(settingsFile);

        Assert.Equal("develop", settings.UpdateOptions.Branch);
        Assert.False(settings.UpdateOptions.AutoCheckOnLaunch);
        Assert.Equal(@"C:\elsewhere\repo", settings.UpdateOptions.RepoRootOverride);
    }

    [Fact]
    public void ToolSettings_MissingUpdatesBlock_KeepsDefaults()
    {
        var tempDir = Path.Combine(_scratchRoot, "settings-default");
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        // No updates: block.
        File.WriteAllText(settingsFile, "kind: developer-control-tower/settings\n");

        var provider = new ToolSettingsProvider();
        var settings = provider.Load(settingsFile);

        Assert.Equal("main", settings.UpdateOptions.Branch);
        Assert.True(settings.UpdateOptions.AutoCheckOnLaunch);
        Assert.Equal(string.Empty, settings.UpdateOptions.RepoRootOverride);
    }

    // ---------- helpers ----------

    private string MakeValidRepoRoot(string name)
    {
        var path = Path.Combine(_scratchRoot, name);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        File.WriteAllText(Path.Combine(path, "DeveloperControlTower.sln"), "stub");
        return path;
    }

    private UpdateService NewService(FakeGitAdapter git, FakeShellLauncher? shell = null)
    {
        return new UpdateService(
            git, shell ?? new FakeShellLauncher(),
            executablePathProvider: () => Path.Combine(_scratchRoot, "ControlTower.Desktop.exe"),
            currentProcessIdProvider: () => 4242,
            tempPathProvider: () => _scratchRoot,
            logFolderProvider: () => _scratchRoot,
            logger: _logger);
    }

    private UpdateService NewBareService()
    {
        // 2-arg form retained for tests that only exercise TryResolveRepoRoot,
        // but routed through the injected sink so the real %LOCALAPPDATA%
        // app log is never touched even if a future log call is added.
        return new UpdateService(
            new FakeGitAdapter(), new FakeShellLauncher(),
            executablePathProvider: null,
            currentProcessIdProvider: null,
            tempPathProvider: null,
            logFolderProvider: () => _scratchRoot,
            logger: _logger);
    }

    /// <summary>
    /// Wires the standard happy-path responses: HEAD sha, branch=main,
    /// upstream=origin/main, fetch ok, remote sha. Ahead/behind counts
    /// must be wired separately by the test.
    /// </summary>
    private static void WireHappyPath(FakeGitAdapter git, string currentSha, string branch)
    {
        git.SetResponse(new[] { "rev-parse", "HEAD" }, ok(currentSha));
        git.SetResponse(new[] { "rev-parse", "--abbrev-ref", "HEAD" }, ok(branch));
        git.SetResponse(new[] { "rev-parse", "--symbolic-full-name", "@{upstream}" }, ok("refs/remotes/origin/main"));
        git.SetResponse(new[] { "fetch", "origin", "main" }, ok(string.Empty));
        git.SetResponse(new[] { "rev-parse", "@{upstream}" }, ok("ffffffffffffffff"));
    }

    private static void WireLaunchRecheck(
        FakeGitAdapter git,
        string ahead,
        string behind,
        string branch = "main",
        string upstream = "refs/remotes/origin/main")
    {
        git.SetResponse(new[] { "status", "--porcelain" }, ok(string.Empty));
        git.SetResponse(
            new[] { "symbolic-ref", "--quiet", "--short", "HEAD" },
            ok(branch));
        git.SetResponse(
            new[] { "rev-parse", "--symbolic-full-name", "@{upstream}" },
            ok(upstream));
        git.SetResponse(
            new[] { "rev-list", "@{upstream}..HEAD", "--count" },
            ok(ahead));
        git.SetResponse(
            new[] { "rev-list", "HEAD..@{upstream}", "--count" },
            ok(behind));
    }

    private static GitRunResult ok(string stdout) =>
        new GitRunResult(0, stdout, string.Empty, TimeSpan.FromMilliseconds(1), false, false);

    private static GitRunResult fail(int exit) =>
        new GitRunResult(exit, string.Empty, "boom", TimeSpan.FromMilliseconds(1), false, false);

    // ---------- fakes ----------

    private sealed class FakeGitAdapter : IGitProcessAdapter
    {
        private readonly Dictionary<string, GitRunResult> _byKey = new(StringComparer.Ordinal);
        public List<string[]> RecordedCalls { get; } = new();

        public void SetResponse(string[] args, GitRunResult result)
        {
            _byKey[Key(args)] = result;
        }

        public Task<GitRunResult> RunAsync(
            IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var arr = arguments.ToArray();
            RecordedCalls.Add(arr);
            if (_byKey.TryGetValue(Key(arr), out var hit))
            {
                return Task.FromResult(hit);
            }
            // Default to "command not stubbed" -> exit 1 so tests trip if
            // a code path drifts onto a request we didn't expect.
            return Task.FromResult(new GitRunResult(
                1, string.Empty, "not stubbed: " + Key(arr),
                TimeSpan.FromMilliseconds(1), false, false));
        }

        private static string Key(string[] args) => string.Join("\u0001", args);
    }

    private sealed class FakeShellLauncher : IShellLauncher
    {
        public int ReturnedPid { get; set; } = 12345;
        public int LaunchUpdateConsoleCalls { get; private set; }
        public int LaunchUpdateConsoleElevatedCalls { get; private set; }
        public string? LastScriptPath { get; private set; }

        public void Open(string pathOrUri) { /* not exercised */ }

        public int LaunchUpdateConsole(string scriptPath)
        {
            LaunchUpdateConsoleCalls++;
            LastScriptPath = scriptPath;
            return ReturnedPid;
        }

        public int LaunchUpdateConsoleElevated(string scriptPath)
        {
            LaunchUpdateConsoleElevatedCalls++;
            LastScriptPath = scriptPath;
            return ReturnedPid;
        }

        public int LaunchPowerShellScript(string scriptPath)
        {
            LastScriptPath = scriptPath;
            return ReturnedPid;
        }
    }
}
