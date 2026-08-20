using System;
using System.IO;
using ControlTower.Infrastructure.FileSystem;

namespace ControlTower.Tests;

// PathDiscovery walks up from a start path looking for portfolio.yml or a
// .git folder so the tool can locate the repo root from any subdirectory.
public class PathDiscoveryTests : IDisposable
{
    private readonly string _root;

    public PathDiscoveryTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-discover-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void FindRepoRoot_PortfolioYmlAtStart_ReturnsStart()
    {
        File.WriteAllText(Path.Combine(_root, "portfolio.yml"), "kind: portfolio");

        var found = PathDiscovery.FindRepoRoot(_root);

        Assert.Equal(_root, found);
    }

    [Fact]
    public void FindRepoRoot_DotGitAtStart_ReturnsStart()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));

        var found = PathDiscovery.FindRepoRoot(_root);

        Assert.Equal(_root, found);
    }

    [Fact]
    public void FindRepoRoot_WalksUpFromNestedSubdirectory()
    {
        File.WriteAllText(Path.Combine(_root, "portfolio.yml"), "kind: portfolio");
        var nested = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(nested);

        var found = PathDiscovery.FindRepoRoot(nested);

        Assert.Equal(_root, found);
    }

    [Fact]
    public void FindRepoRoot_NoMarkers_FallsBackToBaseDirectory()
    {
        // No portfolio.yml or .git anywhere up to the temp root; the walker
        // eventually runs out of parents and must return AppDomain base dir
        // rather than throw.
        var leaf = Path.Combine(_root, "x");
        Directory.CreateDirectory(leaf);

        var found = PathDiscovery.FindRepoRoot(leaf);

        Assert.Equal(AppDomain.CurrentDomain.BaseDirectory, found);
    }

    [Fact]
    public void FindRepoRoot_PrefersClosestMarker()
    {
        // Outer has .git, inner has portfolio.yml; the walk starts inside
        // inner so inner wins.
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        var inner = Path.Combine(_root, "inner");
        Directory.CreateDirectory(inner);
        File.WriteAllText(Path.Combine(inner, "portfolio.yml"), "kind: portfolio");

        var found = PathDiscovery.FindRepoRoot(Path.Combine(inner, "deeper-subdir-that-does-not-exist"));

        Assert.Equal(inner, found);
    }

    [Fact]
    public void FindRepoRoot_NoArguments_DoesNotThrow()
    {
        // The parameterless overload uses AppDomain.CurrentDomain.BaseDirectory.
        // We don't assert the result (it depends on where the test runner
        // lives) but it must never throw.
        var result = PathDiscovery.FindRepoRoot();
        Assert.False(string.IsNullOrWhiteSpace(result));
    }
}
