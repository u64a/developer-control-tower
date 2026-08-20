using System.Collections.Generic;
using System.IO;
using System.Linq;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Library;

namespace ControlTower.Tests;

public class LocalAssetTransferServiceTests
{
    [Fact]
    public void PreparePush_NewAsset_AllChangesAreNew()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "hello",
                ["sub/file.md"] = "x",
            });
            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };

            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            Assert.Empty(plan.Warnings);
            Assert.Equal(2, plan.Changes.Count);
            Assert.All(plan.Changes, c => Assert.Equal(FileChangeKind.New, c.Kind));
            Assert.All(plan.Changes, c => Assert.True(c.Apply));
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void PreparePush_IdenticalFile_DetectedAsIdenticalAndNotApplied()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "same",
            });
            // Pre-populate target with same content
            var resolvedTarget = Path.Combine(target, ".github", "skills", "demo");
            Directory.CreateDirectory(resolvedTarget);
            File.WriteAllText(Path.Combine(resolvedTarget, "README.md"), "same");

            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            Assert.Single(plan.Changes);
            Assert.Equal(FileChangeKind.Identical, plan.Changes[0].Kind);
            Assert.False(plan.Changes[0].Apply);
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void PreparePush_ModifiedFile_DefaultsToNotApplied()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "newer",
            });
            var resolvedTarget = Path.Combine(target, ".github", "skills", "demo");
            Directory.CreateDirectory(resolvedTarget);
            File.WriteAllText(Path.Combine(resolvedTarget, "README.md"), "older");

            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            Assert.Equal(FileChangeKind.Modified, plan.Changes[0].Kind);
            Assert.False(plan.Changes[0].Apply);
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void PreparePush_AssetYmlIsExcluded()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "hi",
                ["asset.yml"] = "id: demo",
            });

            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            Assert.DoesNotContain(plan.Changes, c => c.RelativePath.Contains("asset.yml"));
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void PreparePush_FileCollection_OnlyManifestFilesIncluded()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["a.md"] = "a",
                ["b.md"] = "b",
                ["c.md"] = "c",
            });
            asset.Files = new List<string> { "a.md", "b.md" };

            var type = new AssetType { Id = "md-file", Layout = AssetLayout.FileCollection, DefaultTarget = "docs/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            Assert.Equal(2, plan.Changes.Count);
            Assert.DoesNotContain(plan.Changes, c => c.RelativePath == "c.md");
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void PreparePush_FileCollection_RootedFilePathRejected()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["a.md"] = "a",
            });
            asset.Files = new List<string> { @"C:\evil\a.md", "../escape.md" };

            var type = new AssetType { Id = "md-file", Layout = AssetLayout.FileCollection, DefaultTarget = "docs/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            Assert.Empty(plan.Changes);
            Assert.NotEmpty(plan.Warnings);
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void ApplyPush_OnlyAppliesCheckedChanges()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["a.md"] = "a",
                ["b.md"] = "b",
            });

            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            // Uncheck one
            plan.Changes.First(c => c.RelativePath == "b.md").Apply = false;

            var result = svc.ApplyPush(plan);
            Assert.True(result.Success);
            Assert.Equal(1, result.FilesWritten);
            Assert.Equal(1, result.FilesSkipped);

            Assert.True(File.Exists(Path.Combine(target, ".github", "skills", "demo", "a.md")));
            Assert.False(File.Exists(Path.Combine(target, ".github", "skills", "demo", "b.md")));
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void PreparePull_NewProjectFile_AdoptedAsNew()
    {
        var (lib, target) = MakeFolders();
        try
        {
            // Library has README.md
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "library copy",
            });
            // Project has README.md (modified) AND a new file
            var projectAssetFolder = Path.Combine(target, ".github", "skills", "demo");
            Directory.CreateDirectory(projectAssetFolder);
            File.WriteAllText(Path.Combine(projectAssetFolder, "README.md"), "newer in project");
            File.WriteAllText(Path.Combine(projectAssetFolder, "new-rule.md"), "added in project");

            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePull(asset, type, lib, target);

            Assert.Equal(2, plan.Changes.Count);
            var newFile = plan.Changes.First(c => c.RelativePath == "new-rule.md");
            Assert.Equal(FileChangeKind.New, newFile.Kind);
            Assert.True(newFile.Apply);

            var modFile = plan.Changes.First(c => c.RelativePath == "README.md");
            Assert.Equal(FileChangeKind.Modified, modFile.Kind);
            Assert.False(modFile.Apply);
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void PreparePull_NoDeployedCopy_ReturnsWarning()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "x",
            });
            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePull(asset, type, lib, target);

            Assert.Empty(plan.Changes);
            Assert.NotEmpty(plan.Warnings);
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void PreparePull_ApplyPush_CopiesProjectFilesIntoLibrary()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "library copy",
            });
            var projectAssetFolder = Path.Combine(target, ".github", "skills", "demo");
            Directory.CreateDirectory(projectAssetFolder);
            File.WriteAllText(Path.Combine(projectAssetFolder, "new-rule.md"), "added in project");

            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePull(asset, type, lib, target);
            var result = svc.ApplyPush(plan); // pull commits via ApplyPush by design

            Assert.True(result.Success);
            Assert.Equal(1, result.FilesWritten);
            Assert.True(File.Exists(Path.Combine(asset.AbsoluteRoot, "new-rule.md")));
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void ApplyPush_IdenticalWithApplyTrue_ForceCopies()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "same",
            });
            var resolvedTarget = Path.Combine(target, ".github", "skills", "demo");
            Directory.CreateDirectory(resolvedTarget);
            File.WriteAllText(Path.Combine(resolvedTarget, "README.md"), "same");

            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            // User ticked Select All — even identicals get rewritten.
            plan.Changes[0].Apply = true;

            var result = svc.ApplyPush(plan);
            Assert.True(result.Success);
            Assert.Equal(1, result.FilesWritten);
            Assert.Equal(0, result.FilesIdentical);
        }
        finally { Cleanup(lib, target); }
    }

    [Fact]
    public void ApplyPush_IdenticalWithApplyFalse_StillCountsAsIdentical()
    {
        var (lib, target) = MakeFolders();
        try
        {
            var asset = MakeSkillAsset(lib, "demo", new Dictionary<string, string>
            {
                ["README.md"] = "same",
            });
            var resolvedTarget = Path.Combine(target, ".github", "skills", "demo");
            Directory.CreateDirectory(resolvedTarget);
            File.WriteAllText(Path.Combine(resolvedTarget, "README.md"), "same");

            var type = new AssetType { Id = "skill", Layout = AssetLayout.Folder, DefaultTarget = ".github/skills/{asset_id}/" };
            var svc = new LocalAssetTransferService();
            var plan = svc.PreparePush(asset, type, lib, target);

            // Default behaviour — identical, not applied.
            var result = svc.ApplyPush(plan);
            Assert.True(result.Success);
            Assert.Equal(0, result.FilesWritten);
            Assert.Equal(1, result.FilesIdentical);
        }
        finally { Cleanup(lib, target); }
    }

    private static (string lib, string target) MakeFolders()
    {
        var lib = Path.Combine(Path.GetTempPath(), "dct_lib_" + Path.GetRandomFileName());
        var target = Path.Combine(Path.GetTempPath(), "dct_tgt_" + Path.GetRandomFileName());
        Directory.CreateDirectory(lib);
        Directory.CreateDirectory(target);
        return (lib, target);
    }

    private static void Cleanup(string lib, string target)
    {
        try { if (Directory.Exists(lib)) Directory.Delete(lib, true); } catch { }
        try { if (Directory.Exists(target)) Directory.Delete(target, true); } catch { }
    }

    private static LibraryAsset MakeSkillAsset(string libRoot, string id, Dictionary<string, string> files)
    {
        var assetRoot = Path.Combine(libRoot, "skills", id);
        Directory.CreateDirectory(assetRoot);
        foreach (var (rel, content) in files)
        {
            var full = Path.Combine(assetRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, content);
        }
        return new LibraryAsset
        {
            Id = id,
            TypeId = "skill",
            Path = $"skills/{id}",
            AbsoluteRoot = assetRoot,
        };
    }
}
