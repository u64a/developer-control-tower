using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Tests;

public sealed class LegacyInstallLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ct-legacy-install-" + Guid.NewGuid().ToString("N"));

    public LegacyInstallLocatorTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
        }
    }

    [Fact]
    public void RecordAndResolve_RoundTripsValidatedLegacyInstall()
    {
        var legacy = MakeLegacyInstall("legacy");
        var current = Path.Combine(_root, "packaged", "current");
        Directory.CreateDirectory(current);
        var marker = Path.Combine(_root, "state", "legacy-install-path.txt");

        var recorded = LegacyInstallLocator.TryRecordCurrentSourceInstall(
            marker,
            legacy);
        var resolved = LegacyInstallLocator.Resolve(marker, current);

        Assert.True(recorded);
        Assert.Equal(Path.GetFullPath(legacy), resolved);
    }

    [Fact]
    public void Resolve_RejectsMarkerPointingAtNonInstallFolder()
    {
        var invalid = Path.Combine(_root, "not-an-install");
        Directory.CreateDirectory(invalid);
        var marker = Path.Combine(_root, "legacy-install-path.txt");
        File.WriteAllText(marker, invalid);

        var resolved = LegacyInstallLocator.Resolve(
            marker,
            Path.Combine(_root, "current"));

        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void Resolve_DoesNotReportCurrentSourceInstallAsLegacy()
    {
        var current = MakeLegacyInstall("current");
        var marker = Path.Combine(_root, "legacy-install-path.txt");
        Assert.True(LegacyInstallLocator.TryRecordCurrentSourceInstall(
            marker,
            current));

        var resolved = LegacyInstallLocator.Resolve(marker, current);

        Assert.Equal(string.Empty, resolved);
    }

    private string MakeLegacyInstall(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(
            Path.Combine(path, "ControlTower.Desktop.exe"),
            "test");
        File.WriteAllText(
            Path.Combine(path, "update-repo-root.txt"),
            _root);
        return path;
    }
}
