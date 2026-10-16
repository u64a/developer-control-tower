using System;
using System.IO;

namespace ControlTower.Infrastructure.Configuration
{
    /// <summary>
    /// Resolves the one storage root used by every archive safety-net clone.
    /// The fallback is deliberately below the user profile, never the profile
    /// directory itself or the replaceable application installation. A
    /// configured root may be any safe, fully qualified local folder.
    /// </summary>
    public static class ArchiveStorageRootResolver
    {
        private const string ArchiveFolderName = "Archive";

        public static string GetDefaultRoot()
        {
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return string.IsNullOrWhiteSpace(userProfile)
                ? string.Empty
                : Path.Combine(userProfile, AppPathsResolver.AppFolderName, ArchiveFolderName);
        }

        /// <summary>
        /// Resolves a configured root or the default when it is blank. Invalid
        /// configured values return false rather than being treated as a
        /// configured archive destination.
        /// </summary>
        public static bool TryResolve(string configuredRoot, out string effectiveRoot)
        {
            var candidate = string.IsNullOrWhiteSpace(configuredRoot)
                ? GetDefaultRoot()
                : configuredRoot.Trim();

            if (string.IsNullOrWhiteSpace(candidate))
            {
                effectiveRoot = string.Empty;
                return false;
            }

            try
            {
                if (!TryResolveSafeLocalArchiveRoot(candidate, out var fullPath))
                {
                    effectiveRoot = string.Empty;
                    return false;
                }

                effectiveRoot = fullPath;
                return true;
            }
            catch
            {
                effectiveRoot = string.Empty;
                return false;
            }
        }

        private static bool TryResolveSafeLocalArchiveRoot(string candidate, out string fullPath)
        {
            fullPath = string.Empty;
            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(candidate.Trim());
                if (!Path.IsPathFullyQualified(expanded) ||
                    expanded.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    return false;
                }

                var normalized = Path.GetFullPath(expanded);
                if (IsDriveRoot(normalized) ||
                    !WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(
                        normalized,
                        out var physicalPath) ||
                    IsDriveRoot(physicalPath) ||
                    IsUserProfileRoot(physicalPath) ||
                    IsInApplicationInstallDirectory(physicalPath))
                {
                    return false;
                }

                fullPath = physicalPath;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool IsDriveRoot(string path)
        {
            var root = Path.GetPathRoot(path);
            return !string.IsNullOrWhiteSpace(root) &&
                string.Equals(
                    path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsUserProfileRoot(string path)
        {
            try
            {
                var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return string.IsNullOrWhiteSpace(profile) ||
                    !WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(
                        path,
                        out var physicalPath) ||
                    !WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(
                        profile,
                        out var physicalProfile) ||
                    string.Equals(physicalPath, physicalProfile, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return true;
            }
        }

        private static bool IsSameOrChildPath(string path, string root)
        {
            try
            {
                var trimmedRoot = root
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(path, trimmedRoot, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(
                        trimmedRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool IsInApplicationInstallDirectory(string path)
        {
            try
            {
                var installDirectory = AppContext.BaseDirectory;
                return string.IsNullOrWhiteSpace(installDirectory) ||
                    !WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(
                        path,
                        out var physicalPath) ||
                    !WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(
                        installDirectory,
                        out var physicalInstallDirectory) ||
                    IsSameOrChildPath(physicalPath, physicalInstallDirectory);
            }
            catch
            {
                // If the installation directory cannot be established, do
                // not accept a root based on an unprovable safety check.
                return true;
            }
        }
    }
}
