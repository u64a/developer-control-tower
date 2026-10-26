#nullable enable
using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using ControlTower.Core.Appearance;

namespace ControlTower.Infrastructure.Theme
{
    /// <summary>
    /// Which theme fills each mode slot. Light/dark mode itself is still
    /// chosen by <see cref="ThemePreferenceStore"/>; this only says what
    /// "dark" and "light" look like.
    /// </summary>
    public sealed record ThemeSelection(string DarkThemeId, string LightThemeId, bool KeepGitStatesDistinct)
    {
        public static ThemeSelection Default { get; } =
            new(ThemeCatalog.DefaultDarkId, ThemeCatalog.DefaultLightId, KeepGitStatesDistinct: true);

        public string IdFor(ThemeVariant mode) => mode == ThemeVariant.Dark ? DarkThemeId : LightThemeId;
    }

    /// <summary>
    /// Persists the <see cref="ThemeSelection"/> as <c>key=value</c> lines in
    /// <c>themes.txt</c> under <c>%LOCALAPPDATA%\DeveloperControlTower</c>.
    /// Per-machine, like <c>theme.txt</c>. Unknown keys are ignored; theme IDs
    /// that are malformed, unknown, or meant for the other mode fall back to
    /// the built-in theme. Never throws.
    /// </summary>
    public sealed class ThemeSelectionStore
    {
        public const string FileName = "themes.txt";
        private const string DefaultFolderName = "DeveloperControlTower";
        private const int MaxFileBytes = 4096;

        private static readonly Regex IdPattern = new("^[a-z0-9-]{1,40}$", RegexOptions.CultureInvariant);

        private readonly Func<string> _folderProvider;

        public ThemeSelectionStore()
            : this(folderProvider: null)
        {
        }

        // Test seam: lets tests redirect the folder away from %LOCALAPPDATA%.
        public ThemeSelectionStore(Func<string>? folderProvider)
        {
            _folderProvider = folderProvider ?? DefaultFolder;
        }

        private static string DefaultFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DefaultFolderName);
        }

        public ThemeSelection Read()
        {
            var selection = ThemeSelection.Default;
            try
            {
                var path = ResolvePath();
                if (path == null || !File.Exists(path) || new FileInfo(path).Length > MaxFileBytes)
                {
                    return selection;
                }

                foreach (var line in File.ReadAllLines(path))
                {
                    var separator = line.IndexOf('=');
                    if (separator <= 0)
                    {
                        continue;
                    }
                    var key = line[..separator].Trim().ToLowerInvariant();
                    var value = line[(separator + 1)..].Trim().ToLowerInvariant();
                    switch (key)
                    {
                        case "dark":
                            selection = selection with { DarkThemeId = Validate(value, ThemeVariant.Dark) };
                            break;
                        case "light":
                            selection = selection with { LightThemeId = Validate(value, ThemeVariant.Light) };
                            break;
                        case "distinct-git-states":
                            if (bool.TryParse(value, out var distinct))
                            {
                                selection = selection with { KeepGitStatesDistinct = distinct };
                            }
                            break;
                    }
                }
                return selection;
            }
            catch
            {
                return ThemeSelection.Default;
            }
        }

        /// <summary>
        /// Persists the selection; writing the default removes the file.
        /// Atomic write; best-effort.
        /// </summary>
        public void Write(ThemeSelection selection)
        {
            try
            {
                var path = ResolvePath();
                if (path == null || selection == null)
                {
                    return;
                }

                var normalised = new ThemeSelection(
                    Validate(selection.DarkThemeId, ThemeVariant.Dark),
                    Validate(selection.LightThemeId, ThemeVariant.Light),
                    selection.KeepGitStatesDistinct);

                if (normalised == ThemeSelection.Default)
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

                var content = "dark=" + normalised.DarkThemeId + "\n"
                    + "light=" + normalised.LightThemeId + "\n"
                    + "distinct-git-states=" + (normalised.KeepGitStatesDistinct ? "true" : "false") + "\n";

                var tempPath = path + ".tmp";
                File.WriteAllText(tempPath, content, new UTF8Encoding(false));
                File.Move(tempPath, path, overwrite: true);
            }
            catch
            {
                // Best-effort; the in-session theme is already applied.
            }
        }

        private static string Validate(string? id, ThemeVariant mode)
        {
            if (id != null && IdPattern.IsMatch(id))
            {
                return ThemeCatalog.GetForMode(id, mode).Id;
            }
            return ThemeCatalog.BuiltInFor(mode).Id;
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
