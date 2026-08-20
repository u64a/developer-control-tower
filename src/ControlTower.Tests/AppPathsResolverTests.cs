using System;
using System.IO;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Tests;

// Spec §3 / AppPathsResolver:
//   - Business OneDrive (env var OneDriveCommercial) wins.
//   - Personal OneDrive (env var OneDrive) is the second choice.
//   - %AppData% is the fallback when neither OneDrive variable points to an
//     existing folder.
// These tests manipulate the process-scoped environment variables; each test
// snapshots and restores the previous values so a failure cannot pollute a
// parallel xUnit run.
public class AppPathsResolverTests : IDisposable
{
    private const string CommercialVar = "OneDriveCommercial";
    private const string PersonalVar = "OneDrive";

    private readonly string? _origCommercial;
    private readonly string? _origPersonal;
    private readonly string _scratchRoot;

    public AppPathsResolverTests()
    {
        _origCommercial = Environment.GetEnvironmentVariable(CommercialVar);
        _origPersonal = Environment.GetEnvironmentVariable(PersonalVar);
        _scratchRoot = Path.Combine(Path.GetTempPath(), "ct-paths-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);

        // Force a known starting state for every test.
        Environment.SetEnvironmentVariable(CommercialVar, null);
        Environment.SetEnvironmentVariable(PersonalVar, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(CommercialVar, _origCommercial);
        Environment.SetEnvironmentVariable(PersonalVar, _origPersonal);
        try { Directory.Delete(_scratchRoot, recursive: true); } catch { /* best-effort */ }
    }

    private string MakeRealFolder(string name)
    {
        var p = Path.Combine(_scratchRoot, name);
        Directory.CreateDirectory(p);
        return p;
    }

    [Fact]
    public void Resolve_OneDriveCommercialSet_UsesCommercialRoot()
    {
        var biz = MakeRealFolder("biz");
        Environment.SetEnvironmentVariable(CommercialVar, biz);

        var paths = AppPathsResolver.Resolve();

        Assert.Equal(Path.Combine(biz, "DeveloperControlTower"), paths.ConfigRoot);
        Assert.True(Directory.Exists(paths.ConfigRoot));
    }

    [Fact]
    public void Resolve_OneDrivePersonalOnly_UsesPersonalRoot()
    {
        var personal = MakeRealFolder("personal");
        Environment.SetEnvironmentVariable(PersonalVar, personal);

        var paths = AppPathsResolver.Resolve();

        Assert.Equal(Path.Combine(personal, "DeveloperControlTower"), paths.ConfigRoot);
    }

    [Fact]
    public void Resolve_CommercialTakesPrecedenceOverPersonal()
    {
        var biz = MakeRealFolder("biz");
        var personal = MakeRealFolder("personal");
        Environment.SetEnvironmentVariable(CommercialVar, biz);
        Environment.SetEnvironmentVariable(PersonalVar, personal);

        var paths = AppPathsResolver.Resolve();

        Assert.StartsWith(biz, paths.ConfigRoot);
        Assert.DoesNotContain("personal", paths.ConfigRoot);
    }

    [Fact]
    public void Resolve_OneDriveSetButFolderMissing_FallsThroughToNext()
    {
        // Per implementation: a OneDrive variable that points at a non-existent
        // folder must be ignored, not used. Otherwise stale env vars would
        // silently break config discovery.
        var ghostBiz = Path.Combine(_scratchRoot, "does-not-exist-biz");
        var personal = MakeRealFolder("personal");
        Environment.SetEnvironmentVariable(CommercialVar, ghostBiz);
        Environment.SetEnvironmentVariable(PersonalVar, personal);

        var paths = AppPathsResolver.Resolve();

        Assert.StartsWith(personal, paths.ConfigRoot);
    }

    [Fact]
    public void Resolve_NeitherSet_FallsBackToAppData()
    {
        var paths = AppPathsResolver.Resolve();

        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "DeveloperControlTower");
        Assert.Equal(expected, paths.ConfigRoot);
    }

    [Fact]
    public void Resolve_PortfolioAndSettingsPaths_LiveUnderConfigRoot()
    {
        var biz = MakeRealFolder("biz");
        Environment.SetEnvironmentVariable(CommercialVar, biz);

        var paths = AppPathsResolver.Resolve();

        Assert.Equal(Path.Combine(paths.ConfigRoot, "portfolio.yml"), paths.PortfolioPath);
        Assert.Equal(Path.Combine(paths.ConfigRoot, "settings.yml"), paths.GlobalSettingsPath);
        Assert.Equal(Path.Combine(paths.ConfigRoot, "profiles.yml"), paths.ProfilesPath);
        Assert.Equal(Path.Combine(paths.ConfigRoot, "library"), paths.DefaultLibraryPath);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeveloperControlTower"),
            paths.LocalStateRoot);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "DeveloperControlTower",
                "settings.local.yml"),
            paths.LocalSettingsOverridePath);
        Assert.Equal(
            Path.Combine(paths.LocalStateRoot, "legacy-install-path.txt"),
            paths.LegacyInstallPath);
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeveloperControlTower",
                "active-profile.txt"),
            paths.ActiveProfilePath);
    }

    [Fact]
    public void Resolve_CreatesConfigRoot_WhenMissing()
    {
        var biz = MakeRealFolder("biz");
        Environment.SetEnvironmentVariable(CommercialVar, biz);
        var expected = Path.Combine(biz, "DeveloperControlTower");
        Assert.False(Directory.Exists(expected));

        AppPathsResolver.Resolve();

        Assert.True(Directory.Exists(expected));
    }

    [Fact]
    public void Resolve_WhitespaceOnlyEnvVar_Ignored()
    {
        var personal = MakeRealFolder("personal");
        Environment.SetEnvironmentVariable(CommercialVar, "   ");
        Environment.SetEnvironmentVariable(PersonalVar, personal);

        var paths = AppPathsResolver.Resolve();

        Assert.StartsWith(personal, paths.ConfigRoot);
    }

    // ---- MigrateLegacyConfig ---------------------------------------------

    [Fact]
    public void Migrate_CopiesLegacyPortfolio_WhenDestinationMissing()
    {
        var biz = MakeRealFolder("biz");
        Environment.SetEnvironmentVariable(CommercialVar, biz);
        var paths = AppPathsResolver.Resolve();

        var legacyRoot = MakeRealFolder("legacy-repo");
        File.WriteAllText(Path.Combine(legacyRoot, "portfolio.yml"), "kind: legacy");

        AppPathsResolver.MigrateLegacyConfig(paths, legacyRoot);

        Assert.True(File.Exists(paths.PortfolioPath));
        Assert.Equal("kind: legacy", File.ReadAllText(paths.PortfolioPath));
    }

    [Fact]
    public void Migrate_DoesNotOverwriteExistingPortfolio()
    {
        var biz = MakeRealFolder("biz");
        Environment.SetEnvironmentVariable(CommercialVar, biz);
        var paths = AppPathsResolver.Resolve();

        File.WriteAllText(paths.PortfolioPath, "current");
        var legacyRoot = MakeRealFolder("legacy-repo");
        File.WriteAllText(Path.Combine(legacyRoot, "portfolio.yml"), "stale");

        AppPathsResolver.MigrateLegacyConfig(paths, legacyRoot);

        Assert.Equal("current", File.ReadAllText(paths.PortfolioPath));
    }

    [Fact]
    public void Migrate_EmptyLegacyRoot_NoOp()
    {
        var biz = MakeRealFolder("biz");
        Environment.SetEnvironmentVariable(CommercialVar, biz);
        var paths = AppPathsResolver.Resolve();

        AppPathsResolver.MigrateLegacyConfig(paths, "");
        AppPathsResolver.MigrateLegacyConfig(paths, null!);

        Assert.False(File.Exists(paths.PortfolioPath));
    }

    [Fact]
    public void Migrate_LegacyRootWithoutPortfolioFile_NoOp()
    {
        var biz = MakeRealFolder("biz");
        Environment.SetEnvironmentVariable(CommercialVar, biz);
        var paths = AppPathsResolver.Resolve();
        var legacyRoot = MakeRealFolder("legacy-empty");

        AppPathsResolver.MigrateLegacyConfig(paths, legacyRoot);

        Assert.False(File.Exists(paths.PortfolioPath));
    }
}
