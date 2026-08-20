using System.Collections.Generic;
using System.IO;
using System.Linq;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Registration;

namespace ControlTower.Tests;

public class ProjectCreationServiceTests
{
    [Fact]
    public void CreateProject_NullRequest_Fails()
    {
        var svc = BuildService(out _, out _);
        var result = svc.CreateProject(null);
        Assert.False(result.Success);
    }

    [Fact]
    public void CreateProject_MissingDisplayName_Fails()
    {
        var svc = BuildService(out _, out _);
        var result = svc.CreateProject(new ProjectCreationRequest { StoreId = "local" });
        Assert.False(result.Success);
        Assert.Contains("Display name", result.Message);
    }

    [Fact]
    public void CreateProject_UnknownStore_Fails()
    {
        var svc = BuildService(out _, out _);
        var result = svc.CreateProject(new ProjectCreationRequest
        {
            DisplayName = "Test",
            StoreId = "nonexistent"
        });
        Assert.False(result.Success);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public void CreateProject_Local_CreatesDirectoryAndRegisters()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dct_test_{Path.GetRandomFileName()}");
        try
        {
            var svc = BuildService(out var portfolioPath, out _, localRoot: tempRoot);
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "My Project",
                StoreId = "local"
            });

            Assert.True(result.Success);
            Assert.Equal("my-project", result.ProjectId);
            Assert.True(Directory.Exists(Path.Combine(tempRoot, "my-project")));
            Assert.True(File.Exists(portfolioPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CreateProject_Local_FolderExists_ReturnsExists()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dct_test_{Path.GetRandomFileName()}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "my-project"));

            var svc = BuildService(out _, out _, localRoot: tempRoot);
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "My Project",
                StoreId = "local"
            });

            Assert.False(result.Success);
            Assert.True(result.FolderAlreadyExists);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CreateProject_Local_AdoptExisting_Succeeds()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dct_test_{Path.GetRandomFileName()}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "my-project"));

            var svc = BuildService(out _, out _, localRoot: tempRoot);
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "My Project",
                StoreId = "local",
                AdoptExisting = true
            });

            Assert.True(result.Success);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CreateProject_Local_CustomFolder()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dct_test_{Path.GetRandomFileName()}");
        try
        {
            var svc = BuildService(out _, out _, localRoot: tempRoot);
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "My Project",
                StoreId = "local",
                Folder = "custom-dir"
            });

            Assert.True(result.Success);
            Assert.True(Directory.Exists(Path.Combine(tempRoot, "custom-dir")));
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Theory]
    [InlineData("active")]
    [InlineData("incubating")]
    [InlineData("parked")]
    public void CreateProject_Local_PersistsLifecycleState(string lifecycle)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dct_test_{Path.GetRandomFileName()}");
        try
        {
            var svc = BuildService(out var portfolioPath, out _, localRoot: tempRoot);
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Lifecycle Project",
                StoreId = "local",
                LifecycleState = lifecycle
            });

            Assert.True(result.Success);

            // project.yml is written to the central stub (not the repo) and must
            // carry the requested lifecycle_state, not a default.
            var stub = Path.Combine(
                Path.GetDirectoryName(portfolioPath)!, "portfolio-projects", result.ProjectId);
            var yamlPath = Path.Combine(stub, ".controltower", "project.yml");
            Assert.True(File.Exists(yamlPath));
            var yaml = File.ReadAllText(yamlPath);
            Assert.Contains("lifecycle_state: '" + lifecycle + "'", yaml);

            // Nothing should be written into the repo working tree.
            Assert.False(Directory.Exists(Path.Combine(result.ResolvedPath, ".controltower")));

            // And it should round-trip through the YAML provider unchanged.
            var reload = new ControlTower.Infrastructure.Yaml.ProjectYamlProvider()
                .LoadProject(stub);
            Assert.Equal(lifecycle, reload.Project.LifecycleState);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CreateProject_Local_AdoptExisting_PreservesLifecycleChange()
    {
        // Simulates the Edit flow: an existing project on disk is re-registered
        // with a new LifecycleState, and the new value must overwrite the old one.
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dct_test_{Path.GetRandomFileName()}");
        try
        {
            var svc = BuildService(out var portfolioPath, out _, localRoot: tempRoot);

            var initial = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Edited Project",
                StoreId = "local",
                LifecycleState = "incubating"
            });
            Assert.True(initial.Success);

            var edited = svc.CreateProject(new ProjectCreationRequest
            {
                ProjectId = initial.ProjectId,
                DisplayName = "Edited Project",
                StoreId = "local",
                LifecycleState = "parked",
                AdoptExisting = true
            });
            Assert.True(edited.Success);

            var stub = Path.Combine(
                Path.GetDirectoryName(portfolioPath)!, "portfolio-projects", edited.ProjectId);
            var reload = new ControlTower.Infrastructure.Yaml.ProjectYamlProvider()
                .LoadProject(stub);
            Assert.Equal("parked", reload.Project.LifecycleState);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CreateProject_Local_MissingLifecycle_DefaultsToActive()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dct_test_{Path.GetRandomFileName()}");
        try
        {
            var svc = BuildService(out var portfolioPath, out _, localRoot: tempRoot);
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Default Lifecycle",
                StoreId = "local",
                LifecycleState = "   "
            });

            Assert.True(result.Success);
            var stub = Path.Combine(
                Path.GetDirectoryName(portfolioPath)!, "portfolio-projects", result.ProjectId);
            var reload = new ControlTower.Infrastructure.Yaml.ProjectYamlProvider()
                .LoadProject(stub);
            Assert.Equal("active", reload.Project.LifecycleState);
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_NoCredential_Fails()
    {
        var svc = BuildService(out _, out _, includeSsh: true, sshPassword: "");
        var result = svc.CreateProject(new ProjectCreationRequest
        {
            DisplayName = "Remote Project",
            StoreId = "devbox"
        });

        Assert.False(result.Success);
        Assert.Contains("credential", result.Message, System.StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("..\\evil")]
    [InlineData("../evil")]
    [InlineData("evil/sub")]
    [InlineData("evil\\sub")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("evil; rm -rf /")]
    [InlineData("evil\" & calc & echo \"")]
    [InlineData("")]
    [InlineData(".dotfile")]
    [InlineData("name with spaces")]
    public void CreateProject_RejectsUnsafeFolderNames(string folder)
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"dct_test_{Path.GetRandomFileName()}");
        try
        {
            var svc = BuildService(out _, out _, localRoot: tempRoot);
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "local",
                Folder = folder
            });

            // Empty folder is allowed (falls back to projectId), so it should succeed.
            // Everything else should be rejected.
            if (string.IsNullOrEmpty(folder))
            {
                Assert.True(result.Success);
            }
            else
            {
                Assert.False(result.Success);
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static ProjectCreationService BuildService(
        out string portfolioPath,
        out string tempDir,
        string localRoot = "",
        bool includeSsh = false,
        string sshPassword = "")
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"dct_pf_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        portfolioPath = Path.Combine(tempDir, "portfolio.yml");

        if (string.IsNullOrEmpty(localRoot))
        {
            localRoot = Path.Combine(Path.GetTempPath(), $"dct_store_{Path.GetRandomFileName()}");
        }

        var stores = new List<RepoStore>
        {
            new RepoStore { Id = "local", Type = "local", Root = localRoot }
        };

        if (includeSsh)
        {
            stores.Add(new RepoStore
            {
                Id = "devbox",
                Type = "ssh",
                Host = "192.168.64.10",
                User = "devuser",
                Root = @"D:\repos",
                CredentialTarget = "DCT-SSH-devbox"
            });
        }

        var storeProvider = new StoreProvider(stores);
        var regService = new ProjectRegistrationService(portfolioPath, storeProvider);
        var sshService = new FakeSshService();
        var credStore = new FakeCredentialStore(sshPassword);

        return new ProjectCreationService(storeProvider, regService, sshService, credStore);
    }

    private sealed class FakeSshService : ISshService
    {
        /// <summary>
        /// When true, echo %OS% returns "Windows_NT" and uname -s fails (Windows cmd).
        /// When false, echo %OS% returns "%OS%" (unexpanded) and uname -s returns "Linux" (POSIX).
        /// </summary>
        public bool IsWindowsRemote { get; set; }

        /// <summary>When false, the 'echo %OS%' probe returns a failure result.</summary>
        public bool OsProbeSucceeds { get; set; } = true;

        /// <summary>Controls the output returned by the existence check command.</summary>
        public ExistenceProbeOutputKind ExistenceOutput { get; set; } = ExistenceProbeOutputKind.NotFound;

        /// <summary>When false, the existence check command returns SSH failure.</summary>
        public bool ExistenceProbeSucceeds { get; set; } = true;

        /// <summary>When false, the git init command returns an SSH failure.</summary>
        public bool InitSucceeds { get; set; } = true;

        /// <summary>
        /// When non-null, overrides ExistenceOutput and returns this exact string as the
        /// existence probe output. Use for testing prefix, suffix, and mixed-token rejection.
        /// </summary>
        public string? CustomProbeOutput { get; set; }

        public int TestConnectionCount { get; private set; }
        public int CreateDirectoryCount { get; private set; }
        public int RunCommandCount { get; private set; }
        public int GitInitCallCount { get; private set; }

        private readonly List<string> _issuedRunCommands = new();
        /// <summary>All commands passed to RunCommand in call order.</summary>
        public IReadOnlyList<string> IssuedRunCommands => _issuedRunCommands;
        /// <summary>
        /// Count of mkdir commands issued via RunCommand using the verified OS classification.
        /// Matches both Windows ("if not exist … mkdir …") and POSIX ("mkdir -p …") forms.
        /// </summary>
        public int MkdirCallCount => _issuedRunCommands.Count(c =>
            c.StartsWith("if not exist ", StringComparison.Ordinal) ||
            c.StartsWith("mkdir -p ", StringComparison.Ordinal));

        public enum ExistenceProbeOutputKind { NotFound, Exists, Ambiguous }

        public SshResult TestConnection(string host, int port, string user, string password)
        {
            TestConnectionCount++;
            return SshResult.Ok("Connected");
        }

        public SshResult CreateDirectory(string host, int port, string user, string password, string remotePath)
        {
            CreateDirectoryCount++;
            return SshResult.Ok();
        }

        public SshResult RunCommand(string host, int port, string user, string password, string command)
        {
            RunCommandCount++;
            _issuedRunCommands.Add(command);

            if (command == "echo %OS%")
            {
                if (!OsProbeSucceeds)
                    return SshResult.Fail("Connection lost during OS probe");
                return SshResult.Ok(IsWindowsRemote ? "Windows_NT" : "%OS%");
            }

            if (command == "uname -s")
            {
                // Windows remotes don't have uname; POSIX remotes do.
                return IsWindowsRemote
                    ? SshResult.Fail("'uname' is not recognized as an internal or external command")
                    : SshResult.Ok("Linux");
            }

            // Existence probe: detected by the echo EXISTS / NOTFOUND tokens in the command.
            if (command.Contains("echo EXISTS") || command.Contains("echo NOTFOUND"))
            {
                if (!ExistenceProbeSucceeds)
                    return SshResult.Fail("SSH connection failed during existence check");
                if (CustomProbeOutput != null)
                    return SshResult.Ok(CustomProbeOutput);
                return ExistenceOutput switch
                {
                    ExistenceProbeOutputKind.Exists   => SshResult.Ok("EXISTS"),
                    ExistenceProbeOutputKind.NotFound => SshResult.Ok("NOTFOUND"),
                    _                                 => SshResult.Ok("AMBIGUOUS-OUTPUT")
                };
            }

            // git init command
            if (command.Contains("git init"))
            {
                GitInitCallCount++;
                return InitSucceeds
                    ? SshResult.Ok("Initialized empty Git repository")
                    : SshResult.Fail("git init failed: permission denied");
            }

            return SshResult.Ok("OK");
        }
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        private readonly string _password;

        public FakeCredentialStore(string password) => _password = password;

        public string GetPassword(string target) => _password ?? string.Empty;
        public void SetPassword(string target, string password) { }
        public void DeletePassword(string target) { }
    }

    // ---- SSH OS-aware path construction and round-trip tests ----

    private static ProjectCreationService BuildSshService(
        out string portfolioPath,
        out string tempDir,
        out FakeSshService sshFake,
        string sshRoot = @"D:\repos",
        bool isWindowsRemote = true)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"dct_pf_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        portfolioPath = Path.Combine(tempDir, "portfolio.yml");

        var stores = new List<RepoStore>
        {
            new RepoStore { Id = "local", Type = "local", Root = Path.Combine(Path.GetTempPath(), $"dct_store_{Path.GetRandomFileName()}") },
            new RepoStore
            {
                Id = "devbox",
                Type = "ssh",
                Host = "192.168.64.10",
                User = "devuser",
                Root = sshRoot,
                CredentialTarget = "DCT-SSH-devbox"
            }
        };

        var storeProvider = new StoreProvider(stores);
        var regService = new ProjectRegistrationService(portfolioPath, storeProvider);
        sshFake = new FakeSshService { IsWindowsRemote = isWindowsRemote };
        var credStore = new FakeCredentialStore("secret123");

        return new ProjectCreationService(storeProvider, regService, sshFake, credStore);
    }

    [Fact]
    public void CreateProject_Ssh_PosixRoundTrip_TargetResolvesBackToStore()
    {
        var svc = BuildSshService(out _, out var tempDir, out _,
            sshRoot: "/srv/repos", isWindowsRemote: false);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "My Project",
                StoreId = "devbox"
            });

            Assert.True(result.Success);

            // Round-trip: the ssh_target produced by creation must resolve back
            // through SshStoreResolver to the same store/folder.
            var stores = new[] { new RepoStore
            {
                Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = "/srv/repos", User = "devuser"
            }};

            Assert.True(SshStoreResolver.TryResolve(
                result.ResolvedPath, stores, out var storeId, out var folder));
            Assert.Equal("devbox", storeId);
            Assert.Equal("my-project", folder);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_WindowsRoundTrip_TargetResolvesBackToStore()
    {
        var svc = BuildSshService(out _, out var tempDir, out _,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "My Project",
                StoreId = "devbox"
            });

            Assert.True(result.Success);

            // Round-trip: the ssh_target produced by creation must resolve back
            // through SshStoreResolver to the same store/folder.
            var stores = new[] { new RepoStore
            {
                Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"D:\repos", User = "devuser"
            }};

            Assert.True(SshStoreResolver.TryResolve(
                result.ResolvedPath, stores, out var storeId, out var folder));
            Assert.Equal("devbox", storeId);
            Assert.Equal("my-project", folder);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_PosixFilesystemRoot_RoundTrip_ResolvesBackToStore()
    {
        // Production scenario: POSIX store with Root="/" (filesystem root).
        // ProjectCreationService creates "/folder" via OS probe; the resulting
        // ssh_target must resolve back through SshStoreResolver to exact StoreId/folder.
        var svc = BuildSshService(out _, out var tempDir, out _,
            sshRoot: "/", isWindowsRemote: false);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "My Project",
                StoreId = "devbox"
            });

            Assert.True(result.Success);
            // The resolved path must use POSIX separators under root "/".
            Assert.Contains("/my-project", result.ResolvedPath);

            // Round-trip: resolve the ssh_target back to store + folder.
            var stores = new[] { new RepoStore
            {
                Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = "/", User = "devuser"
            }};

            Assert.True(SshStoreResolver.TryResolve(
                result.ResolvedPath, stores, out var storeId, out var folder));
            Assert.Equal("devbox", storeId);
            Assert.Equal("my-project", folder);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- Ambiguous UPN/IPv6 fail-safe: reject before remote mutation ----

    [Fact]
    public void CreateProject_Ssh_UpnUser_FailsBeforeMutation()
    {
        var svc = BuildSshServiceWithCustomStore(out var tempDir,
            host: "192.168.64.10", user: "admin@corp.local", root: @"D:\repos");
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "custom"
            });

            Assert.False(result.Success);
            Assert.Contains("@", result.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_Ipv6Host_FailsBeforeMutation()
    {
        var svc = BuildSshServiceWithCustomStore(out var tempDir,
            host: "fe80::1", user: "dev", root: "/srv/repos");
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "custom"
            });

            Assert.False(result.Success);
            Assert.Contains(":", result.Message);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- Relative roots: creation round-trip for both path styles ----

    [Fact]
    public void CreateProject_Ssh_RelativeRootPosix_RejectsBeforeMutation()
    {
        // Relative roots cannot be resolved to an absolute remote path, so
        // BuildVsCodeSsh would produce "/repos/project" instead of the real path.
        // ProjectCreationService must reject them before any SSH interaction.
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: "repos", isWindowsRemote: false);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Proj",
                StoreId = "devbox"
            });

            Assert.False(result.Success);
            Assert.Contains("relative Root", result.Message, System.StringComparison.OrdinalIgnoreCase);
            // No SSH calls should occur before the relative-root rejection.
            Assert.Equal(0, sshFake.TestConnectionCount);
            Assert.Equal(0, sshFake.RunCommandCount);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_RelativeRootWindows_RejectsBeforeMutation()
    {
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: "repos", isWindowsRemote: true);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Proj",
                StoreId = "devbox"
            });

            Assert.False(result.Success);
            Assert.Contains("relative Root", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, sshFake.TestConnectionCount);
            Assert.Equal(0, sshFake.RunCommandCount);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static ProjectCreationService BuildSshServiceWithCustomStore(
        out string tempDir,
        string host, string user, string root)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"dct_pf_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        var portfolioPath = Path.Combine(tempDir, "portfolio.yml");

        var stores = new List<RepoStore>
        {
            new RepoStore { Id = "local", Type = "local", Root = Path.Combine(Path.GetTempPath(), $"dct_store_{Path.GetRandomFileName()}") },
            new RepoStore
            {
                Id = "custom",
                Type = "ssh",
                Host = host,
                User = user,
                Root = root,
                CredentialTarget = "DCT-SSH-custom"
            }
        };

        var storeProvider = new StoreProvider(stores);
        var regService = new ProjectRegistrationService(portfolioPath, storeProvider);
        var sshFake = new FakeSshService();
        var credStore = new FakeCredentialStore("secret123");

        return new ProjectCreationService(storeProvider, regService, sshFake, credStore);
    }

    private static ProjectCreationService BuildSshServiceWithCustomStore(
        out string tempDir,
        out FakeSshService sshFake,
        string host, string user, string root)
    {
        tempDir = Path.Combine(Path.GetTempPath(), $"dct_pf_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        var portfolioPath = Path.Combine(tempDir, "portfolio.yml");

        var stores = new List<RepoStore>
        {
            new RepoStore { Id = "local", Type = "local", Root = Path.Combine(Path.GetTempPath(), $"dct_store_{Path.GetRandomFileName()}") },
            new RepoStore
            {
                Id = "custom",
                Type = "ssh",
                Host = host,
                User = user,
                Root = root,
                CredentialTarget = "DCT-SSH-custom"
            }
        };

        var storeProvider = new StoreProvider(stores);
        var regService = new ProjectRegistrationService(portfolioPath, storeProvider);
        sshFake = new FakeSshService();
        var credStore = new FakeCredentialStore("secret123");

        return new ProjectCreationService(storeProvider, regService, sshFake, credStore);
    }

    // ---- Safety: blank Root rejects before any SSH interaction ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void CreateProject_Ssh_BlankRoot_FailsBeforeMutation(string? root)
    {
        var svc = BuildSshServiceWithCustomStore(out var tempDir, out var sshFake,
            host: "192.168.64.10", user: "dev", root: root ?? string.Empty);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "custom"
            });

            Assert.False(result.Success);
            Assert.Contains("blank Root", result.Message);
            Assert.Equal(0, sshFake.TestConnectionCount);
            Assert.Equal(0, sshFake.RunCommandCount);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- Safety: one-character Host rejects before any SSH interaction ----

    [Theory]
    [InlineData("x")]
    [InlineData("1")]
    [InlineData(" ")]
    public void CreateProject_Ssh_OneCharHost_FailsBeforeMutation(string host)
    {
        var svc = BuildSshServiceWithCustomStore(out var tempDir, out var sshFake,
            host: host, user: "dev", root: "/srv/repos");
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "custom"
            });

            Assert.False(result.Success);
            Assert.Contains("invalid Host", result.Message);
            Assert.Equal(0, sshFake.TestConnectionCount);
            Assert.Equal(0, sshFake.RunCommandCount);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- Safety: failed OS probe returns failure before directory mutation ----

    [Fact]
    public void CreateProject_Ssh_FailedOsProbe_FailsBeforeMutation()
    {
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: "/srv/repos", isWindowsRemote: false);
        sshFake.OsProbeSucceeds = false;
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "devbox"
            });

            Assert.False(result.Success);
            Assert.Contains("OS probe failed", result.Message);
            // OS probe itself runs (1 RunCommand), but no further mutation occurs.
            Assert.Equal(1, sshFake.RunCommandCount);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- POSIX: '%OS%' echo output is not classified as POSIX directly;
    //             uname -s confirmation is required. ----

    [Fact]
    public void CreateProject_Ssh_PosixRemote_UnameConfirmsPosixAndCreatesRoundTrip()
    {
        // The fake returns '%OS%' for 'echo %OS%' and 'Linux' for 'uname -s'.
        // The service must use uname -s to confirm POSIX — not treat the
        // literal '%OS%' echo output as sufficient evidence of POSIX.
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: "/srv/repos", isWindowsRemote: false);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Posix Proj",
                StoreId = "devbox"
            });

            Assert.True(result.Success);
            // Path uses POSIX forward slashes (not Windows backslashes).
            Assert.Contains("/srv/repos/posix-proj", result.ResolvedPath);
            // mkdir was issued via RunCommand using the POSIX classification (mkdir -p form).
            Assert.Equal(1, sshFake.MkdirCallCount);
            Assert.Contains(sshFake.IssuedRunCommands, c => c.StartsWith("mkdir -p ", StringComparison.Ordinal));

            // Round-trip through SshStoreResolver.
            var stores = new[] { new RepoStore
            {
                Id = "devbox", Type = "ssh", Host = "192.168.64.10",
                Root = "/srv/repos", User = "devuser"
            }};

            Assert.True(SshStoreResolver.TryResolve(
                result.ResolvedPath, stores, out var storeId, out var folder));
            Assert.Equal("devbox", storeId);
            Assert.Equal("posix-proj", folder);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- Finding 4: ambiguous OS (PowerShell-like: echo returns %OS%, uname fails) ----

    [Fact]
    public void CreateProject_Ssh_AmbiguousOs_PowerShellLike_RejectsBeforeMutation()
    {
        // Simulates a Windows/PowerShell remote where 'echo %OS%' returns the
        // literal '%OS%' (not 'Windows_NT') and 'uname -s' fails (not available).
        // The service must reject with "Cannot determine remote OS" before any mutation.
        var tempDir = Path.Combine(Path.GetTempPath(), $"dct_pf_{Path.GetRandomFileName()}");
        Directory.CreateDirectory(tempDir);
        var portfolioPath = Path.Combine(tempDir, "portfolio.yml");
        try
        {
            var stores = new List<RepoStore>
            {
                new RepoStore
                {
                    Id = "pwsh", Type = "ssh", Host = "192.168.64.10", User = "dev",
                    Root = "/srv/repos", CredentialTarget = "DCT-SSH-pwsh"
                }
            };
            var storeProvider = new StoreProvider(stores);
            var regService = new ProjectRegistrationService(portfolioPath, storeProvider);
            var sshFake = new AmbiguousOsFakeSshService();
            var svc = new ProjectCreationService(storeProvider, regService, sshFake, new FakeCredentialStore("secret123"));

            var result = svc.CreateProject(new ProjectCreationRequest { DisplayName = "Ambiguous", StoreId = "pwsh" });

            Assert.False(result.Success);
            Assert.Contains("Cannot determine remote OS", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(2, sshFake.RunCommandCount); // echo %OS% + uname -s; no further calls
            Assert.Equal(0, sshFake.CreateDirectoryCount);
            Assert.False(File.Exists(portfolioPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    /// <summary>
    /// Simulates a Windows/PowerShell remote: 'echo %%OS%%' returns literal '%%OS%%'
    /// (not 'Windows_NT') and 'uname -s' fails (command not found on Windows).
    /// </summary>
    private sealed class AmbiguousOsFakeSshService : ISshService
    {
        public int RunCommandCount { get; private set; }
        public int CreateDirectoryCount { get; private set; }

        public SshResult TestConnection(string host, int port, string user, string password)
            => SshResult.Ok("Connected");

        public SshResult CreateDirectory(string host, int port, string user, string password, string remotePath)
        {
            CreateDirectoryCount++;
            return SshResult.Ok();
        }

        public SshResult RunCommand(string host, int port, string user, string password, string command)
        {
            RunCommandCount++;
            if (command == "echo %OS%") return SshResult.Ok("%OS%");            // PowerShell echoes literally
            if (command == "uname -s")  return SshResult.Fail("'uname' is not recognized"); // not on Windows
            return SshResult.Ok("OK");
        }
    }

    // ---- Finding 1: existence probe safety ----

    [Fact]
    public void CreateProject_Ssh_ExistenceProbe_FailedSsh_AbortsBeforeMutation()
    {
        var svc = BuildSshService(out var portfolioPath, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        sshFake.ExistenceProbeSucceeds = false;
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "devbox"
            });

            Assert.False(result.Success);
            Assert.Contains("existence check failed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
            Assert.Equal(0, sshFake.GitInitCallCount);
            Assert.False(File.Exists(portfolioPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_ExistenceProbe_AmbiguousOutput_AbortsBeforeMutation()
    {
        var svc = BuildSshService(out var portfolioPath, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        sshFake.ExistenceOutput = FakeSshService.ExistenceProbeOutputKind.Ambiguous;
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "devbox"
            });

            Assert.False(result.Success);
            Assert.Contains("unexpected output", result.Message, System.StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
            Assert.Equal(0, sshFake.GitInitCallCount);
            Assert.False(File.Exists(portfolioPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_ExistenceProbe_ExistsToken_AdoptExisting_SkipsInitAndCreateDir()
    {
        // When the remote directory already exists and AdoptExisting = true,
        // no mkdir or git init should be performed.
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        sshFake.ExistenceOutput = FakeSshService.ExistenceProbeOutputKind.Exists;
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Adopt",
                StoreId = "devbox",
                AdoptExisting = true
            });

            Assert.True(result.Success);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
            Assert.Equal(0, sshFake.GitInitCallCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_ExistenceProbe_ExistsToken_NoAdopt_ReturnsExistsResult()
    {
        // Exact 'EXISTS' without AdoptExisting must return FolderAlreadyExists, not mutate.
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        sshFake.ExistenceOutput = FakeSshService.ExistenceProbeOutputKind.Exists;
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Exists",
                StoreId = "devbox",
                AdoptExisting = false
            });

            Assert.False(result.Success);
            Assert.True(result.FolderAlreadyExists);
            Assert.Equal(0, sshFake.CreateDirectoryCount);
            Assert.Equal(0, sshFake.GitInitCallCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_ExistenceProbe_NotFoundToken_AllowsCreation()
    {
        // Exact 'NOTFOUND' is the only token that allows mkdir+init.
        var svc = BuildSshService(out var portfolioPath, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        // ExistenceOutput defaults to NotFound
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "New Proj",
                StoreId = "devbox"
            });

            Assert.True(result.Success);
            Assert.Equal(1, sshFake.MkdirCallCount);
            Assert.Equal(1, sshFake.GitInitCallCount);
            Assert.True(File.Exists(portfolioPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_FailedInit_AbortsRegistrationAfterMkdir()
    {
        // If git init fails, registration must be aborted even though the
        // directory was already created on the remote.
        var svc = BuildSshService(out var portfolioPath, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        sshFake.InitSucceeds = false;
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Init Fail",
                StoreId = "devbox"
            });

            Assert.False(result.Success);
            Assert.Contains("git init failed", result.Message, System.StringComparison.OrdinalIgnoreCase);
            // Directory was created (mkdir via RunCommand) but registration was aborted.
            Assert.Equal(1, sshFake.MkdirCallCount);
            Assert.Equal(1, sshFake.GitInitCallCount);
            Assert.False(File.Exists(portfolioPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- Finding 1 (regression): exact-token probe — mixed/polluted output must abort ----

    [Theory]
    [InlineData("EXISTS\nNOTFOUND")]   // mixed tokens: the AdoptExisting=true bypass vector
    [InlineData("NOTFOUND_EXTRA")]     // suffix after valid token
    [InlineData("PREFIX_EXISTS")]      // prefix before valid token
    [InlineData("")]                   // empty output
    [InlineData("   ")]                // whitespace-only output
    public void CreateProject_Ssh_ExistenceProbe_NonExactToken_AbortsBeforeMutation(string probeOutput)
    {
        // AdoptExisting=true is used to expose the worst-case bypass:
        // with the old Contains() matching, "EXISTS\nNOTFOUND" set both exists and
        // notFound to true — the exists branch was skipped (AdoptExisting=true) and
        // the notFound branch executed mkdir+init. Exact-token comparison closes this.
        var svc = BuildSshService(out var portfolioPath, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        sshFake.CustomProbeOutput = probeOutput;
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Test",
                StoreId = "devbox",
                AdoptExisting = true
            });

            Assert.False(result.Success);
            Assert.Equal(0, sshFake.MkdirCallCount);
            Assert.Equal(0, sshFake.GitInitCallCount);
            Assert.False(File.Exists(portfolioPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    // ---- Compliance: mkdir uses verified OS classification, not a second probe ----

    [Fact]
    public void CreateProject_Ssh_WindowsRemote_MkdirUsesVerifiedWindowsClassification()
    {
        // Windows remote: the mkdir command issued via RunCommand must use Windows cmd
        // syntax ("if not exist … mkdir …"), not POSIX "mkdir -p", and must not
        // trigger a second "echo %OS%" probe.
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Win Proj",
                StoreId = "devbox"
            });

            Assert.True(result.Success);
            Assert.Equal(1, sshFake.MkdirCallCount);
            // Windows mkdir command must use "if not exist … mkdir …" form.
            Assert.Contains(sshFake.IssuedRunCommands, c =>
                c.StartsWith("if not exist ", StringComparison.Ordinal));
            // POSIX "mkdir -p" must NOT appear.
            Assert.DoesNotContain(sshFake.IssuedRunCommands, c =>
                c.StartsWith("mkdir -p ", StringComparison.Ordinal));
            // Only one "echo %OS%" probe total — no second probe during mkdir.
            Assert.Equal(1, sshFake.IssuedRunCommands.Count(c => c == "echo %OS%"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_PosixRemote_MkdirUsesVerifiedPosixClassification()
    {
        // POSIX remote: the mkdir command issued via RunCommand must use POSIX
        // syntax ("mkdir -p …"), not Windows "if not exist … mkdir …", and must
        // not trigger a second "echo %OS%" probe.
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: "/srv/repos", isWindowsRemote: false);
        try
        {
            var result = svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "Posix Proj2",
                StoreId = "devbox"
            });

            Assert.True(result.Success);
            Assert.Equal(1, sshFake.MkdirCallCount);
            // POSIX mkdir command must use "mkdir -p …" form.
            Assert.Contains(sshFake.IssuedRunCommands, c =>
                c.StartsWith("mkdir -p ", StringComparison.Ordinal));
            // Windows "if not exist" must NOT appear.
            Assert.DoesNotContain(sshFake.IssuedRunCommands, c =>
                c.StartsWith("if not exist ", StringComparison.Ordinal));
            // Only one "echo %OS%" probe total (no second probe during mkdir).
            Assert.Equal(1, sshFake.IssuedRunCommands.Count(c => c == "echo %OS%"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void CreateProject_Ssh_Mkdir_NoSecondOsProbeIssued_WindowsRemote()
    {
        // Directly verifies the compliance invariant: echo %OS% appears exactly once
        // across the entire workflow (OS detection only), not again during mkdir.
        var svc = BuildSshService(out _, out var tempDir, out var sshFake,
            sshRoot: @"D:\repos", isWindowsRemote: true);
        try
        {
            svc.CreateProject(new ProjectCreationRequest
            {
                DisplayName = "No Re-probe",
                StoreId = "devbox"
            });

            var osProbeCount = sshFake.IssuedRunCommands.Count(c => c == "echo %OS%");
            Assert.Equal(1, osProbeCount);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }
}