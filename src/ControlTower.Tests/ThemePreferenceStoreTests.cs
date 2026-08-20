using System;
using System.IO;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Tests;

// Spec for ThemePreferenceStore:
//   - Missing file means "follow OS" (ThemePreference.System).
//   - Round-trip Dark/Light through the file.
//   - Writing System removes the file (so next launch falls back to OS).
//   - Unparseable content is treated as System rather than throwing.
//   - Read/Write never throw on I/O failure (folder gone, etc.).
public class ThemePreferenceStoreTests : IDisposable
{
    private readonly string _scratchRoot;

    public ThemePreferenceStoreTests()
    {
        _scratchRoot = Path.Combine(Path.GetTempPath(), "ct-theme-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_scratchRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_scratchRoot, recursive: true); } catch { /* best-effort */ }
    }

    private ThemePreferenceStore NewStore(string folder)
    {
        return new ThemePreferenceStore(() => folder);
    }

    [Fact]
    public void Read_NoFile_ReturnsSystem()
    {
        var folder = Path.Combine(_scratchRoot, "fresh");
        Directory.CreateDirectory(folder);

        var store = NewStore(folder);

        Assert.Equal(ThemePreference.System, store.Read());
    }

    [Fact]
    public void Read_MissingFolderEntirely_ReturnsSystem()
    {
        var folder = Path.Combine(_scratchRoot, "does-not-exist");
        // Deliberately do NOT create the folder; store must cope.

        var store = NewStore(folder);

        Assert.Equal(ThemePreference.System, store.Read());
    }

    [Fact]
    public void WriteThenRead_Dark_RoundTrips()
    {
        var folder = Path.Combine(_scratchRoot, "dark");
        var store = NewStore(folder);

        store.Write(ThemePreference.Dark);

        Assert.True(File.Exists(Path.Combine(folder, ThemePreferenceStore.FileName)));
        Assert.Equal(ThemePreference.Dark, store.Read());
    }

    [Fact]
    public void WriteThenRead_Light_RoundTrips()
    {
        var folder = Path.Combine(_scratchRoot, "light");
        var store = NewStore(folder);

        store.Write(ThemePreference.Light);

        Assert.True(File.Exists(Path.Combine(folder, ThemePreferenceStore.FileName)));
        Assert.Equal(ThemePreference.Light, store.Read());
    }

    [Fact]
    public void Write_System_RemovesFileSoNextLaunchFollowsOs()
    {
        var folder = Path.Combine(_scratchRoot, "revert");
        var store = NewStore(folder);

        store.Write(ThemePreference.Dark);
        Assert.True(File.Exists(Path.Combine(folder, ThemePreferenceStore.FileName)));

        store.Write(ThemePreference.System);
        Assert.False(File.Exists(Path.Combine(folder, ThemePreferenceStore.FileName)));
        Assert.Equal(ThemePreference.System, store.Read());
    }

    [Fact]
    public void Write_System_WhenNoFileExists_DoesNotThrowAndStillSystem()
    {
        var folder = Path.Combine(_scratchRoot, "no-op");
        Directory.CreateDirectory(folder);
        var store = NewStore(folder);

        store.Write(ThemePreference.System);

        Assert.False(File.Exists(Path.Combine(folder, ThemePreferenceStore.FileName)));
        Assert.Equal(ThemePreference.System, store.Read());
    }

    [Fact]
    public void Read_UnparseableContent_ReturnsSystem()
    {
        var folder = Path.Combine(_scratchRoot, "junk");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ThemePreferenceStore.FileName), "purple");

        var store = NewStore(folder);

        Assert.Equal(ThemePreference.System, store.Read());
    }

    [Fact]
    public void Read_EmptyFile_ReturnsSystem()
    {
        var folder = Path.Combine(_scratchRoot, "empty");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ThemePreferenceStore.FileName), string.Empty);

        var store = NewStore(folder);

        Assert.Equal(ThemePreference.System, store.Read());
    }

    [Fact]
    public void Read_TolerantOfCaseAndWhitespace()
    {
        var folder = Path.Combine(_scratchRoot, "case");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, ThemePreferenceStore.FileName), "  DARK\n");

        var store = NewStore(folder);

        Assert.Equal(ThemePreference.Dark, store.Read());
    }

    [Fact]
    public void Write_OverwritesPreviousValueAtomically()
    {
        var folder = Path.Combine(_scratchRoot, "overwrite");
        var store = NewStore(folder);

        store.Write(ThemePreference.Light);
        Assert.Equal(ThemePreference.Light, store.Read());

        store.Write(ThemePreference.Dark);
        Assert.Equal(ThemePreference.Dark, store.Read());

        // No stray .tmp file left behind by the atomic rename.
        var tmp = Path.Combine(folder, ThemePreferenceStore.FileName + ".tmp");
        Assert.False(File.Exists(tmp));
    }

    [Fact]
    public void Read_FolderProviderThrows_ReturnsSystem()
    {
        var store = new ThemePreferenceStore(() => throw new InvalidOperationException("boom"));

        // Must swallow the exception and degrade gracefully.
        Assert.Equal(ThemePreference.System, store.Read());
    }

    [Fact]
    public void Write_FolderProviderThrows_DoesNotPropagate()
    {
        var store = new ThemePreferenceStore(() => throw new InvalidOperationException("boom"));

        // Write must never throw; it is best-effort.
        store.Write(ThemePreference.Dark);
    }
}
