using System;
using System.IO;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Tests;

public sealed class ActiveProfileSelectionStoreTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public ActiveProfileSelectionStoreTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "ct-active-profile-" + Guid.NewGuid().ToString("N"));
        _path = Path.Combine(_root, "active-profile.txt");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Load_MissingSelection_ReturnsNoIdAndDoesNotThrow()
    {
        var loaded = new ActiveProfileSelectionStore(_path).Load();

        Assert.Null(loaded.ProfileId);
        Assert.Null(loaded.Issue);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsOnlyProfileId()
    {
        var id = Guid.NewGuid();
        var store = new ActiveProfileSelectionStore(_path);

        store.Save(id);
        var loaded = store.Load();

        Assert.Equal(id, loaded.ProfileId);
        Assert.Null(loaded.Issue);
        Assert.Equal(id.ToString("D"), File.ReadAllText(_path));
    }

    [Fact]
    public void Save_OverwritesAtomicallyWithoutTempFiles()
    {
        var store = new ActiveProfileSelectionStore(_path);
        var second = Guid.NewGuid();

        store.Save(Guid.NewGuid());
        store.Save(second);

        Assert.Equal(second, store.Load().ProfileId);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public void Load_MalformedSelection_ReturnsVisibleIssue()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(_path, "not-a-guid");

        var loaded = new ActiveProfileSelectionStore(_path).Load();

        Assert.Null(loaded.ProfileId);
        Assert.Equal("profiles/selection/invalid", loaded.Issue?.Code);
    }

    [Fact]
    public void Save_EmptyGuid_IsRejected()
    {
        var store = new ActiveProfileSelectionStore(_path);

        Assert.Throws<ArgumentException>(() => store.Save(Guid.Empty));
        Assert.False(File.Exists(_path));
    }
}
