using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ControlTower.Infrastructure.Library
{
    public sealed class YamlLibraryProvider : ILibraryProvider
    {
        public LibraryIndex LoadLibrary(string libraryRoot)
        {
            var index = new LibraryIndex { LibraryRoot = libraryRoot ?? string.Empty };

            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                return index;
            }

            string canonicalLibraryRoot;
            try
            {
                canonicalLibraryRoot = Path.TrimEndingDirectorySeparator(
                    Path.GetFullPath(libraryRoot));
                index.LibraryRoot = canonicalLibraryRoot;
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is IOException ||
                ex is NotSupportedException ||
                ex is UnauthorizedAccessException)
            {
                RecordIssue(index, "Library root could not be safely resolved: " + ex.Message);
                return index;
            }

            var libraryYml = Path.Combine(canonicalLibraryRoot, "library.yml");
            if (!File.Exists(libraryYml))
            {
                return index;
            }

            LibraryYamlDto dto = null;
            try
            {
                var deserializer = new DeserializerBuilder()
                    .WithNamingConvention(UnderscoredNamingConvention.Instance)
                    .IgnoreUnmatchedProperties()
                    .Build();
                dto = deserializer.Deserialize<LibraryYamlDto>(File.ReadAllText(libraryYml));
            }
            catch (Exception ex)
            {
                RecordIssue(index, "library.yml could not be read: " + ex.Message);
                return index;
            }

            if (dto?.AssetTypes != null)
            {
                foreach (var kvp in dto.AssetTypes)
                {
                    index.AssetTypes.Add(new AssetType
                    {
                        Id = kvp.Key ?? string.Empty,
                        Layout = ParseLayout(kvp.Value?.Layout),
                        DefaultTarget = kvp.Value?.DefaultTarget ?? string.Empty,
                        Description = kvp.Value?.Description ?? string.Empty,
                    });
                }
            }

            if (dto?.Assets != null)
            {
                foreach (var assetDto in dto.Assets)
                {
                    if (string.IsNullOrWhiteSpace(assetDto.Id) || string.IsNullOrWhiteSpace(assetDto.Path))
                    {
                        continue;
                    }

                    if (!LibraryPathContainment.TryResolveLocalDescendant(
                            canonicalLibraryRoot,
                            assetDto.Path,
                            inspectRootForReparsePoint: false,
                            out var assetRoot,
                            out var issue))
                    {
                        RecordIssue(
                            index,
                            $"Asset '{SafeLabel(assetDto.Id)}' was not loaded: {issue}");
                        continue;
                    }

                    var asset = LoadAsset(assetRoot, assetDto);
                    if (asset != null)
                    {
                        index.Assets.Add(asset);
                    }
                }
            }

            // Auto-discover: for every declared asset_type, walk the matching
            // type folder (typeId + 's' by convention) and register any
            // sub-folder we don't already know about. Per-asset asset.yml
            // is honoured if present; otherwise sensible defaults apply.
            AutoDiscoverAssets(canonicalLibraryRoot, index);

            return index;
        }

        private static void AutoDiscoverAssets(string libraryRoot, LibraryIndex index)
        {
            var alreadyKnown = new HashSet<string>(
                index.Assets.Select(a => a.TypeId + "::" + a.Id),
                StringComparer.OrdinalIgnoreCase);

            foreach (var type in index.AssetTypes)
            {
                var typeFolder = type.Id + "s";
                if (!LibraryPathContainment.TryResolveLocalDescendant(
                        libraryRoot,
                        typeFolder,
                        inspectRootForReparsePoint: false,
                        out var typeFolderPath,
                        out var typeFolderIssue))
                {
                    RecordIssue(
                        index,
                        $"Asset type '{SafeLabel(type.Id)}' was not auto-discovered: {typeFolderIssue}");
                    continue;
                }

                if (!Directory.Exists(typeFolderPath))
                {
                    continue;
                }

                List<string> assetFolders;
                try
                {
                    assetFolders = Directory.EnumerateDirectories(typeFolderPath).ToList();
                }
                catch (Exception ex) when (
                    ex is IOException ||
                    ex is UnauthorizedAccessException ||
                    ex is DirectoryNotFoundException)
                {
                    RecordIssue(
                        index,
                        $"Asset type '{SafeLabel(type.Id)}' could not be enumerated: {ex.Message}");
                    continue;
                }

                foreach (var assetFolderCandidate in assetFolders)
                {
                    if (!LibraryPathContainment.TryValidateLocalDescendant(
                            libraryRoot,
                            assetFolderCandidate,
                            inspectRootForReparsePoint: false,
                            out var assetFolder,
                            out var assetFolderIssue))
                    {
                        RecordIssue(
                            index,
                            $"An auto-discovered '{SafeLabel(type.Id)}' asset was ignored: {assetFolderIssue}");
                        continue;
                    }

                    var assetId = Path.GetFileName(assetFolder);
                    if (string.IsNullOrWhiteSpace(assetId)) continue;
                    if (alreadyKnown.Contains(type.Id + "::" + assetId)) continue;

                    // Synthetic index entry — LoadAsset will overlay asset.yml
                    // if present, otherwise we fill in sensible defaults below.
                    var synthetic = new LibraryAssetIndexEntry
                    {
                        Id = assetId,
                        Type = type.Id,
                        Path = $"{typeFolder}/{assetId}",
                        Version = "1.0.0",
                        LastUpdated = DateTime.UtcNow.ToString("yyyy-MM-dd"),
                        Description = $"Auto-discovered {type.Id}.",
                    };

                    var asset = LoadAsset(assetFolder, synthetic);
                    if (asset == null) continue;

                    // For FileCollection layout assets without an asset.yml manifest,
                    // populate Files from the folder contents so push/pull include
                    // them automatically.
                    if (type.Layout == AssetLayout.FileCollection &&
                        (asset.Files == null || asset.Files.Count == 0))
                    {
                        try
                        {
                            asset.Files = Directory.EnumerateFiles(assetFolder)
                                .Select(p => Path.GetFileName(p))
                                .Where(n => !string.Equals(n, "asset.yml", StringComparison.OrdinalIgnoreCase))
                                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                                .ToList();
                        }
                        catch
                        {
                            asset.Files = new List<string>();
                        }
                    }

                    index.Assets.Add(asset);
                    alreadyKnown.Add(type.Id + "::" + assetId);
                }
            }
        }

        public LibraryAsset GetAsset(string libraryRoot, string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId))
            {
                return null;
            }

            var index = LoadLibrary(libraryRoot);
            return index.Assets.FirstOrDefault(
                a => string.Equals(a.Id, assetId, StringComparison.OrdinalIgnoreCase));
        }

        public void RegisterAsset(string libraryRoot, LibraryAsset asset, string fromProjectId)
        {
            if (asset == null) throw new ArgumentNullException(nameof(asset));
            if (string.IsNullOrWhiteSpace(libraryRoot) || !Directory.Exists(libraryRoot))
            {
                throw new InvalidOperationException("Library root not found.");
            }

            // Write per-asset asset.yml.
            if (!LibraryPathContainment.TryResolveLocalDescendant(
                    libraryRoot,
                    asset.Path,
                    inspectRootForReparsePoint: false,
                    out var assetRoot,
                    out var issue))
            {
                throw new InvalidOperationException("Asset path rejected: " + issue);
            }

            Directory.CreateDirectory(assetRoot);
            WriteAssetYml(assetRoot, asset, fromProjectId);

            // Append to library.yml's assets list.
            var libraryYml = Path.Combine(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(libraryRoot)),
                "library.yml");
            AppendAssetToRegistry(libraryYml, asset);
        }

        public void TouchAsset(string libraryRoot, string assetId, DateTime updatedUtc, string fromProjectId)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot) || string.IsNullOrWhiteSpace(assetId))
            {
                return;
            }

            var asset = GetAsset(libraryRoot, assetId);
            if (asset == null || string.IsNullOrWhiteSpace(asset.AbsoluteRoot) || !Directory.Exists(asset.AbsoluteRoot))
            {
                return;
            }

            if (!LibraryPathContainment.TryValidateLocalDescendant(
                    libraryRoot,
                    asset.AbsoluteRoot,
                    inspectRootForReparsePoint: false,
                    out var assetRoot,
                    out var issue))
            {
                throw new InvalidOperationException("Asset path rejected: " + issue);
            }

            // Round-trip the existing asset.yml so we preserve description/tags/
            // files/default_target/version etc., overlaying only last_updated and
            // a new source_history entry.
            var assetYmlPath = Path.Combine(assetRoot, "asset.yml");
            AssetYamlDto detail = null;
            if (File.Exists(assetYmlPath))
            {
                try
                {
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                    detail = deserializer.Deserialize<AssetYamlDto>(File.ReadAllText(assetYmlPath));
                }
                catch
                {
                    detail = null;
                }
            }
            detail ??= new AssetYamlDto();

            // Fill in anything missing from the in-memory asset so we don't lose
            // metadata that was only in library.yml's index.
            if (string.IsNullOrWhiteSpace(detail.Id)) detail.Id = asset.Id;
            if (string.IsNullOrWhiteSpace(detail.Type)) detail.Type = asset.TypeId;
            if (string.IsNullOrWhiteSpace(detail.Version)) detail.Version = string.IsNullOrWhiteSpace(asset.Version) ? "1.0.0" : asset.Version;
            if (string.IsNullOrWhiteSpace(detail.Description)) detail.Description = asset.Description ?? string.Empty;
            if (string.IsNullOrWhiteSpace(detail.DefaultTarget) && !string.IsNullOrWhiteSpace(asset.DefaultTargetOverride))
            {
                detail.DefaultTarget = asset.DefaultTargetOverride;
            }
            if ((detail.Tags == null || detail.Tags.Count == 0) && asset.Tags != null && asset.Tags.Count > 0)
            {
                detail.Tags = new List<string>(asset.Tags);
            }
            if ((detail.Files == null || detail.Files.Count == 0) && asset.Files != null && asset.Files.Count > 0)
            {
                detail.Files = new List<string>(asset.Files);
            }

            detail.LastUpdated = updatedUtc.ToUniversalTime().ToString("yyyy-MM-dd");

            // Build YAML
            var sb = new StringBuilder();
            sb.AppendLine("id: " + Yaml.YamlScalar.Quote(detail.Id));
            sb.AppendLine("type: " + Yaml.YamlScalar.Quote(detail.Type));
            sb.AppendLine("version: " + Yaml.YamlScalar.Quote(detail.Version));
            sb.AppendLine("last_updated: " + Yaml.YamlScalar.Quote(detail.LastUpdated));
            if (!string.IsNullOrWhiteSpace(detail.DefaultTarget))
            {
                sb.AppendLine("default_target: " + Yaml.YamlScalar.Quote(detail.DefaultTarget));
            }
            sb.AppendLine("description: " + Yaml.YamlScalar.Quote(detail.Description ?? string.Empty));
            if (detail.Tags != null && detail.Tags.Count > 0)
            {
                sb.AppendLine("tags:");
                foreach (var t in detail.Tags)
                {
                    sb.AppendLine("  - " + Yaml.YamlScalar.Quote(t));
                }
            }
            if (detail.Files != null && detail.Files.Count > 0)
            {
                sb.AppendLine("files:");
                foreach (var f in detail.Files)
                {
                    sb.AppendLine("  - " + Yaml.YamlScalar.Quote(f));
                }
            }
            sb.AppendLine("source_history:");
            // We don't currently round-trip existing entries from asset.yml
            // (no DTO fields for them), so this becomes the latest entry only.
            // Audit log captures the full append-only history.
            if (!string.IsNullOrWhiteSpace(fromProjectId))
            {
                sb.AppendLine("  - from_project: " + Yaml.YamlScalar.Quote(fromProjectId));
                sb.AppendLine("    action: pull");
                sb.AppendLine("    on: " + updatedUtc.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"));
            }

            // Atomic write
            var tmp = assetYmlPath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Copy(tmp, assetYmlPath, true);
            try { File.Delete(tmp); } catch { }
        }

        private static void WriteAssetYml(string assetRoot, LibraryAsset asset, string fromProjectId)
        {
            var sb = new StringBuilder();
            sb.AppendLine("id: " + Yaml.YamlScalar.Quote(asset.Id));
            sb.AppendLine("type: " + Yaml.YamlScalar.Quote(asset.TypeId));
            sb.AppendLine("version: " + Yaml.YamlScalar.Quote(string.IsNullOrWhiteSpace(asset.Version) ? "1.0.0" : asset.Version));
            sb.AppendLine("last_updated: " + Yaml.YamlScalar.Quote(string.IsNullOrWhiteSpace(asset.LastUpdated) ? DateTime.UtcNow.ToString("yyyy-MM-dd") : asset.LastUpdated));
            if (!string.IsNullOrWhiteSpace(asset.DefaultTargetOverride))
            {
                sb.AppendLine("default_target: " + Yaml.YamlScalar.Quote(asset.DefaultTargetOverride));
            }
            sb.AppendLine("description: " + Yaml.YamlScalar.Quote(asset.Description ?? string.Empty));
            if (asset.Tags != null && asset.Tags.Count > 0)
            {
                sb.AppendLine("tags:");
                foreach (var t in asset.Tags)
                {
                    sb.AppendLine("  - " + Yaml.YamlScalar.Quote(t));
                }
            }
            if (asset.Files != null && asset.Files.Count > 0)
            {
                sb.AppendLine("files:");
                foreach (var f in asset.Files)
                {
                    sb.AppendLine("  - " + Yaml.YamlScalar.Quote(f));
                }
            }
            sb.AppendLine("source_history:");
            if (!string.IsNullOrWhiteSpace(fromProjectId))
            {
                sb.AppendLine("  - from_project: " + Yaml.YamlScalar.Quote(fromProjectId));
                sb.AppendLine("    on: " + DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
            }

            var path = Path.Combine(assetRoot, "asset.yml");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static void AppendAssetToRegistry(string libraryYml, LibraryAsset asset)
        {
            // Read existing registry (or build a fresh one if missing).
            LibraryYamlDto dto = null;
            if (File.Exists(libraryYml))
            {
                try
                {
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                    dto = deserializer.Deserialize<LibraryYamlDto>(File.ReadAllText(libraryYml));
                }
                catch
                {
                    dto = null;
                }
            }
            dto ??= new LibraryYamlDto();
            dto.Assets ??= new List<LibraryAssetIndexEntry>();

            // Skip if already registered.
            if (dto.Assets.Any(a => string.Equals(a.Id, asset.Id, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            dto.Assets.Add(new LibraryAssetIndexEntry
            {
                Id = asset.Id,
                Type = asset.TypeId,
                Path = asset.Path,
                Version = string.IsNullOrWhiteSpace(asset.Version) ? "1.0.0" : asset.Version,
                LastUpdated = string.IsNullOrWhiteSpace(asset.LastUpdated)
                    ? DateTime.UtcNow.ToString("yyyy-MM-dd") : asset.LastUpdated,
                Description = asset.Description ?? string.Empty,
            });

            // Emit a deterministic library.yml.
            var sb = new StringBuilder();
            sb.AppendLine("# Developer Control Tower asset library registry");
            sb.AppendLine("schema_version: " + Yaml.YamlScalar.Quote("1"));
            sb.AppendLine("generated_on: " + Yaml.YamlScalar.Quote(DateTime.UtcNow.ToString("yyyy-MM-dd")));
            sb.AppendLine("last_updated: " + Yaml.YamlScalar.Quote(DateTime.UtcNow.ToString("yyyy-MM-dd")));
            sb.AppendLine();

            if (dto.AssetTypes != null && dto.AssetTypes.Count > 0)
            {
                sb.AppendLine("asset_types:");
                foreach (var kvp in dto.AssetTypes)
                {
                    sb.AppendLine("  " + Yaml.YamlScalar.Quote(kvp.Key) + ":");
                    if (!string.IsNullOrWhiteSpace(kvp.Value?.Layout))
                    {
                        sb.AppendLine("    layout: " + Yaml.YamlScalar.Quote(kvp.Value.Layout));
                    }
                    if (!string.IsNullOrWhiteSpace(kvp.Value?.DefaultTarget))
                    {
                        sb.AppendLine("    default_target: " + Yaml.YamlScalar.Quote(kvp.Value.DefaultTarget));
                    }
                    if (!string.IsNullOrWhiteSpace(kvp.Value?.Description))
                    {
                        sb.AppendLine("    description: " + Yaml.YamlScalar.Quote(kvp.Value.Description));
                    }
                }
                sb.AppendLine();
            }

            sb.AppendLine("assets:");
            foreach (var a in dto.Assets)
            {
                sb.AppendLine("  - id: " + Yaml.YamlScalar.Quote(a.Id ?? string.Empty));
                sb.AppendLine("    type: " + Yaml.YamlScalar.Quote(a.Type ?? string.Empty));
                sb.AppendLine("    path: " + Yaml.YamlScalar.Quote(a.Path ?? string.Empty));
                if (!string.IsNullOrWhiteSpace(a.Version))
                {
                    sb.AppendLine("    version: " + Yaml.YamlScalar.Quote(a.Version));
                }
                if (!string.IsNullOrWhiteSpace(a.LastUpdated))
                {
                    sb.AppendLine("    last_updated: " + Yaml.YamlScalar.Quote(a.LastUpdated));
                }
                if (!string.IsNullOrWhiteSpace(a.Description))
                {
                    sb.AppendLine("    description: " + Yaml.YamlScalar.Quote(a.Description));
                }
            }

            sb.AppendLine();
            sb.AppendLine("audit: []");

            // Atomic write
            var tmp = libraryYml + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Copy(tmp, libraryYml, true);
            try { File.Delete(tmp); } catch { }
        }

        private static LibraryAsset LoadAsset(string assetRoot, LibraryAssetIndexEntry indexEntry)
        {
            var asset = new LibraryAsset
            {
                Id = indexEntry.Id,
                TypeId = indexEntry.Type ?? string.Empty,
                Path = indexEntry.Path,
                Version = indexEntry.Version ?? string.Empty,
                LastUpdated = indexEntry.LastUpdated ?? string.Empty,
                Description = indexEntry.Description ?? string.Empty,
                AbsoluteRoot = assetRoot,
            };

            // Per-asset asset.yml takes precedence.
            var assetYml = Path.Combine(assetRoot, "asset.yml");
            if (File.Exists(assetYml))
            {
                try
                {
                    var deserializer = new DeserializerBuilder()
                        .WithNamingConvention(UnderscoredNamingConvention.Instance)
                        .IgnoreUnmatchedProperties()
                        .Build();
                    var detail = deserializer.Deserialize<AssetYamlDto>(File.ReadAllText(assetYml));

                    if (detail != null)
                    {
                        if (!string.IsNullOrWhiteSpace(detail.Description))
                        {
                            asset.Description = detail.Description;
                        }
                        if (!string.IsNullOrWhiteSpace(detail.Version))
                        {
                            asset.Version = detail.Version;
                        }
                        if (!string.IsNullOrWhiteSpace(detail.LastUpdated))
                        {
                            asset.LastUpdated = detail.LastUpdated;
                        }
                        if (!string.IsNullOrWhiteSpace(detail.DefaultTarget))
                        {
                            asset.DefaultTargetOverride = detail.DefaultTarget;
                        }
                        if (detail.Tags != null)
                        {
                            asset.Tags = detail.Tags;
                        }
                        if (detail.Files != null)
                        {
                            asset.Files = detail.Files;
                        }
                    }
                }
                catch (Exception)
                {
                    // Ignore malformed asset.yml — we still surface the asset from the index.
                }
            }

            return asset;
        }

        private static AssetLayout ParseLayout(string value)
        {
            if (string.Equals(value, "file_collection", StringComparison.OrdinalIgnoreCase))
            {
                return AssetLayout.FileCollection;
            }
            return AssetLayout.Folder;
        }

        private static void RecordIssue(LibraryIndex index, string issue)
        {
            index.Issues.Add(issue);
            AppLogger.Warn("library.load", issue);
        }

        private static string SafeLabel(string value)
        {
            return (value ?? string.Empty)
                .Replace('\r', ' ')
                .Replace('\n', ' ');
        }

        private sealed class LibraryYamlDto
        {
            [YamlMember(Alias = "asset_types")]
            public Dictionary<string, AssetTypeDto> AssetTypes { get; set; }

            public List<LibraryAssetIndexEntry> Assets { get; set; }
        }

        private sealed class AssetTypeDto
        {
            public string Layout { get; set; }

            [YamlMember(Alias = "default_target")]
            public string DefaultTarget { get; set; }

            public string Description { get; set; }
        }

        private sealed class LibraryAssetIndexEntry
        {
            public string Id { get; set; }
            public string Type { get; set; }
            public string Path { get; set; }
            public string Version { get; set; }

            [YamlMember(Alias = "last_updated")]
            public string LastUpdated { get; set; }

            public string Description { get; set; }
        }

        private sealed class AssetYamlDto
        {
            public string Id { get; set; }
            public string Type { get; set; }
            public string Version { get; set; }

            [YamlMember(Alias = "last_updated")]
            public string LastUpdated { get; set; }

            [YamlMember(Alias = "default_target")]
            public string DefaultTarget { get; set; }

            public string Description { get; set; }
            public List<string> Tags { get; set; }
            public List<string> Files { get; set; }
        }
    }
}
