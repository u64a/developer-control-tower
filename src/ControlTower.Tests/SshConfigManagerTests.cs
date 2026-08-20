using System.IO;
using System.Linq;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Ssh;

namespace ControlTower.Tests;

public class SshConfigManagerTests
{
    [Fact]
    public void UpdateSshConfig_CreatesNewFile_WithManagedBlock()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ssh_test_{Path.GetRandomFileName()}", "config");
        try
        {
            var manager = new SshConfigManager(configPath);
            var stores = new[]
            {
                new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", User = "devuser" }
            };

            manager.UpdateSshConfig(stores);

            Assert.True(File.Exists(configPath));
            var content = File.ReadAllText(configPath);
            Assert.Contains("# --- Developer Control Tower managed ---", content);
            Assert.Contains("Host devbox", content);
            Assert.Contains("HostName 192.168.64.10", content);
            Assert.Contains("User devuser", content);
            Assert.Contains("# --- End Developer Control Tower ---", content);
        }
        finally
        {
            var dir = Path.GetDirectoryName(configPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateSshConfig_PreservesExistingEntries()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ssh_test_{Path.GetRandomFileName()}", "config");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, "Host myserver\n  HostName example.com\n  User admin\n");

            var manager = new SshConfigManager(configPath);
            var stores = new[]
            {
                new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", User = "devuser" }
            };

            manager.UpdateSshConfig(stores);

            var content = File.ReadAllText(configPath);
            Assert.Contains("Host myserver", content);
            Assert.Contains("HostName example.com", content);
            Assert.Contains("Host devbox", content);
        }
        finally
        {
            var dir = Path.GetDirectoryName(configPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateSshConfig_ReplacesManagedBlock_OnSecondRun()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ssh_test_{Path.GetRandomFileName()}", "config");
        try
        {
            var manager = new SshConfigManager(configPath);

            // First run
            manager.UpdateSshConfig(new[]
            {
                new RepoStore { Id = "old", Type = "ssh", Host = "1.1.1.1", User = "u" }
            });

            // Second run with different store
            manager.UpdateSshConfig(new[]
            {
                new RepoStore { Id = "new", Type = "ssh", Host = "2.2.2.2", User = "v" }
            });

            var content = File.ReadAllText(configPath);
            Assert.DoesNotContain("Host old", content);
            Assert.Contains("Host new", content);
            Assert.Contains("HostName 2.2.2.2", content);
        }
        finally
        {
            var dir = Path.GetDirectoryName(configPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateSshConfig_IncludesCustomPort()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ssh_test_{Path.GetRandomFileName()}", "config");
        try
        {
            var manager = new SshConfigManager(configPath);
            var stores = new[]
            {
                new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", User = "devuser", Port = 2222 }
            };

            manager.UpdateSshConfig(stores);

            var content = File.ReadAllText(configPath);
            Assert.Contains("Port 2222", content);
        }
        finally
        {
            var dir = Path.GetDirectoryName(configPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateSshConfig_SkipsLocalStores()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ssh_test_{Path.GetRandomFileName()}", "config");
        try
        {
            var manager = new SshConfigManager(configPath);
            var stores = new[]
            {
                new RepoStore { Id = "local", Type = "local", Root = @"C:\Repos" }
            };

            manager.UpdateSshConfig(stores);

            // File created but no Host entries
            var content = File.Exists(configPath) ? File.ReadAllText(configPath) : "";
            Assert.DoesNotContain("Host local", content);
        }
        finally
        {
            var dir = Path.GetDirectoryName(configPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetManagedHosts_ReturnsManagedHosts()
    {
        var configPath = Path.Combine(Path.GetTempPath(), $"ssh_test_{Path.GetRandomFileName()}", "config");
        try
        {
            var manager = new SshConfigManager(configPath);
            manager.UpdateSshConfig(new[]
            {
                new RepoStore { Id = "box1", Type = "ssh", Host = "1.1.1.1", User = "u" },
                new RepoStore { Id = "box2", Type = "ssh", Host = "2.2.2.2", User = "v" }
            });

            var hosts = manager.GetManagedHosts();
            Assert.Equal(2, hosts.Count);
            Assert.Contains("box1", hosts);
            Assert.Contains("box2", hosts);
        }
        finally
        {
            var dir = Path.GetDirectoryName(configPath);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void GetManagedHosts_MissingFile_ReturnsEmpty()
    {
        var manager = new SshConfigManager(@"C:\nonexistent\path\config");
        var hosts = manager.GetManagedHosts();
        Assert.Empty(hosts);
    }
}
