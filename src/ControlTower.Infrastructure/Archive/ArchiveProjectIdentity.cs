using System;
using System.IO;
using System.Text.RegularExpressions;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Infrastructure.Archive
{
    /// <summary>
    /// Constrains archive project identities to a single, collision-free
    /// Windows directory component.
    /// </summary>
    internal static class ArchiveProjectIdentity
    {
        private static readonly Regex SafeProjectId = new Regex(
            @"^[a-z0-9][a-z0-9._-]*$",
            RegexOptions.CultureInvariant);

        public static bool TryResolveArchivePath(
            string archiveStorageRoot,
            string projectId,
            out string archivePath,
            out string error)
        {
            archivePath = string.Empty;
            error = string.Empty;
            if (!IsSafe(projectId))
            {
                error = "Archive project id must be a lower-case safe directory name.";
                return false;
            }

            if (!ArchiveStorageRootResolver.TryResolve(archiveStorageRoot, out var root))
            {
                error = "Archive storage root could not be resolved safely.";
                return false;
            }

            try
            {
                var candidate = Path.GetFullPath(Path.Combine(root, projectId));
                if (!IsStrictChild(candidate, root) ||
                    AllowedSettingsRoots.HasExistingReparsePoint(candidate))
                {
                    error = "Archive destination is outside its storage root or crosses a reparse point.";
                    return false;
                }

                archivePath = candidate;
                return true;
            }
            catch
            {
                error = "Archive destination could not be resolved safely.";
                return false;
            }
        }

        public static bool IsSafe(string projectId)
        {
            return !string.IsNullOrWhiteSpace(projectId) &&
                projectId.Length == projectId.Trim().Length &&
                !projectId.EndsWith(".", StringComparison.Ordinal) &&
                SafeProjectId.IsMatch(projectId);
        }

        private static bool IsStrictChild(string path, string root)
        {
            try
            {
                var fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return path.StartsWith(
                    fullRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
