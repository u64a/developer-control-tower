#nullable enable
using System;
using System.IO;
using System.Text;

namespace ControlTower.Infrastructure.Theme
{
    /// <summary>
    /// User's persisted accent choice. <see cref="TowerCyan"/> is the app's
    /// brand accent (the shipped default). <see cref="WindowsAccent"/> follows
    /// the OS accent colour. Absent file means <see cref="TowerCyan"/>.
    /// </summary>
    public enum AccentPreference
    {
        TowerCyan = 0,
        WindowsAccent = 1
    }

    /// <summary>
    /// Reads and writes the user's persisted accent preference as a single
    /// lower-case token (<c>cyan</c> or <c>windows</c>) in a tiny text file
    /// under <c>%LOCALAPPDATA%\DeveloperControlTower</c>.
    ///
    /// Per-machine (not synced) because accent is a per-display concern, and
    /// it mirrors how <c>theme.txt</c> is handled. Never throws — a flaky disk
    /// must not break the accent picker; an unreadable/absent file falls back
    /// to the brand accent (<see cref="AccentPreference.TowerCyan"/>).
    /// </summary>
    public sealed class AccentPreferenceStore
    {
        public const string FileName = "accent.txt";
        private const string DefaultFolderName = "DeveloperControlTower";

        private readonly Func<string> _folderProvider;

        public AccentPreferenceStore()
            : this(folderProvider: null)
        {
        }

        // Test seam: lets tests redirect the folder away from %LOCALAPPDATA%.
        public AccentPreferenceStore(Func<string>? folderProvider)
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
        /// <see cref="AccentPreference.TowerCyan"/> when the file is missing,
        /// empty, unparseable, or unreadable — never throws.
        /// </summary>
        public AccentPreference Read()
        {
            try
            {
                var path = ResolvePath();
                if (path == null || !File.Exists(path))
                {
                    return AccentPreference.TowerCyan;
                }

                var raw = File.ReadAllText(path).Trim().ToLowerInvariant();
                return raw switch
                {
                    "windows" => AccentPreference.WindowsAccent,
                    "cyan" => AccentPreference.TowerCyan,
                    _ => AccentPreference.TowerCyan
                };
            }
            catch
            {
                return AccentPreference.TowerCyan;
            }
        }

        /// <summary>
        /// Persists the preference. Writing <see cref="AccentPreference.TowerCyan"/>
        /// removes the file so the brand default applies. Atomic write; never
        /// throws on I/O failure (persistence is best-effort).
        /// </summary>
        public void Write(AccentPreference preference)
        {
            try
            {
                var path = ResolvePath();
                if (path == null)
                {
                    return;
                }

                if (preference == AccentPreference.TowerCyan)
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

                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, "windows", new UTF8Encoding(false));
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                // Best-effort; the in-session accent is already applied.
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
