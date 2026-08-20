using System;
using System.IO;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Tests;

// Spec for AccentPreferenceStore (mirrors ThemePreferenceStore):
//   - Missing file means brand accent (AccentPreference.TowerCyan).
//   - Round-trip WindowsAccent through the file.
//   - Writing TowerCyan removes the file (so the brand default applies).
//   - Unparseable/empty content is treated as TowerCyan rather than throwing.
//   - Read/Write never throw on I/O failure.
public class AccentPreferenceStoreTests : IDisposable
{
    private readonly string _scratchRoot;

    public AccentPreferenceStoreTests()
    {
        _scratchRoot = Path.Combine(Path.GetTempPath(), "ct-accent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchRoot, recursive: true); } catch { /* best-effort */ }
    }

    private AccentPreferenceStore NewStore(string folder)
    {
        return new AccentPreferenceStore(() => folder);
    }

    [Fact]
    public void Read_NoFile_ReturnsTowerCyan()
    {
        var folder = Path.Combine(_scratchRoot, "fresh");
        Directory.CreateDirectory(folder);

        var store = NewStore(folder);

        Assert.Equal(AccentPreference.TowerCyan, store.Read());
    }

    [Fact]
    public void Read_MissingFolderEntirely_ReturnsTowerCyan()
    {
        var folder = Path.Combine(_scratchRoot, "does-not-exist");

        var store = NewStore(folder);

        Assert.Equal(AccentPreference.TowerCyan, store.Read());
    }

    [Fact]
    public void WriteThenRead_WindowsAccent_RoundTrips()
    {
        var folder = Path.Combine(_scratchRoot, "windows");
        var store = NewStore(folder);

        store.Write(AccentPreference.WindowsAccent);

        Assert.True(File.Exists(Path.Combine(folder, AccentPreferenceStore.FileName)));
        Assert.Equal(AccentPreference.WindowsAccent, store.Read());
    }

    [Fact]
    public void Write_TowerCyan_RemovesFileSoBrandDefaultApplies()
    {
        var folder = Path.Combine(_scratchRoot, "revert");
        var store = NewStore(folder);

        store.Write(AccentPreference.WindowsAccent);
        Assert.True(File.Exists(Path.Combine(folder, AccentPreferenceStore.FileName)));

        store.Write(AccentPreference.TowerCyan);
        Assert.False(File.Exists(Path.Combine(folder, AccentPreferenceStore.FileName)));
        Assert.Equal(AccentPreference.TowerCyan, store.Read());
    }

    [Fact]
    public void Read_UnparseableContent_ReturnsTowerCyan()
    {
        var folder = Path.Combine(_scratchRoot, "junk");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, AccentPreferenceStore.FileName), "magenta");

        var store = NewStore(folder);

        Assert.Equal(AccentPreference.TowerCyan, store.Read());
    }

    [Fact]
    public void Read_EmptyFile_ReturnsTowerCyan()
    {
        var folder = Path.Combine(_scratchRoot, "empty");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, AccentPreferenceStore.FileName), string.Empty);

        var store = NewStore(folder);

        Assert.Equal(AccentPreference.TowerCyan, store.Read());
    }

    [Fact]
    public void Read_TolerantOfCaseAndWhitespace()
    {
        var folder = Path.Combine(_scratchRoot, "case");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, AccentPreferenceStore.FileName), "  WINDOWS\n");

        var store = NewStore(folder);

        Assert.Equal(AccentPreference.WindowsAccent, store.Read());
    }

    [Fact]
    public void Write_OverwritesPreviousValueLeavingNoTempFile()
    {
        var folder = Path.Combine(_scratchRoot, "overwrite");
        var store = NewStore(folder);

        store.Write(AccentPreference.WindowsAccent);
        Assert.Equal(AccentPreference.WindowsAccent, store.Read());

        var tmp = Path.Combine(folder, AccentPreferenceStore.FileName + ".tmp");
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Read_FolderProviderThrows_ReturnsTowerCyan()
    {
        var store = new AccentPreferenceStore(() => throw new InvalidOperationException("boom"));

        Assert.Equal(AccentPreference.TowerCyan, store.Read());
    }

    [Fact]
    public void Write_FolderProviderThrows_DoesNotPropagate()
    {
        var store = new AccentPreferenceStore(() => throw new InvalidOperationException("boom"));

        store.Write(AccentPreference.WindowsAccent);
    }
}
