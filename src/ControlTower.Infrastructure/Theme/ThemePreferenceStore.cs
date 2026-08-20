#nullable enable
using System;
using System.IO;
using System.Text;

namespace ControlTower.Infrastructure.Theme
{
    /// <summary>
    /// User's persisted theme choice. <see cref="System"/> means "follow the
    /// OS preference" (no override on disk). <see cref="Dark"/> and
    /// <see cref="Light"/> are explicit overrides that survive restart.
    /// </summary>
    public enum ThemePreference
    {
        System = 0,
        Dark = 1,
        Light = 2
    }

    /// <summary>
    /// Reads and writes the user's persisted theme preference.
    /// Stored as a single lower-case token (<c>dark</c> or <c>light</c>) in
    /// a tiny text file under <c>%LOCALAPPDATA%\DeveloperControlTower</c>.
    /// Absent or unparseable file means "follow the OS preference"
    /// (<see cref="ThemePreference.System"/>).
    ///
    /// Per-machine (not synced via OneDrive) because theme is a per-display
    /// concern - the same user may prefer different themes on different
    /// machines or monitor profiles. Survives uninstall via LOCALAPPDATA.
    /// </summary>
    public sealed class ThemePreferenceStore
    {
        public const string FileName = "theme.txt";
        private const string DefaultFolderName = "DeveloperControlTower";

        private readonly Func<string> _folderProvider;

        public ThemePreferenceStore()
            : this(folderProvider: null)
        {
        }

        // Test seam: lets tests redirect the folder so they don't write to
        // the real %LOCALAPPDATA%.
        public ThemePreferenceStore(Func<string>? folderProvider)
        {
            _folderProvider = folderProvider ?? DefaultFolder;
        }

        private static string DefaultFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DefaultFolderName);
        }

        /// <summary>
        /// Reads the persisted preference. Returns
        /// <see cref="ThemePreference.System"/> when the file is missing,
        /// empty, unparseable, or otherwise unreadable - never throws.
        /// </summary>
        public ThemePreference Read()
        {
            try
            {
                var path = ResolvePath();
                if (path == null || !File.Exists(path))
                {
                    return ThemePreference.System;
                }

                var raw = File.ReadAllText(path).Trim().ToLowerInvariant();
                return raw switch
                {
                    "dark" => ThemePreference.Dark,
                    "light" => ThemePreference.Light,
                    _ => ThemePreference.System
                };
            }
            catch
            {
                return ThemePreference.System;
            }
        }

        /// <summary>
        /// Persists the preference. Writing <see cref="ThemePreference.System"/>
        /// removes the file so the next launch falls back to the OS preference.
        /// Atomic: writes to a sibling <c>.tmp</c> first then replaces.
        /// Silently succeeds-or-not - never throws on I/O failure so a flaky
        /// disk cannot break the in-session theme toggle.
        /// </summary>
        public void Write(ThemePreference preference)
        {
            try
            {
                var path = ResolvePath();
                if (path == null)
                {
                    return;
                }

                if (preference == ThemePreference.System)
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }
                    return;
                }

                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder))
                {
                    Directory.CreateDirectory(folder);
                }

                var token = preference == ThemePreference.Dark ? "dark" : "light";
                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, token, new UTF8Encoding(false));
                // File.Move with overwrite is atomic on the same volume on Windows.
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                // Persistence is best-effort. Failing to write the preference
                // must not surface to the user; the in-session theme is
                // already applied by the caller.
            }
        }

        private string? ResolvePath()
        {
            try
            {
                var folder = _folderProvider();
                if (string.IsNullOrWhiteSpace(folder))
                {
                    return null;
                }
                return Path.Combine(folder, FileName);
            }
            catch
            {
                return null;
            }
        }
    }
}
