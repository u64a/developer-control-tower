using System.IO;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Ssh;

namespace ControlTower.Tests;

public class SshConfigManagerInjectionTests
{
    private static string TempConfig() =>
        Path.Combine(Path.GetTempPath(), $"ssh-inj-{Path.GetRandomFileName()}", "config");

    [Fact]
    public void UpdateSshConfig_HostWithNewline_Rejected_NoWrite()
    {
        var path = TempConfig();
        try
        {
            var manager = new SshConfigManager(path);
            var stores = new[]
            {
                new RepoStore { Id = "evil\nHost rogue", Type = "ssh", Host = "1.2.3.4", User = "u" }
            };

            var ex = Assert.Throws<SshConfigValueException>(() => manager.UpdateSshConfig(stores));
            Assert.Contains("newline", ex.Reason);
            Assert.False(File.Exists(path), "config file must not be written when a value is invalid");
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateSshConfig_UserWithControlChar_Rejected_NoWrite()
    {
        var path = TempConfig();
        try
        {
            var manager = new SshConfigManager(path);
            var stores = new[]
            {
                new RepoStore { Id = "box", Type = "ssh", Host = "1.2.3.4", User = "user\u0007name" }
            };

            var ex = Assert.Throws<SshConfigValueException>(() => manager.UpdateSshConfig(stores));
            Assert.Contains("control", ex.Reason);
            Assert.False(File.Exists(path));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateSshConfig_HostnameWithQuote_Rejected_NoWrite()
    {
        var path = TempConfig();
        try
        {
            var manager = new SshConfigManager(path);
            var stores = new[]
            {
                new RepoStore { Id = "box", Type = "ssh", Host = "evil\"host", User = "u" }
            };

            Assert.Throws<SshConfigValueException>(() => manager.UpdateSshConfig(stores));
            Assert.False(File.Exists(path));
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateSshConfig_ValidEntries_Accepted()
    {
        var path = TempConfig();
        try
        {
            var manager = new SshConfigManager(path);
            var stores = new[]
            {
                new RepoStore { Id = "box1", Type = "ssh", Host = "1.2.3.4", User = "alice" },
                new RepoStore { Id = "box2", Type = "ssh", Host = "host.example.com", User = "bob" }
            };

            manager.UpdateSshConfig(stores);

            Assert.True(File.Exists(path));
            var content = File.ReadAllText(path);
            Assert.Contains("Host box1", content);
            Assert.Contains("Host box2", content);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void UpdateSshConfig_PartialMalformedBatch_LeavesExistingFileUnchanged()
    {
        var path = TempConfig();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "Host preserved\n  HostName keep.example\n");

            var manager = new SshConfigManager(path);
            var stores = new[]
            {
                new RepoStore { Id = "good", Type = "ssh", Host = "1.1.1.1", User = "u" },
                new RepoStore { Id = "bad\nHost rogue", Type = "ssh", Host = "2.2.2.2", User = "v" }
            };

            Assert.Throws<SshConfigValueException>(() => manager.UpdateSshConfig(stores));

            // Original content preserved; neither entry partially written.
            var content = File.ReadAllText(path);
            Assert.Contains("Host preserved", content);
            Assert.DoesNotContain("Host good", content);
            Assert.DoesNotContain("Host rogue", content);
        }
        finally
        {
            var dir = Path.GetDirectoryName(path);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
