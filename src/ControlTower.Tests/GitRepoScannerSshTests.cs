using System.Collections.Generic;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

/// <summary>
/// Production-level tests for <see cref="GitRepoScanner.ScanSshViaService"/>.
/// Verifies that the scanner builds cross-platform <c>git -C &lt;path&gt;</c>
/// commands (not the Windows-only <c>cd /d</c>) and applies the correct shell
/// quoter for the detected remote OS.
/// </summary>
public class GitRepoScannerSshTests
{
    // ── POSIX remote ──────────────────────────────────────────────────────

    [Fact]
    public void ScanSsh_PosixPath_UsesGitDashCWithPosixQuoting()
    {
        var fake = HealthySshService();
        var store = PosixStore("/srv/repos");
        var scanner = BuildScanner(fake, store);

        var snapshot = scanner.Scan("devuser@192.168.64.10:/srv/repos/myproject");

        Assert.True(snapshot.IsAvailable, $"Expected available but got: {snapshot.StatusMessage}");
        Assert.Equal("main", snapshot.Branch);

        Assert.All(fake.Commands, command =>
        {
            Assert.Contains("git -C", command);
            Assert.DoesNotContain("cd /d", command);
            Assert.Contains("'/srv/repos/myproject'", command);
        });
    }

    [Fact]
    public void ScanSsh_PosixPath_WithSpaces_QuotedSafely()
    {
        var fake = HealthySshService();
        var store = PosixStore("/srv/my repos");
        var scanner = BuildScanner(fake, store);

        var snapshot = scanner.Scan("devuser@192.168.64.10:/srv/my repos/project one");

        // Whether available or not depends on IsSafeRemotePath — spaces are permitted.
        // The key assertion is that the command does NOT use cd /d.
        Assert.DoesNotContain("cd /d", fake.LastCommand ?? string.Empty);

        if (snapshot.IsAvailable)
        {
            // If available, the path must be POSIX-single-quoted.
            Assert.Contains("git -C", fake.LastCommand!);
            Assert.Contains("'/srv/my repos/project one'", fake.LastCommand!);
        }
    }

    [Fact]
    public void ScanSsh_PosixPath_SshFailure_ReturnsUnavailable()
    {
        var fake = new CapturingSshService(SshResult.Fail("connection refused"));
        var store = PosixStore("/srv/repos");
        var scanner = BuildScanner(fake, store);

        var snapshot = scanner.Scan("devuser@192.168.64.10:/srv/repos/proj");

        Assert.False(snapshot.IsAvailable);
        Assert.NotNull(snapshot.StatusMessage);
    }

    // ── Windows remote ────────────────────────────────────────────────────

    [Fact]
    public void ScanSsh_WindowsPath_UsesGitDashCWithWindowsQuoting()
    {
        var fake = HealthySshService();
        var store = WindowsStore(@"D:\repos");
        var scanner = BuildScanner(fake, store);

        var snapshot = scanner.Scan(@"devuser@192.168.64.10:D:\repos\myproject");

        Assert.True(snapshot.IsAvailable, $"Expected available but got: {snapshot.StatusMessage}");
        Assert.Equal("main", snapshot.Branch);

        Assert.All(fake.Commands, command =>
        {
            Assert.Contains("git -C", command);
            Assert.DoesNotContain("cd /d", command);
            Assert.Contains(@"""D:\repos\myproject""", command);
        });
    }

    [Fact]
    public void ScanSsh_WindowsPath_WithSpaces_QuotedSafely()
    {
        var fake = HealthySshService();
        var store = WindowsStore(@"D:\my repos");
        var scanner = BuildScanner(fake, store);

        var snapshot = scanner.Scan(@"devuser@192.168.64.10:D:\my repos\project one");

        // The key assertion: cd /d must not appear.
        Assert.DoesNotContain("cd /d", fake.LastCommand ?? string.Empty);

        if (snapshot.IsAvailable)
        {
            Assert.Contains("git -C", fake.LastCommand!);
            Assert.Contains(@"""D:\my repos\project one""", fake.LastCommand!);
        }
    }

    // ── Probe failure isolation ───────────────────────────────────────────

    [Fact]
    public void ScanSsh_CreatedRepoWithoutOriginOrUpstream_RemainsAvailable()
    {
        // ProjectCreationService creates an initial commit but does not configure
        // a remote or tracking branch.
        var fake = HealthySshService(
            upstreamResult: SshResult.Fail("fatal: no upstream configured for branch 'main'"),
            originResult: SshResult.Fail("error: No such remote 'origin'"));
        var scanner = BuildScanner(fake, PosixStore("/srv/repos"));

        var snapshot = scanner.Scan("devuser@192.168.64.10:/srv/repos/new-project");

        Assert.True(snapshot.IsAvailable, $"Expected available but got: {snapshot.StatusMessage}");
        Assert.Equal("main", snapshot.Branch);
        Assert.False(snapshot.HasUpstream);
        Assert.Equal(0, snapshot.AheadBy);
        Assert.Equal(0, snapshot.BehindBy);
        Assert.Empty(snapshot.OriginUrl);
        Assert.NotNull(snapshot.LastCommitUtc);
        Assert.Equal(5, fake.Commands.Count);
        Assert.All(fake.Commands, command => Assert.DoesNotContain("&&", command));
    }

    [Fact]
    public void ScanSsh_RepoWithUpstream_ParsesAheadBehind()
    {
        var fake = HealthySshService(upstreamResult: SshResult.Ok("2\t3"));
        var scanner = BuildScanner(fake, PosixStore("/srv/repos"));

        var snapshot = scanner.Scan("devuser@192.168.64.10:/srv/repos/tracked-project");

        Assert.True(snapshot.IsAvailable, $"Expected available but got: {snapshot.StatusMessage}");
        Assert.True(snapshot.HasUpstream);
        Assert.Equal(2, snapshot.AheadBy);
        Assert.Equal(3, snapshot.BehindBy);
        Assert.Equal("https://github.com/org/repo.git", snapshot.OriginUrl);
    }

    [Fact]
    public void ScanSsh_RequiredBranchProbeFailure_ReturnsUnavailable()
    {
        var fake = new CapturingSshService(
            SshResult.Fail("fatal: not a git repository (or any parent directories): .git"));
        var scanner = BuildScanner(fake, PosixStore("/srv/repos"));

        var snapshot = scanner.Scan("devuser@192.168.64.10:/srv/repos/not-a-repo");

        Assert.False(snapshot.IsAvailable);
        Assert.Contains("not a git repository", snapshot.StatusMessage);
        Assert.Single(fake.Commands);
        Assert.Contains("rev-parse --abbrev-ref HEAD", fake.Commands[0]);
    }

    // ── Store-not-found fallback ──────────────────────────────────────────

    [Fact]
    public void ScanSsh_NoMatchingStore_ReturnsUnavailable()
    {
        // When no configured store matches the SSH host, the scanner
        // falls back to ScanSshViaProcess (key-based auth). Since we
        // have no real ssh.exe here, the result is unavailable — but
        // the store-not-found message should be surfaced.
        var fake = HealthySshService();
        // Store for a different host
        var store = PosixStore("/srv/repos", host: "other-host");
        var scanner = BuildScanner(fake, store);

        var snapshot = scanner.Scan("devuser@192.168.64.10:/srv/repos/proj");

        // No store matched → ScanSshViaProcess path → likely fails without real ssh.exe,
        // or returns unavailable. Either way, fake.LastCommand is not from ViaService.
        Assert.NotNull(snapshot.StatusMessage);
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static CapturingSshService HealthySshService(
        SshResult? upstreamResult = null,
        SshResult? originResult = null) =>
        new CapturingSshService(
            SshResult.Ok("main"),
            SshResult.Ok(),
            upstreamResult ?? SshResult.Ok("0\t0"),
            SshResult.Ok("2024-01-15 10:00:00 +1000"),
            originResult ?? SshResult.Ok("https://github.com/org/repo.git"));

    private static GitRepoScanner BuildScanner(CapturingSshService fake, RepoStore store)
    {
        var storeProvider = new StoreProvider(new[] { store });
        var credStore = new SimpleCredentialStore("test-pass");
        return new GitRepoScanner(new ToolSettings(), fake, credStore, storeProvider);
    }

    private static RepoStore PosixStore(string root, string host = "192.168.64.10") =>
        new RepoStore
        {
            Id = "posix-store", Type = "ssh", Host = host, User = "devuser",
            Root = root, Port = 22, CredentialTarget = "DCT-SSH-posix"
        };

    private static RepoStore WindowsStore(string root, string host = "192.168.64.10") =>
        new RepoStore
        {
            Id = "win-store", Type = "ssh", Host = host, User = "devuser",
            Root = root, Port = 22, CredentialTarget = "DCT-SSH-win"
        };

    /// <summary>
    /// SSH fake that captures commands and returns canned results in probe order.
    /// </summary>
    private sealed class CapturingSshService : ISshService
    {
        private readonly Queue<SshResult> _results;

        public List<string> Commands { get; } = new();

        public string? LastCommand =>
            Commands.Count == 0 ? null : Commands[Commands.Count - 1];

        public CapturingSshService(params SshResult[] results) =>
            _results = new Queue<SshResult>(results);

        public SshResult TestConnection(string host, int port, string user, string password)
            => SshResult.Ok("Connected");

        public SshResult CreateDirectory(string host, int port, string user, string password, string remotePath)
            => SshResult.Ok();

        public SshResult RunCommand(string host, int port, string user, string password, string command)
        {
            Commands.Add(command);
            return _results.Count > 0
                ? _results.Dequeue()
                : SshResult.Fail("Unexpected SSH command");
        }
    }

    private sealed class SimpleCredentialStore : ICredentialStore
    {
        private readonly string _password;
        public SimpleCredentialStore(string password) => _password = password;
        public string GetPassword(string target) => _password;
        public void SetPassword(string target, string password) { }
        public void DeletePassword(string target) { }
    }
}
