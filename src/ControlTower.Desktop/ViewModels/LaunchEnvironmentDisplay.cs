using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Launch;

namespace ControlTower.Desktop.ViewModels
{
    /// <summary>What the UI shows for a launch environment: name, product icon (or glyph fallback).</summary>
    public sealed class LaunchEnvironmentDisplay
    {
        public const string EditorGlyph = "\uE8B7";
        public const string TerminalGlyph = "\uE756";

        public LaunchEnvironmentDisplay(LaunchEnvironment environment, ImageSource icon, bool isProjectDefault)
        {
            Environment = environment;
            Id = environment.Id;
            Name = environment.DisplayName;
            Icon = icon;
            Glyph = environment.Kind == LaunchEnvironmentKind.Terminal ? TerminalGlyph : EditorGlyph;
            ToolTip = "Opens in " + environment.DisplayName + (isProjectDefault ? " (default)" : string.Empty);
        }

        public LaunchEnvironment Environment { get; }

        public string Id { get; }

        public string Name { get; }

        public ImageSource Icon { get; }

        public bool HasIcon => Icon != null;

        public string Glyph { get; }

        public string ToolTip { get; }

        public bool IsTerminal => Environment.Kind == LaunchEnvironmentKind.Terminal;
    }

    /// <summary>
    /// Extracts product icons at runtime from the user's installed
    /// executables (never bundled). Results, including misses, are cached
    /// per environment for the app session.
    /// </summary>
    public static class LaunchEnvironmentIconCache
    {
        private static readonly ConcurrentDictionary<string, ImageSource> Cache = new ConcurrentDictionary<string, ImageSource>(StringComparer.Ordinal);

        public static ImageSource Get(LaunchEnvironment environment)
        {
            if (environment == null)
            {
                return null;
            }

            var key = environment.Id + "|" + environment.Command + "|" + environment.IconPath;
            return Cache.GetOrAdd(key, _ => Load(environment));
        }

        private static ImageSource Load(LaunchEnvironment environment)
        {
            try
            {
                foreach (var candidate in LaunchEnvironmentIconLocator.GetCandidates(environment))
                {
                    var image = TryLoad(candidate);
                    if (image != null)
                    {
                        return image;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Launch", "Could not resolve an icon for launch environment '" + environment.Id + "': " + ex.Message);
            }

            return null;
        }

        private static ImageSource TryLoad(string path)
        {
            try
            {
                var extension = Path.GetExtension(path);
                if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(extension, ".ico", StringComparison.OrdinalIgnoreCase))
                {
                    var bitmap = new BitmapImage();
                    bitmap.BeginInit();
                    bitmap.CacheOption = BitmapCacheOption.OnLoad;
                    bitmap.UriSource = new Uri(path, UriKind.Absolute);
                    bitmap.DecodePixelWidth = 32;
                    bitmap.EndInit();
                    bitmap.Freeze();
                    return bitmap;
                }

                return ExtractIcon(path);
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource ExtractIcon(string path)
        {
            // App execution aliases (e.g. WindowsApps\wt.exe) are zero-byte
            // reparse points with no icon resources; count == 0 skips them.
            var count = ExtractIconEx(path, -1, null, null, 0);
            if (count == 0)
            {
                return null;
            }

            var large = new IntPtr[1];
            if (ExtractIconEx(path, 0, large, null, 1) == 0 || large[0] == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var source = Imaging.CreateBitmapSourceFromHIcon(large[0], Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DestroyIcon(large[0]);
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(string file, int iconIndex, IntPtr[] largeIcons, IntPtr[] smallIcons, uint icons);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyIcon(IntPtr handle);
    }
}
