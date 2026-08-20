using ControlTower.Core.Configuration;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Launch;
using ControlTower.Infrastructure.Update;

namespace ControlTower.Tests;

public sealed class VelopackUninstallServiceTests : IDisposable
{
    private readonly string _scratch = Path.Combine(
        Path.GetTempPath(),
        "ct-uninstall-" + Guid.NewGuid().ToString("N"));

    public VelopackUninstallServiceTests()
    {
        Directory.CreateDirectory(_scratch);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_scratch, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void BuildCleanupTargets_AppOnlyPreservesPortableData()
    {
        var (service, paths, _) = CreateService();

        var targets = service.BuildCleanupTargets(
            UninstallDataMode.KeepPortableData);

        Assert.Contains(paths.LocalStateRoot, targets);
        Assert.Contains(paths.LocalSettingsOverridePath, targets);
        Assert.DoesNotContain(paths.ConfigRoot, targets);
        Assert.DoesNotContain(paths.PortfolioPath, targets);
        Assert.DoesNotContain(paths.DefaultLibraryPath, targets);
    }

    [Fact]
    public void BuildCleanupTargets_ConfigOnlyKeepsLibrary()
    {
        var (service, paths, _) = CreateService();

        var targets = service.BuildCleanupTargets(
            UninstallDataMode.RemovePortableConfigurationKeepLibrary);

        Assert.Contains(paths.PortfolioPath, targets);
        Assert.Contains(paths.GlobalSettingsPath, targets);
        Assert.Contains(paths.ProfilesPath, targets);
        Assert.Contains(
            Path.Combine(paths.ConfigRoot, "portfolio-projects"),
            targets);
        Assert.DoesNotContain(paths.ConfigRoot, targets);
        Assert.DoesNotContain(paths.DefaultLibraryPath, targets);
    }

    [Fact]
    public void BuildCleanupTargets_AllDataDeletesOnlyAppOwnedRoots()
    {
        var (service, paths, _) = CreateService();

        var targets = service.BuildCleanupTargets(
            UninstallDataMode.RemoveAllPortableData);

        Assert.Contains(paths.LocalStateRoot, targets);
        Assert.Contains(paths.ConfigRoot, targets);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            targets);
    }

    [Fact]
    public void WriteUninstallScript_CleansDataOnlyAfterSuccessfulUninstall()
    {
        var (service, paths, _) = CreateService();

        var scriptPath = service.WriteUninstallScript(
            UninstallDataMode.RemoveAllPortableData,
            currentProcessId: 1234);
        var script = File.ReadAllText(scriptPath);

        var startIndex = script.IndexOf(
            "Start-Process -FilePath $updateExe",
            StringComparison.Ordinal);
        var exitCheckIndex = script.IndexOf(
            "$uninstaller.ExitCode -ne 0",
            StringComparison.Ordinal);
        var cleanupIndex = script.IndexOf(
            "Remove-Item -LiteralPath '" + paths.ConfigRoot.Replace("'", "''") + "'",
            StringComparison.Ordinal);

        Assert.True(startIndex >= 0);
        Assert.True(exitCheckIndex > startIndex);
        Assert.True(cleanupIndex > exitCheckIndex);
        Assert.Contains("No user data was removed.", script);
        Assert.Contains("Wait-Process -Id $appPid", script);
    }

    [Fact]
    public void Launch_StartsGeneratedScriptAndReturnsHandoff()
    {
        var (service, _, launcher) = CreateService();

        var result = service.Launch(
            UninstallDataMode.KeepPortableData,
            currentProcessId: 1234);

        Assert.True(result.Started);
        Assert.NotNull(launcher.ScriptPath);
        Assert.True(File.Exists(launcher.ScriptPath));
    }

    private (VelopackUninstallService Service, AppPaths Paths, FakeShellLauncher Launcher)
        CreateService()
    {
        var configRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeveloperControlTower");
        var localRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeveloperControlTower");
        var paths = new AppPaths(
            configRoot,
            Path.Combine(configRoot, "portfolio.yml"),
            Path.Combine(configRoot, "settings.yml"),
            Path.Combine(configRoot, "profiles.yml"),
            Path.Combine(localRoot, "active-profile.txt"),
            Path.Combine(configRoot, "library"),
            localRoot,
            Path.Combine(configRoot, "settings.local.yml"),
            Path.Combine(localRoot, "legacy-install-path.txt"));
        var updateExe = Path.Combine(_scratch, "Update.exe");
        File.WriteAllText(updateExe, "test");
        var launcher = new FakeShellLauncher();
        var service = new VelopackUninstallService(
            paths,
            updateExe,
            launcher,
            () => _scratch);
        return (service, paths, launcher);
    }

    private sealed class FakeShellLauncher : IShellLauncher
    {
        public string? ScriptPath { get; private set; }

        public void Open(string pathOrUri)
        {
        }

        public int LaunchUpdateConsole(string scriptPath)
        {
            return 1;
        }

        public int LaunchUpdateConsoleElevated(string scriptPath)
        {
            return 1;
        }

        public int LaunchPowerShellScript(string scriptPath)
        {
            ScriptPath = scriptPath;
            return 42;
        }
    }
}
