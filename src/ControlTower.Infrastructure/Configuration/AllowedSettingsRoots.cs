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
                if (!string.IsNullOrWhiteSpace(path))
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

        public static bool IsUnderAllowedRoot(string fullPath, IReadOnlyList<string> roots)
        {
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                return false;
            }

            string resolved;
            try { resolved = Path.GetFullPath(fullPath); }
            catch { return false; }

            // Reject UNC and extended paths outright.
            if (resolved.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

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
    }
}
