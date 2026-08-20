using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Library
{
    /// <summary>
    /// Pushes assets to a local target project. Diffs by SHA256, preview-first,
    /// no deletes (Phase 1), strict path-traversal guards.
    /// </summary>
    public sealed class LocalAssetTransferService : IAssetTransferService
    {
        // Files inside an asset that describe metadata, not payload — never pushed.
        private static readonly HashSet<string> ExcludedFileNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "asset.yml",
        };

        public AssetPushPlan PreparePush(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            string targetProjectRoot,
            IEnumerable<string> includedFiles = null)
        {
            if (asset == null)
            {
                throw new ArgumentNullException(nameof(asset));
            }
            if (assetType == null)
            {
                throw new ArgumentNullException(nameof(assetType));
            }
            if (string.IsNullOrWhiteSpace(targetProjectRoot))
            {
                throw new ArgumentException("Target project root is required.", nameof(targetProjectRoot));
            }

            var plan = new AssetPushPlan
            {
                Asset = asset,
                TargetRoot = targetProjectRoot,
            };

            // Resolve target: asset override beats type default. Substitute {asset_id}.
            var targetRel = !string.IsNullOrWhiteSpace(asset.DefaultTargetOverride)
                ? asset.DefaultTargetOverride
                : assetType.DefaultTarget;
            targetRel = (targetRel ?? string.Empty).Replace("{asset_id}", asset.Id);
            targetRel = targetRel.Replace('/', Path.DirectorySeparatorChar);

            var resolvedTarget = Path.GetFullPath(Path.Combine(targetProjectRoot, targetRel));
            var rootFull = Path.GetFullPath(targetProjectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsUnder(resolvedTarget, rootFull))
            {
                plan.Warnings.Add("Resolved target path falls outside the project root. Aborting plan.");
                return plan;
            }
            plan.ResolvedTargetPath = resolvedTarget;

            if (string.IsNullOrWhiteSpace(asset.AbsoluteRoot) || !Directory.Exists(asset.AbsoluteRoot))
            {
                plan.Warnings.Add($"Asset folder not found on disk: {asset.AbsoluteRoot}");
                return plan;
            }

            // Build the list of source files relative to the asset root.
            List<string> sourceFiles;
            if (assetType.Layout == AssetLayout.FileCollection)
            {
                // Manifest-driven. Validate every entry sits inside the asset root.
                var manifest = (asset.Files ?? new List<string>())
                    .Where(f => !string.IsNullOrWhiteSpace(f))
                    .Select(f => f.Replace('/', Path.DirectorySeparatorChar))
                    .ToList();

                if (includedFiles != null)
                {
                    var requestedSet = new HashSet<string>(
                        includedFiles.Select(f => f.Replace('/', Path.DirectorySeparatorChar)),
                        StringComparer.OrdinalIgnoreCase);
                    manifest = manifest.Where(m => requestedSet.Contains(m)).ToList();
                }

                sourceFiles = new List<string>();
                foreach (var rel in manifest)
                {
                    if (!IsSafeRelative(rel))
                    {
                        plan.Warnings.Add($"Skipping unsafe file path '{rel}'.");
                        continue;
                    }
                    var srcAbs = Path.GetFullPath(Path.Combine(asset.AbsoluteRoot, rel));
                    if (!IsUnder(srcAbs, asset.AbsoluteRoot))
                    {
                        plan.Warnings.Add($"Skipping file '{rel}' (escapes asset root).");
                        continue;
                    }
                    if (!File.Exists(srcAbs))
                    {
                        plan.Warnings.Add($"Manifest file '{rel}' missing on disk.");
                        continue;
                    }
                    sourceFiles.Add(rel);
                }
            }
            else
            {
                // Folder layout — walk the asset root, exclude metadata + reparse points.
                sourceFiles = WalkAssetFiles(asset.AbsoluteRoot, plan.Warnings);
            }

            foreach (var rel in sourceFiles)
            {
                var srcAbs = Path.Combine(asset.AbsoluteRoot, rel);
                var tgtAbs = Path.GetFullPath(Path.Combine(plan.ResolvedTargetPath, rel));

                // Defence in depth: every per-file target must stay inside resolved target root.
                if (!IsUnder(tgtAbs, plan.ResolvedTargetPath))
                {
                    plan.Warnings.Add($"Skipping '{rel}' (target escapes resolved root).");
                    continue;
                }

                FileChangeKind kind;
                long? targetSize = null;
                if (!File.Exists(tgtAbs))
                {
                    kind = FileChangeKind.New;
                }
                else
                {
                    targetSize = new FileInfo(tgtAbs).Length;
                    if (HashEquals(srcAbs, tgtAbs))
                    {
                        kind = FileChangeKind.Identical;
                    }
                    else
                    {
                        kind = FileChangeKind.Modified;
                    }
                }

                plan.Changes.Add(new FileChange
                {
                    RelativePath = rel,
                    Kind = kind,
                    SourceAbsolutePath = srcAbs,
                    TargetAbsolutePath = tgtAbs,
                    SourceSize = new FileInfo(srcAbs).Length,
                    TargetSize = targetSize,
                    Apply = kind == FileChangeKind.New, // Modified defaults off (safer)
                });
            }

            return plan;
        }

        public AssetPushResult ApplyPush(AssetPushPlan plan)
        {
            if (plan == null)
            {
                throw new ArgumentNullException(nameof(plan));
            }

            var result = new AssetPushResult();

            if (string.IsNullOrWhiteSpace(plan.ResolvedTargetPath))
            {
                result.Success = false;
                result.Message = "No resolved target path. Push aborted.";
                return result;
            }

            try
            {
                foreach (var change in plan.Changes)
                {
                    // The Apply checkbox is the user's intent — let it win even
                    // when the file is identical (forces a rewrite).
                    if (!change.Apply)
                    {
                        if (change.Kind == FileChangeKind.Identical)
                        {
                            result.FilesIdentical++;
                        }
                        else
                        {
                            result.FilesSkipped++;
                        }
                        continue;
                    }

                    // Defence in depth — reject anything that doesn't stay under the resolved target root.
                    if (!IsUnder(change.TargetAbsolutePath, plan.ResolvedTargetPath))
                    {
                        result.FilesSkipped++;
                        continue;
                    }

                    var dir = Path.GetDirectoryName(change.TargetAbsolutePath);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    {
                        Directory.CreateDirectory(dir);
                    }

                    File.Copy(change.SourceAbsolutePath, change.TargetAbsolutePath, overwrite: true);
                    result.FilesWritten++;
                }
            }
            catch (Exception ex)
            {
                result.Success = false;
                result.Message = "Push failed: " + ex.Message;
                return result;
            }

            result.Success = true;
            result.Message = $"Pushed {result.FilesWritten} file(s); skipped {result.FilesSkipped}; {result.FilesIdentical} identical.";
            return result;
        }

        public AssetPushPlan PreparePull(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            string sourceProjectRoot)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (assetType == null) throw new ArgumentNullException(nameof(assetType));
            if (string.IsNullOrWhiteSpace(sourceProjectRoot))
            {
                throw new ArgumentException("Source project root is required.", nameof(sourceProjectRoot));
            }

            var plan = new AssetPushPlan
            {
                Asset = asset,
                TargetRoot = sourceProjectRoot,
            };

            // Resolve the project-side asset folder using the same target template
            // we'd use for push. That's the canonical place a deployed asset lives.
            var targetRel = !string.IsNullOrWhiteSpace(asset.DefaultTargetOverride)
                ? asset.DefaultTargetOverride
                : assetType.DefaultTarget;
            targetRel = (targetRel ?? string.Empty).Replace("{asset_id}", asset.Id);
            targetRel = targetRel.Replace('/', Path.DirectorySeparatorChar);

            var projectAssetFolder = Path.GetFullPath(Path.Combine(sourceProjectRoot, targetRel));
            var rootFull = Path.GetFullPath(sourceProjectRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsUnder(projectAssetFolder, rootFull))
            {
                plan.Warnings.Add("Resolved project asset path falls outside the project root. Aborting plan.");
                return plan;
            }

            if (!Directory.Exists(projectAssetFolder))
            {
                plan.Warnings.Add($"No deployed copy of '{asset.Id}' found at {projectAssetFolder}.");
                return plan;
            }

            // Library destination — must stay inside the asset's own folder.
            if (string.IsNullOrWhiteSpace(asset.AbsoluteRoot))
            {
                plan.Warnings.Add("Library asset root not known. Aborting plan.");
                return plan;
            }
            plan.ResolvedTargetPath = asset.AbsoluteRoot;

            // Walk the project-side folder and build a plan whose Source = project,
            // Target = library. ApplyPush will then copy project -> library.
            foreach (var srcAbs in Directory.EnumerateFiles(projectAssetFolder, "*", SearchOption.AllDirectories))
            {
                var info = new FileInfo(srcAbs);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (ExcludedFileNames.Contains(info.Name)) continue;

                var rel = Path.GetRelativePath(projectAssetFolder, srcAbs);

                // For FileCollection assets, only consider files in the asset's manifest.
                if (assetType.Layout == AssetLayout.FileCollection)
                {
                    var manifest = (asset.Files ?? new List<string>())
                        .Select(f => f.Replace('/', Path.DirectorySeparatorChar))
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);
                    if (!manifest.Contains(rel))
                    {
                        continue;
                    }
                }

                var tgtAbs = Path.GetFullPath(Path.Combine(asset.AbsoluteRoot, rel));
                if (!IsUnder(tgtAbs, asset.AbsoluteRoot))
                {
                    plan.Warnings.Add($"Skipping '{rel}' (target escapes library asset root).");
                    continue;
                }

                FileChangeKind kind;
                long? targetSize = null;
                if (!File.Exists(tgtAbs))
                {
                    kind = FileChangeKind.New;
                }
                else
                {
                    targetSize = new FileInfo(tgtAbs).Length;
                    kind = HashEquals(srcAbs, tgtAbs) ? FileChangeKind.Identical : FileChangeKind.Modified;
                }

                plan.Changes.Add(new FileChange
                {
                    RelativePath = rel,
                    Kind = kind,
                    SourceAbsolutePath = srcAbs,
                    TargetAbsolutePath = tgtAbs,
                    SourceSize = info.Length,
                    TargetSize = targetSize,
                    Apply = kind == FileChangeKind.New, // safer default for pull too
                });
            }

            return plan;
        }

        private static List<string> WalkAssetFiles(string root, IList<string> warnings)
        {
            var files = new List<string>();
            var rootInfo = new DirectoryInfo(root);
            WalkInto(rootInfo, root, files, warnings);
            return files;
        }

        private static void WalkInto(DirectoryInfo dir, string assetRoot, List<string> files, IList<string> warnings)
        {
            if ((dir.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                warnings.Add($"Skipping reparse point '{dir.FullName}'.");
                return;
            }

            foreach (var file in dir.EnumerateFiles())
            {
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add($"Skipping reparse point file '{file.FullName}'.");
                    continue;
                }
                if (ExcludedFileNames.Contains(file.Name))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(assetRoot, file.FullName);
                files.Add(rel);
            }

            foreach (var sub in dir.EnumerateDirectories())
            {
                WalkInto(sub, assetRoot, files, warnings);
            }
        }

        private static bool IsSafeRelative(string rel)
        {
            if (string.IsNullOrWhiteSpace(rel))
            {
                return false;
            }
            if (Path.IsPathRooted(rel))
            {
                return false;
            }
            // Reject any segment that is .. or contains invalid chars.
            var segments = rel.Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            return segments.All(s => s != ".." && s.IndexOfAny(Path.GetInvalidFileNameChars()) < 0);
        }

        private static bool IsUnder(string path, string root)
        {
            var p = Path.GetFullPath(path);
            var r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(p, r, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HashEquals(string a, string b)
        {
            using var sha = SHA256.Create();
            using var sa = File.OpenRead(a);
            using var sb = File.OpenRead(b);
            var ha = sha.ComputeHash(sa);
            var hb = sha.ComputeHash(sb);
            if (ha.Length != hb.Length) return false;
            for (var i = 0; i < ha.Length; i++)
            {
                if (ha[i] != hb[i]) return false;
            }
            return true;
        }
    }
}
