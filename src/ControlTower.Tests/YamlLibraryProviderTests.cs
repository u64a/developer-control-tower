using System.IO;
using System.Linq;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Library;

namespace ControlTower.Tests;

public class YamlLibraryProviderTests
{
    [Fact]
    public void LoadLibrary_AutoDiscover_FindsFolderNotInRegistry()
    {
        var lib = MakeLib();
        try
        {
            // Registry declares the type only.
            File.WriteAllText(Path.Combine(lib, "library.yml"),
                "schema_version: \"1\"\n" +
                "asset_types:\n" +
                "  skill:\n" +
                "    layout: folder\n" +
                "    default_target: \".github/skills/{asset_id}/\"\n");

            // Drop a skill folder without registering it.
            var skillFolder = Path.Combine(lib, "skills", "drop-and-go");
            Directory.CreateDirectory(skillFolder);
            File.WriteAllText(Path.Combine(skillFolder, "README.md"), "hi");

            var provider = new YamlLibraryProvider();
            var index = provider.LoadLibrary(lib);

            Assert.Single(index.Assets);
            var asset = index.Assets[0];
            Assert.Equal("drop-and-go", asset.Id);
            Assert.Equal("skill", asset.TypeId);
            Assert.True(Directory.Exists(asset.AbsoluteRoot));
        }
        finally { Cleanup(lib); }
    }

    [Fact]
    public void LoadLibrary_AutoDiscover_FileCollectionPopulatesFilesFromFolder()
    {
        var lib = MakeLib();
        try
        {
            File.WriteAllText(Path.Combine(lib, "library.yml"),
                "schema_version: \"1\"\n" +
                "asset_types:\n" +
                "  md-file:\n" +
                "    layout: file_collection\n" +
                "    default_target: \"docs/\"\n");

            var folder = Path.Combine(lib, "md-files", "guides");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "a.md"), "a");
            File.WriteAllText(Path.Combine(folder, "b.md"), "b");

            var index = new YamlLibraryProvider().LoadLibrary(lib);
            Assert.Single(index.Assets);
            var asset = index.Assets[0];
            Assert.Equal(2, asset.Files.Count);
            Assert.Contains("a.md", asset.Files);
            Assert.Contains("b.md", asset.Files);
        }
        finally { Cleanup(lib); }
    }

    [Fact]
    public void LoadLibrary_AutoDiscover_HonoursAssetYmlOverrides()
    {
        var lib = MakeLib();
        try
        {
            File.WriteAllText(Path.Combine(lib, "library.yml"),
                "schema_version: \"1\"\n" +
                "asset_types:\n" +
                "  skill:\n" +
                "    layout: folder\n" +
                "    default_target: \".github/skills/{asset_id}/\"\n");

            var folder = Path.Combine(lib, "skills", "fancy");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "skill.md"), "x");
            File.WriteAllText(Path.Combine(folder, "asset.yml"),
                "id: fancy\n" +
                "type: skill\n" +
                "version: \"2.5.0\"\n" +
                "description: Custom description\n" +
                "default_target: custom/path/\n");

            var index = new YamlLibraryProvider().LoadLibrary(lib);
            var asset = index.Assets.First(a => a.Id == "fancy");
            Assert.Equal("2.5.0", asset.Version);
            Assert.Equal("Custom description", asset.Description);
            Assert.Equal("custom/path/", asset.DefaultTargetOverride);
        }
        finally { Cleanup(lib); }
    }

    [Fact]
    public void LoadLibrary_AutoDiscover_DoesNotDuplicateRegistryEntries()
    {
        var lib = MakeLib();
        try
        {
            File.WriteAllText(Path.Combine(lib, "library.yml"),
                "schema_version: \"1\"\n" +
                "asset_types:\n" +
                "  skill:\n" +
                "    layout: folder\n" +
                "    default_target: \".github/skills/{asset_id}/\"\n" +
                "assets:\n" +
                "  - id: declared\n" +
                "    type: skill\n" +
                "    path: skills/declared\n" +
                "    description: From registry\n");

            var folder = Path.Combine(lib, "skills", "declared");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "README.md"), "x");

            var index = new YamlLibraryProvider().LoadLibrary(lib);
            Assert.Single(index.Assets);
            Assert.Equal("From registry", index.Assets[0].Description);
        }
        finally { Cleanup(lib); }
    }

    [Fact]
    public void LoadLibrary_RegistryParentTraversalIsRejected()
    {
        var lib = MakeLib();
        var outside = Path.Combine(
            Path.GetDirectoryName(lib)!,
            "dct_outside_" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "asset.yml"), "description: outside\n");
            WriteRegistry(
                lib,
                Path.GetRelativePath(lib, outside).Replace('\\', '/'));

            var index = new YamlLibraryProvider().LoadLibrary(lib);

            Assert.Empty(index.Assets);
            Assert.Contains(
                index.Issues,
                issue => issue.Contains("outside", StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(lib, outside); }
    }

    [Fact]
    public void LoadLibrary_RegistryRootedPathIsRejected()
    {
        var lib = MakeLib();
        var outside = Path.Combine(
            Path.GetDirectoryName(lib)!,
            "dct_rooted_" + Path.GetRandomFileName());
        try
        {
            Directory.CreateDirectory(outside);
            File.WriteAllText(Path.Combine(outside, "asset.yml"), "description: rooted\n");
            WriteRegistry(lib, outside);

            var index = new YamlLibraryProvider().LoadLibrary(lib);

            Assert.Empty(index.Assets);
            Assert.Contains(
                index.Issues,
                issue => issue.Contains("relative", StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(lib, outside); }
    }

    [Fact]
    public void LoadLibrary_RegistrySiblingPrefixEscapeIsRejected()
    {
        var lib = MakeLib();
        var sibling = lib + "-archive";
        try
        {
            Directory.CreateDirectory(sibling);
            File.WriteAllText(Path.Combine(sibling, "asset.yml"), "description: sibling\n");
            WriteRegistry(
                lib,
                Path.GetRelativePath(lib, sibling).Replace('\\', '/'));

            var index = new YamlLibraryProvider().LoadLibrary(lib);

            Assert.Empty(index.Assets);
            Assert.Contains(
                index.Issues,
                issue => issue.Contains("outside", StringComparison.OrdinalIgnoreCase));
        }
        finally { Cleanup(lib, sibling); }
    }

    private static string MakeLib()
    {
        var lib = Path.Combine(Path.GetTempPath(), "dct_libtest_" + Path.GetRandomFileName());
        Directory.CreateDirectory(lib);
        return lib;
    }

    private static void WriteRegistry(string libraryRoot, string assetPath)
    {
        var yamlPath = "'" + assetPath.Replace("'", "''") + "'";
        File.WriteAllText(
            Path.Combine(libraryRoot, "library.yml"),
            "schema_version: \"1\"\n" +
            "assets:\n" +
            "  - id: escape\n" +
            "    type: skill\n" +
            "    path: " + yamlPath + "\n");
    }

    private static void Cleanup(params string[] paths)
    {
        foreach (var path in paths)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { }
        }
    }
}
