using System;
using System.IO;
using System.Linq;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Tests;

// GroupCollapseStore: round-trips collapsed group labels per-machine; empty
// set removes the file; never throws on bad input/IO.
public class GroupCollapseStoreTests : IDisposable
{
    private readonly string _root;

    public GroupCollapseStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-grp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private GroupCollapseStore New(string f) => new GroupCollapseStore(() => f);

    [Fact]
    public void Read_NoFile_ReturnsEmpty()
    {
        var s = New(Path.Combine(_root, "a"));
        Assert.Empty(s.Read());
    }

    [Fact]
    public void Write_Then_Read_RoundTrips()
    {
        var folder = Path.Combine(_root, "b");
        var s = New(folder);
        s.Write(new[] { "Customer Projects", "IPKits" });
        var read = s.Read();
        Assert.Contains("Customer Projects", read);
        Assert.Contains("IPKits", read);
    }

    [Fact]
    public void Write_Empty_RemovesFile()
    {
        var folder = Path.Combine(_root, "c");
        var s = New(folder);
        s.Write(new[] { "IPKits" });
        Assert.True(File.Exists(Path.Combine(folder, GroupCollapseStore.FileName)));
        s.Write(Array.Empty<string>());
        Assert.False(File.Exists(Path.Combine(folder, GroupCollapseStore.FileName)));
    }

    [Fact]
    public void Read_IsCaseInsensitive()
    {
        var folder = Path.Combine(_root, "d");
        New(folder).Write(new[] { "IPKits" });
        Assert.Contains("ipkits", New(folder).Read(), StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_FolderProviderThrows_DoesNotPropagate()
    {
        var s = new GroupCollapseStore(() => throw new InvalidOperationException());
        s.Write(new[] { "x" });
        Assert.Empty(s.Read());
    }
}
