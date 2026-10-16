using System;
using System.IO;
using System.Linq;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Tests;

public class ToolSettingsProviderTests
{
    [Fact]
    public void Load_LibraryPathOutsideAllowedRoots_RaisesIssueAndFallsBack()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            // C:\NotAUserDir is not under LocalAppData / RoamingAppData /
            // UserProfile / OneDrive, so the provider must refuse it.
            File.WriteAllText(settingsFile, @"library:
  path: C:\NotAUserDir\library
");

            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);

            Assert.Equal(string.Empty, settings.LibraryPath);
            Assert.Contains(settings.Issues, i => i.Code == "settings/path/outside-allowed-roots");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_LibraryPathInsideUserProfile_Accepted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            // Pick a path that's guaranteed to be inside the user profile.
            var allowed = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeveloperControlTower",
                "library");

            // Single-quoted YAML treats backslashes literally, so we write
            // the path verbatim without escaping.
            File.WriteAllText(settingsFile, $"library:\n  path: '{allowed}'\n");

            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);

            Assert.Equal(allowed, settings.LibraryPath);
            Assert.DoesNotContain(settings.Issues, i => i.Code == "settings/path/outside-allowed-roots");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_SshConfigPathOutsideAllowedRoots_RaisesIssue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            File.WriteAllText(settingsFile, @"tooling:
  ssh_config_path: C:\Windows\Temp\config
");
            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);

            Assert.Equal(string.Empty, settings.SshConfigPath);
            Assert.Contains(settings.Issues, i => i.Code == "settings/path/outside-allowed-roots");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_ArchiveStorageRootMissing_UsesProfileDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            File.WriteAllText(settingsFile, "kind: developer-control-tower/settings\nschema_version: 0\n");

            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);
            var expectedDefault = Path.GetFullPath(ArchiveStorageRootResolver.GetDefaultRoot());

            Assert.True(settings.ArchiveStorageRootIsValid);
            Assert.Equal(string.Empty, settings.ConfiguredArchiveStorageRootPath);
            Assert.Equal(expectedDefault, settings.ArchiveStorageRootPath);
            Assert.DoesNotContain(settings.Issues, i => i.Code == "settings/path/outside-allowed-roots");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_ArchiveStorageRootWhitespace_UsesProfileDefault()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            File.WriteAllText(settingsFile, "archive:\n  storage_root: '   '\n");

            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);
            var expectedDefault = Path.GetFullPath(ArchiveStorageRootResolver.GetDefaultRoot());

            Assert.True(settings.ArchiveStorageRootIsValid);
            Assert.Equal(string.Empty, settings.ConfiguredArchiveStorageRootPath);
            Assert.Equal(expectedDefault, settings.ArchiveStorageRootPath);
            Assert.DoesNotContain(settings.Issues, i => i.Code == "settings/path/outside-allowed-roots");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_ArchiveStorageRootInsideAllowedRoots_Accepted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            var configured = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeveloperControlTower",
                "archive-custom");
            File.WriteAllText(settingsFile, $"archive:\n  storage_root: '{configured}'\n");

            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);
            var expected = Path.GetFullPath(configured);

            Assert.True(settings.ArchiveStorageRootIsValid);
            Assert.Equal(expected, settings.ConfiguredArchiveStorageRootPath);
            Assert.Equal(expected, settings.ArchiveStorageRootPath);
            Assert.DoesNotContain(settings.Issues, i => i.Code == "settings/path/outside-allowed-roots");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_ArchiveStorageRootOutsideUserRoots_Accepted()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            var configured = @"C:\GitHub\Archive";
            File.WriteAllText(settingsFile, $"archive:\n  storage_root: '{configured}'\n");

            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);
            var expected = Path.GetFullPath(configured);

            Assert.True(settings.ArchiveStorageRootIsValid);
            Assert.Equal(expected, settings.ConfiguredArchiveStorageRootPath);
            Assert.Equal(expected, settings.ArchiveStorageRootPath);
            Assert.DoesNotContain(settings.Issues, i => i.Code == "settings/archive/storage-root-invalid");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_ArchiveStorageRootUnsafeNetworkPath_DisablesArchiveSyncUntilFixed()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            var configured = @"\\server\share\archive";
            File.WriteAllText(settingsFile, $"archive:\n  storage_root: '{configured}'\n");

            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);

            Assert.False(settings.ArchiveStorageRootIsValid);
            Assert.Equal(configured, settings.ConfiguredArchiveStorageRootPath);
            Assert.Equal(string.Empty, settings.ArchiveStorageRootPath);
            Assert.Contains(settings.Issues, i => i.Code == "settings/archive/storage-root-invalid");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_MalformedYamlWithEarlierValidArchiveSetting_DisablesArchiveSync()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            var validRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeveloperControlTower",
                "archive-valid-first");
            File.WriteAllText(
                settingsFile,
                "archive:\n" +
                $"  storage_root: '{validRoot}'\n" +
                "launch:\n" +
                "  default_environment: \"unterminated\n");

            var provider = new ToolSettingsProvider();
            var settings = provider.Load(settingsFile);

            Assert.False(settings.ArchiveStorageRootIsValid);
            Assert.Equal(string.Empty, settings.ArchiveStorageRootPath);
            Assert.Contains(settings.Issues, i => i.Code == "settings/yaml/malformed");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Load_AfterMalformedConfigIsCorrected_ArchiveSyncRecovers()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "ct-settings-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        var settingsFile = Path.Combine(tempDir, "settings.yml");
        try
        {
            File.WriteAllText(settingsFile, "archive:\n  storage_root: \"unterminated\n");

            var provider = new ToolSettingsProvider();
            var broken = provider.Load(settingsFile);
            Assert.False(broken.ArchiveStorageRootIsValid);
            Assert.Equal(string.Empty, broken.ArchiveStorageRootPath);
            Assert.Contains(broken.Issues, i => i.Code == "settings/yaml/malformed");

            File.WriteAllText(settingsFile, "kind: developer-control-tower/settings\nschema_version: 0\n");
            var recovered = provider.Load(settingsFile);
            var expectedDefault = Path.GetFullPath(ArchiveStorageRootResolver.GetDefaultRoot());

            Assert.True(recovered.ArchiveStorageRootIsValid);
            Assert.Equal(string.Empty, recovered.ConfiguredArchiveStorageRootPath);
            Assert.Equal(expectedDefault, recovered.ArchiveStorageRootPath);
            Assert.DoesNotContain(recovered.Issues, i => i.Code == "settings/yaml/malformed");
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
