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
}
