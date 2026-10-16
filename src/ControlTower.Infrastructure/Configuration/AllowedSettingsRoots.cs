using System;
using System.Collections.Generic;
using System.IO;

namespace ControlTower.Infrastructure.Configuration
{
    /// <summary>
    /// Enforces the allowed-roots policy for paths read from
    /// <c>settings.local.yml</c> / global settings. Paths configured by the
    /// user must resolve under a known per-user directory (LocalAppData,
    /// RoamingAppData, UserProfile, OneDrive) — otherwise a malformed or
    /// hostile settings file could point the tool at arbitrary locations.
    /// </summary>
    internal static class AllowedSettingsRoots
    {
        public static IReadOnlyList<string> GetAllowedRoots()
        {
            var roots = new List<string>();

            void Add(string path)
            {
                if (!string.IsNullOrWhiteSpace(path) &&
                    Path.IsPathFullyQualified(path) &&
                    !IsNetworkOrDevicePath(path))
                {
                    try { roots.Add(Path.GetFullPath(path)); }
                    catch { /* ignore unresolvable */ }
                }
            }

            Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            Add(Environment.GetEnvironmentVariable("OneDrive"));
            Add(Environment.GetEnvironmentVariable("OneDriveConsumer"));
            Add(Environment.GetEnvironmentVariable("OneDriveCommercial"));

            return roots;
        }

        /// <summary>
        /// Expands and validates a setting-supplied path without ever using the
        /// process working directory to make a relative value absolute.
        /// Existing junctions and symbolic links are refused, because a
        /// lexical prefix check cannot prove where they lead.
        /// </summary>
        public static bool TryResolveConfiguredPath(
            string configuredPath,
            IReadOnlyList<string> roots,
            out string resolvedPath)
        {
            resolvedPath = string.Empty;
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                return false;
            }

            try
            {
                var expanded = Environment.ExpandEnvironmentVariables(configuredPath.Trim());
                if (!Path.IsPathFullyQualified(expanded) || IsNetworkOrDevicePath(expanded))
                {
                    return false;
                }

                var full = Path.GetFullPath(expanded);
                if (!IsUnderAllowedRoot(full, roots) || HasExistingReparsePoint(full))
                {
                    return false;
                }

                resolvedPath = full;
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool IsUnderAllowedRoot(string fullPath, IReadOnlyList<string> roots)
        {
            if (string.IsNullOrWhiteSpace(fullPath) ||
                !Path.IsPathFullyQualified(fullPath) ||
                IsNetworkOrDevicePath(fullPath))
            {
                return false;
            }

            string resolved;
            try { resolved = Path.GetFullPath(fullPath); }
            catch { return false; }

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root)) continue;
                var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.Equals(resolved, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (resolved.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool IsStrictlyUnderAllowedRoot(string fullPath, IReadOnlyList<string> roots)
        {
            if (!IsUnderAllowedRoot(fullPath, roots))
            {
                return false;
            }

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    continue;
                }

                try
                {
                    var trimmed = Path.GetFullPath(root)
                        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (fullPath.StartsWith(
                            trimmed + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // Invalid trusted roots are ignored just as they are by
                    // GetAllowedRoots.
                }
            }

            return false;
        }

        public static bool IsSameOrChildPath(string path, string root)
        {
            if (string.IsNullOrWhiteSpace(path) ||
                string.IsNullOrWhiteSpace(root) ||
                !Path.IsPathFullyQualified(path) ||
                !Path.IsPathFullyQualified(root))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path);
                var fullRoot = Path.GetFullPath(root)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase) ||
                    fullPath.StartsWith(
                        fullRoot + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Returns false when any existing component resolves through a
        /// junction or symbolic link. Missing trailing components are safe to
        /// create after their real parent has been checked.
        /// </summary>
        public static bool HasExistingReparsePoint(string fullPath)
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathFullyQualified(fullPath))
            {
                return true;
            }

            try
            {
                var normalized = Path.GetFullPath(fullPath);
                var root = Path.GetPathRoot(normalized);
                if (string.IsNullOrWhiteSpace(root))
                {
                    return true;
                }

                var current = root;
                var remainder = normalized.Substring(root.Length);
                foreach (var component in remainder.Split(
                    new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                    StringSplitOptions.RemoveEmptyEntries))
                {
                    current = Path.Combine(current, component);

                    if (!TryGetProvableAttributes(current, out var attributes, out var definitelyMissing))
                    {
                        if (definitelyMissing)
                        {
                            break;
                        }

                        // Access-denied or another unprovable I/O condition:
                        // this component's identity cannot be confirmed, so
                        // it must never be treated as safely absent.
                        return true;
                    }

                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }

        /// <summary>
        /// Reads the raw attributes of a path without ever folding an
        /// unreadable or dangling filesystem object into "does not exist".
        /// <see cref="Directory.Exists(string)"/> and
        /// <see cref="File.Exists(string)"/> both return <see langword="false"/>
        /// for access-denied errors and for a reparse point whose target
        /// cannot be resolved (a dangling junction/symlink) — the same result
        /// they give for a path that is genuinely absent. That would let a
        /// dangling or inaccessible alias be walked past and silently
        /// reconstructed as new "missing" directories.
        /// <see cref="File.GetAttributes(string)"/> inspects the filesystem
        /// entry itself instead of resolving through it, so it still reports
        /// <see cref="FileAttributes.ReparsePoint"/> for a dangling link. Only
        /// an explicit <see cref="FileNotFoundException"/> or
        /// <see cref="DirectoryNotFoundException"/> counts as proof the path
        /// is actually missing; any other failure (access denied, device
        /// error, or anything unexpected) is reported as indeterminate so the
        /// caller can fail closed instead of treating it as absent.
        /// </summary>
        public static bool TryGetProvableAttributes(
            string path,
            out FileAttributes attributes,
            out bool definitelyMissing)
        {
            try
            {
                attributes = File.GetAttributes(path);
                definitelyMissing = false;
                return true;
            }
            catch (FileNotFoundException)
            {
                attributes = default;
                definitelyMissing = true;
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                attributes = default;
                definitelyMissing = true;
                return false;
            }
            catch
            {
                attributes = default;
                definitelyMissing = false;
                return false;
            }
        }

        private static bool IsNetworkOrDevicePath(string path)
        {
            return path.StartsWith(@"\\", StringComparison.Ordinal);
        }
    }
}
