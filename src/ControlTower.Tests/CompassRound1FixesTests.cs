using System.Diagnostics;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Launch;

namespace ControlTower.Tests;

/// <summary>
/// Tests pinning the behaviour changes from Compass round 1 (PR-A).
/// Each fact references the finding id it locks in.
/// </summary>
public class CompassRound1FixesTests
{
    // ---- H-01: no hardcoded path defaults for new local stores ------------

    [Fact]
    public void H01_RepoStoreDefaults_NewLocal_HasEmptyRoot()
    {
        var store = RepoStoreDefaults.NewLocal("local");

        Assert.Equal("local", store.Type);
        Assert.Equal("local", store.Id);
        Assert.Equal(string.Empty, store.Root);
    }

    [Fact]
    public void H01_RepoStoreDefaults_NewLocal_NeverYieldsCRepos()
    {
        // The previous code seeded Root = "C:\\Repos". Pin the regression
        // explicitly: no literal Windows path may slip back in.
        var store = RepoStoreDefaults.NewLocal("anything");

        Assert.DoesNotContain("C:\\", store.Root);
        Assert.DoesNotContain("Repos", store.Root);
    }

    [Fact]
    public void H01_RepoStoreDefaults_NewSsh_HasEmptyHostAndDefaultPort()
    {
        var store = RepoStoreDefaults.NewSsh("devbox");

        Assert.Equal("ssh", store.Type);
        Assert.Equal("devbox", store.Id);
        Assert.Equal(string.Empty, store.Host);
        Assert.Equal(string.Empty, store.User);
        Assert.Equal(22, store.Port);
        Assert.Equal("DCT-SSH-devbox", store.CredentialTarget);
    }

    // ---- H-04: subtitle reflects resolved path, not literal OneDrive ------

    [Fact]
    public void H04_SettingsSubtitle_IncludesResolvedPath_AndOmitsOneDriveClaim()
    {
        var path = @"D:\Profiles\example\OneDrive - Work\DeveloperControlTower\settings.yml";

        var text = SettingsSubtitleFormatter.Format(path);

        Assert.Contains(path, text);
        Assert.DoesNotContain("synced via OneDrive", text);
        Assert.DoesNotContain("Settings are synced", text);
    }

    [Fact]
    public void H04_SettingsSubtitle_EmptyPath_FallsBackGracefully()
    {
        var text = SettingsSubtitleFormatter.Format(string.Empty);

        Assert.Contains("Settings file:", text);
        Assert.Contains("(not resolved)", text);
    }

    [Fact]
    public void H04_SettingsSubtitle_NullPath_FallsBackGracefully()
    {
        var text = SettingsSubtitleFormatter.Format(null);

        Assert.Contains("(not resolved)", text);
    }

    // ---- M-02: default project sort = Name (alphabetical) -----------------

    [Fact]
    public void M02_ProjectSortModes_DefaultIsName()
    {
        Assert.Equal("Name", ProjectSortModes.Default);
        Assert.Equal(ProjectSortModes.Name, ProjectSortModes.Default);
    }

    // ---- M-03: View log routes through IShellLauncher, never Process.Start -

    [Fact]
    public void M03_WindowsShellLauncher_Open_UsesShellExecute()
    {
        ProcessStartInfo? captured = null;
        var launcher = new WindowsShellLauncher(info => captured = info);

        launcher.Open(@"D:\Profiles\example\logs\app-20260516.log");

        Assert.NotNull(captured);
        Assert.True(captured!.UseShellExecute);
        Assert.Equal(@"D:\Profiles\example\logs\app-20260516.log", captured.FileName);
    }

    [Fact]
    public void M03_WindowsShellLauncher_Open_RejectsEmptyTarget()
    {
        var launcher = new WindowsShellLauncher(_ => { });
        Assert.Throws<System.ArgumentException>(() => launcher.Open(""));
        Assert.Throws<System.ArgumentException>(() => launcher.Open("   "));
    }

    [Fact]
    public void M03_LogOpenTarget_PrefersTodaysFileWhenItExists()
    {
        var today = AppLogger.CurrentLogFile;
        var folder = AppLogger.LogFolder;

        var resolved = LogOpenTarget.Resolve(
            fileExists: p => p == today,
            directoryExists: p => p == folder);

        Assert.Equal(today, resolved);
    }

    [Fact]
    public void M03_LogOpenTarget_FallsBackToFolderWhenFileMissing()
    {
        var folder = AppLogger.LogFolder;

        var resolved = LogOpenTarget.Resolve(
            fileExists: _ => false,
            directoryExists: p => p == folder);

        Assert.Equal(folder, resolved);
    }
}
