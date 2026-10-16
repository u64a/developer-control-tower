using System;
using System.IO;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Tests;

public class SettingsWriterArchiveTests : IDisposable
{
    private readonly string _root;

    public SettingsWriterArchiveTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-settings-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Write_ArchiveStorageRootOutsideUserRoots_PersistsConfiguredAndEffectiveValues()
    {
        var settingsFile = Path.Combine(_root, "settings.yml");
        var configured = @"C:\GitHub\Archive";

        new SettingsWriter().Write(
            settingsFile,
            Array.Empty<RepoStore>(),
            libraryPath: null,
            updateOptions: null,
            defaultLaunchEnvironment: null,
            archiveStorageRoot: configured);

        var yaml = File.ReadAllText(settingsFile);
        var loaded = new ToolSettingsProvider().Load(settingsFile);
        var expected = Path.GetFullPath(configured);

        Assert.Contains("archive:", yaml);
        Assert.Equal(expected, loaded.ConfiguredArchiveStorageRootPath);
        Assert.Equal(expected, loaded.ArchiveStorageRootPath);
    }

    [Fact]
    public void Write_ArchiveStorageRootEmpty_ClearsConfiguredValueAndUsesDefaultFallback()
    {
        var settingsFile = Path.Combine(_root, "settings.yml");
        var configured = @"C:\GitHub\Archive";

        var writer = new SettingsWriter();
        writer.Write(
            settingsFile,
            Array.Empty<RepoStore>(),
            libraryPath: null,
            updateOptions: null,
            defaultLaunchEnvironment: null,
            archiveStorageRoot: configured);

        writer.Write(
            settingsFile,
            Array.Empty<RepoStore>(),
            libraryPath: null,
            updateOptions: null,
            defaultLaunchEnvironment: null,
            archiveStorageRoot: string.Empty);

        var yaml = File.ReadAllText(settingsFile);
        var loaded = new ToolSettingsProvider().Load(settingsFile);
        var expectedDefault = Path.GetFullPath(ArchiveStorageRootResolver.GetDefaultRoot());

        Assert.Contains("storage_root: ''", yaml, StringComparison.Ordinal);
        Assert.Equal(string.Empty, loaded.ConfiguredArchiveStorageRootPath);
        Assert.Equal(expectedDefault, loaded.ArchiveStorageRootPath);
    }

    [Fact]
    public void Write_ArchiveStorageRootNull_PreservesExistingConfiguredValue()
    {
        var settingsFile = Path.Combine(_root, "settings.yml");
        var configured = @"C:\GitHub\Archive\Preserve";

        var writer = new SettingsWriter();
        writer.Write(
            settingsFile,
            Array.Empty<RepoStore>(),
            libraryPath: null,
            updateOptions: null,
            defaultLaunchEnvironment: null,
            archiveStorageRoot: configured);

        writer.Write(
            settingsFile,
            Array.Empty<RepoStore>(),
            libraryPath: null,
            updateOptions: null,
            defaultLaunchEnvironment: null,
            archiveStorageRoot: null);

        var loaded = new ToolSettingsProvider().Load(settingsFile);

        Assert.Equal(Path.GetFullPath(configured), loaded.ConfiguredArchiveStorageRootPath);
    }
}
