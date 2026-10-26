using System;
using System.IO;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Tests;

public class ThemeSelectionStoreTests : IDisposable
{
    private readonly string _folder;

    public ThemeSelectionStoreTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ct-themes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* best-effort */ }
    }

    private string FilePath => Path.Combine(_folder, ThemeSelectionStore.FileName);

    private ThemeSelectionStore NewStore() => new(() => _folder);

    [Fact]
    public void Read_NoFile_ReturnsDefault()
    {
        Assert.Equal(ThemeSelection.Default, NewStore().Read());
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var store = NewStore();
        var selection = new ThemeSelection("tokyo-night", "rose-pine", KeepGitStatesDistinct: false);

        store.Write(selection);

        Assert.Equal(selection, store.Read());
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public void Write_Default_RemovesFile()
    {
        var store = NewStore();
        store.Write(new ThemeSelection("nord", "lupine", true));
        Assert.True(File.Exists(FilePath));

        store.Write(ThemeSelection.Default);

        Assert.False(File.Exists(FilePath));
    }

    [Fact]
    public void Read_IgnoresUnknownKeysAndTolerantOfCase()
    {
        File.WriteAllText(FilePath, "# comment\nfuture=thing\n  DARK = Gruvbox \nlight=white\ndistinct-git-states=FALSE\n");

        var selection = NewStore().Read();

        Assert.Equal("gruvbox", selection.DarkThemeId);
        Assert.Equal("white", selection.LightThemeId);
        Assert.False(selection.KeepGitStatesDistinct);
    }

    [Theory]
    [InlineData("dark=rose-pine")]            // light theme in the dark slot
    [InlineData("dark=no-such-theme")]
    [InlineData("dark=..\\..\\evil")]
    [InlineData("dark=")]
    public void Read_InvalidDarkId_FallsBackToBuiltIn(string line)
    {
        File.WriteAllText(FilePath, line + "\nlight=lupine\n");

        var selection = NewStore().Read();

        Assert.Equal("control-tower-dark", selection.DarkThemeId);
        Assert.Equal("lupine", selection.LightThemeId);
    }

    [Fact]
    public void Read_UnparseableBoolean_KeepsDefault()
    {
        File.WriteAllText(FilePath, "distinct-git-states=maybe\n");

        Assert.True(NewStore().Read().KeepGitStatesDistinct);
    }

    [Fact]
    public void Read_OversizedFile_ReturnsDefault()
    {
        File.WriteAllText(FilePath, "dark=nord\n" + new string('x', 8192));

        Assert.Equal(ThemeSelection.Default, NewStore().Read());
    }

    [Fact]
    public void Write_NormalisesInvalidIds()
    {
        var store = NewStore();

        store.Write(new ThemeSelection("lupine", "nord", false));

        Assert.Equal(new ThemeSelection("control-tower-dark", "control-tower-light", false), store.Read());
    }

    [Fact]
    public void FolderProviderThrows_NeverPropagates()
    {
        var store = new ThemeSelectionStore(() => throw new InvalidOperationException("boom"));

        store.Write(new ThemeSelection("nord", "lupine", true));
        Assert.Equal(ThemeSelection.Default, store.Read());
    }
}
