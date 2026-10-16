#nullable enable
using System;
using System.IO;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Infrastructure.Archive
{
    /// <summary>
    /// Validates the global archive root against the explicitly supplied,
    /// complete portfolio inventory. It deliberately does not inspect
    /// arbitrary directories or infer working sources from Git metadata.
    /// </summary>
    internal static class ArchiveWorkingSourceSafetyValidator
    {
        private const string Remediation =
            "Move source/update registered path or choose a separate archive root.";

        public static ArchiveSyncSafetyResult Validate(ArchiveSyncSafetyRequest? request)
        {
            if (request == null)
            {
                return Blocked("Archive sync safety inventory was not supplied.");
            }

            var inventory = request.WorkingSourceInventory;
            var workingSources = inventory?.WorkingSources;
            if (inventory == null || !inventory.IsComplete || workingSources == null)
            {
                var detail = inventory?.FailureMessage;
                return Blocked(
                    string.IsNullOrWhiteSpace(detail)
                        ? "Archive sync safety inventory is unavailable."
                        : "Archive sync safety inventory could not be established: " + detail);
            }

            if (!ArchiveStorageRootResolver.TryResolve(
                    request.ArchiveStorageRoot,
                    out var archiveStorageRoot))
            {
                return Blocked("Archive storage root must be a safe, fully qualified local folder.");
            }

            foreach (var source in workingSources)
            {
                if (source == null || string.IsNullOrWhiteSpace(source.LocalPath))
                {
                    // Empty local locations represent SSH/remote-only
                    // projects, which have no local source folder to protect.
                    continue;
                }

                if (!WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(
                        source.LocalPath,
                        out var physicalSourcePath))
                {
                    return Blocked(
                        "Cannot prove the registered working source path '" + source.LocalPath +
                        "' for project '" + ProjectLabel(source) + "'. " + Remediation);
                }

                if (PathsOverlap(archiveStorageRoot, physicalSourcePath))
                {
                    return Blocked(
                        "Archive storage root '" + archiveStorageRoot +
                        "' conflicts with registered working source '" + physicalSourcePath +
                        "' for project '" + ProjectLabel(source) + "'. " + Remediation);
                }
            }

            return new ArchiveSyncSafetyResult(true, archiveStorageRoot, string.Empty);
        }

        private static ArchiveSyncSafetyResult Blocked(string message) =>
            new ArchiveSyncSafetyResult(false, string.Empty, message);

        private static string ProjectLabel(ArchiveWorkingSource source) =>
            string.IsNullOrWhiteSpace(source.ProjectId) ? "unknown" : source.ProjectId;

        private static bool PathsOverlap(string first, string second) =>
            IsSameOrChildPath(first, second) || IsSameOrChildPath(second, first);

        private static bool IsSameOrChildPath(string path, string root)
        {
            try
            {
                var normalizedPath = Path.GetFullPath(path)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var normalizedRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
                    normalizedPath.StartsWith(
                        normalizedRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }
    }
}
