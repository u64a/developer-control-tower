using ControlTower.Infrastructure.Library;

namespace ControlTower.Tests;

public sealed class LibraryBootstrapperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ct-library-bootstrap-" + Guid.NewGuid().ToString("N"));

    public LibraryBootstrapperTests()
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
    public void EnsureInitialized_PrefersLegacyLibraryAndCopiesNestedFiles()
    {
        var legacy = MakeLibrary("legacy", "legacy");
        var starter = MakeLibrary("starter", "starter");
        var nested = Path.Combine(legacy, "skills", "sample");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "skill.md"), "legacy skill");
        var destination = Path.Combine(_root, "user-library");

        var result = LibraryBootstrapper.EnsureInitialized(
            destination,
            legacy,
            starter);

        Assert.Equal(LibraryBootstrapSource.LegacyApplication, result.Source);
        Assert.Equal("legacy", File.ReadAllText(Path.Combine(destination, "library.yml")));
        Assert.Equal(
            "legacy skill",
            File.ReadAllText(Path.Combine(destination, "skills", "sample", "skill.md")));
    }

    [Fact]
    public void EnsureInitialized_UsesStarterWhenLegacyIsUnavailable()
    {
        var starter = MakeLibrary("starter", "starter");
        var destination = Path.Combine(_root, "user-library");

        var result = LibraryBootstrapper.EnsureInitialized(
            destination,
            Path.Combine(_root, "missing"),
            starter);

        Assert.Equal(LibraryBootstrapSource.Starter, result.Source);
        Assert.Equal("starter", File.ReadAllText(Path.Combine(destination, "library.yml")));
    }

    [Fact]
    public void EnsureInitialized_DoesNotOverwriteExistingLibrary()
    {
        var legacy = MakeLibrary("legacy", "legacy");
        var starter = MakeLibrary("starter", "starter");
        var destination = MakeLibrary("user-library", "user");

        var result = LibraryBootstrapper.EnsureInitialized(
            destination,
            legacy,
            starter);

        Assert.Equal(LibraryBootstrapSource.Existing, result.Source);
        Assert.Equal("user", File.ReadAllText(Path.Combine(destination, "library.yml")));
    }

    [Fact]
    public void EnsureInitialized_LeavesConflictingFolderUntouched()
    {
        var starter = MakeLibrary("starter", "starter");
        var destination = Path.Combine(_root, "user-library");
        Directory.CreateDirectory(destination);
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");

        var result = LibraryBootstrapper.EnsureInitialized(
            destination,
            string.Empty,
            starter);

        Assert.Equal(LibraryBootstrapSource.Conflict, result.Source);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.False(File.Exists(Path.Combine(destination, "library.yml")));
    }

    [Fact]
    public void EnsureInitialized_MissingSourcesCreatesEmptyRootWithWarning()
    {
        var destination = Path.Combine(_root, "user-library");

        var result = LibraryBootstrapper.EnsureInitialized(
            destination,
            Path.Combine(_root, "missing-legacy"),
            Path.Combine(_root, "missing-starter"));

        Assert.Equal(LibraryBootstrapSource.Unavailable, result.Source);
        Assert.True(Directory.Exists(destination));
        Assert.False(File.Exists(Path.Combine(destination, "library.yml")));
    }

    private string MakeLibrary(string name, string registryContent)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "library.yml"), registryContent);
        return path;
    }
}
