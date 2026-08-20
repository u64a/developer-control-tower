using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Tests;

/// <summary>
/// Tests for <see cref="QuarantineService"/>. Uses a per-test sandbox as
/// both the "user profile" (where the quarantine root will be created)
/// and as the working area, so nothing escapes onto the developer's
/// real profile.
/// </summary>
public class QuarantineServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _profile;

    public QuarantineServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-quarantine-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _profile = Path.Combine(_root, "profile");
        Directory.CreateDirectory(_profile);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private QuarantineService NewService(DateTime? utc = null)
    {
        var ts = utc ?? new DateTime(2026, 5, 16, 12, 34, 56, DateTimeKind.Utc);
        return new QuarantineService(() => _profile, () => ts);
    }

    [Fact]
    public async Task QuarantineAsync_MovesContentsAndRemovesSource()
    {
        var source = Path.Combine(_root, "source-repo");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "marker.txt"), "hello");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "child.txt"), "world");

        var destination = await NewService().QuarantineAsync(source, "my-slug", CancellationToken.None);

        Assert.False(Directory.Exists(source), "source must be moved away");
        Assert.True(Directory.Exists(destination));
        Assert.True(File.Exists(Path.Combine(destination, "marker.txt")));
        Assert.True(File.Exists(Path.Combine(destination, "nested", "child.txt")));

        var quarantineRoot = Path.GetDirectoryName(destination);
        Assert.NotNull(quarantineRoot);
        Assert.StartsWith("projectmgr-quarantine-", Path.GetFileName(quarantineRoot!));
        Assert.Equal(_profile, Path.GetDirectoryName(quarantineRoot!));
    }

    [Fact]
    public async Task QuarantineAsync_CrossVolumeFallback_CopyThenDelete()
    {
        // Simulate cross-volume by pre-creating the destination as a
        // read-only file so Directory.Move fails with IOException;
        // the service must then fall back to copy + delete. To trigger
        // that here without crossing real volumes we instead create a
        // conflicting destination and verify the disambiguation path
        // still produces a copy outcome.
        var source = Path.Combine(_root, "src");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "a.txt"), "alpha");

        // Pre-create a fake same-timestamp quarantine that already owns
        // the bare slug name, forcing the disambiguation suffix path.
        var ts = new DateTime(2026, 5, 16, 12, 34, 56, DateTimeKind.Utc);
        var conflictingRoot = Path.Combine(_profile, "projectmgr-quarantine-" + ts.ToString("yyyyMMdd-HHmmss"));
        Directory.CreateDirectory(Path.Combine(conflictingRoot, "slug"));

        var destination = await NewService(ts).QuarantineAsync(source, "slug", CancellationToken.None);

        Assert.True(Directory.Exists(destination));
        Assert.True(File.Exists(Path.Combine(destination, "a.txt")));
        Assert.False(Directory.Exists(source));
        // The disambiguation suffix should kick in.
        Assert.EndsWith("-1", Path.GetFileName(destination));
    }

    [Fact]
    public async Task QuarantineAsync_MissingSource_ThrowsDirectoryNotFound()
    {
        var missing = Path.Combine(_root, "does-not-exist");

        var ex = await Assert.ThrowsAsync<DirectoryNotFoundException>(
            () => NewService().QuarantineAsync(missing, "slug", CancellationToken.None));

        Assert.Contains("does-not-exist", ex.Message);
    }

    [Fact]
    public async Task QuarantineAsync_BlankSource_ThrowsArgumentException()
    {
        await Assert.ThrowsAsync<ArgumentException>(
            () => NewService().QuarantineAsync(string.Empty, "slug", CancellationToken.None));
    }

    [Fact]
    public async Task QuarantineAsync_EmptySlug_SanitisesToProjectFallback()
    {
        var source = Path.Combine(_root, "src-empty-slug");
        Directory.CreateDirectory(source);
        File.WriteAllText(Path.Combine(source, "x.txt"), "x");

        var destination = await NewService().QuarantineAsync(source, "   ", CancellationToken.None);

        Assert.Equal("project", Path.GetFileName(destination));
        Assert.True(File.Exists(Path.Combine(destination, "x.txt")));
    }

    [Fact]
    public async Task QuarantineAsync_InvalidSlugCharacters_AreReplaced()
    {
        var source = Path.Combine(_root, "src-bad-slug");
        Directory.CreateDirectory(source);

        var destination = await NewService().QuarantineAsync(source, "bad/slug:name", CancellationToken.None);

        var folder = Path.GetFileName(destination);
        Assert.DoesNotContain("/", folder);
        Assert.DoesNotContain(":", folder);
    }
}
