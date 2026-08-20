#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace ControlTower.Infrastructure.Theme
{
    /// <summary>
    /// Persists which portfolio groups are collapsed, per-machine, as one
    /// group label per line under <c>%LOCALAPPDATA%\DeveloperControlTower</c>.
    /// Collapse is a display concern (per display/machine), so it sits next to
    /// theme.txt / accent.txt rather than in synced settings. Absent file or
    /// any I/O failure means "everything expanded" — never throws.
    /// </summary>
    public sealed class GroupCollapseStore
    {
        public const string FileName = "groups-collapsed.txt";
        private const string DefaultFolderName = "DeveloperControlTower";

        private readonly Func<string> _folderProvider;

        public GroupCollapseStore() : this(folderProvider: null) { }

        public GroupCollapseStore(Func<string>? folderProvider)
        {
            _folderProvider = folderProvider ?? DefaultFolder;
        }

        private static string DefaultFolder()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                DefaultFolderName);
        }

        /// <summary>Reads the set of collapsed group labels. Never throws.</summary>
        public HashSet<string> Read()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var path = ResolvePath();
                if (path == null || !File.Exists(path))
                {
                    return set;
                }

                foreach (var line in File.ReadAllLines(path))
                {
                    var label = line.Trim();
                    if (!string.IsNullOrEmpty(label))
                    {
                        set.Add(label);
                    }
                }
            }
            catch
            {
                // Best-effort: a bad file means "all expanded".
            }
            return set;
        }

        /// <summary>Persists the collapsed group labels. Never throws.</summary>
        public void Write(IEnumerable<string> collapsedLabels)
        {
            try
            {
                var path = ResolvePath();
                if (path == null) return;

                var labels = (collapsedLabels ?? Enumerable.Empty<string>())
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Select(l => l.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                if (labels.Count == 0)
                {
                    if (File.Exists(path)) File.Delete(path);
                    return;
                }

                var folder = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, string.Join(Environment.NewLine, labels), new UTF8Encoding(false));
                File.Move(tmp, path, overwrite: true);
            }
            catch
            {
                // Persistence is best-effort.
            }
        }

        private string? ResolvePath()
        {
            try
            {
                var folder = _folderProvider();
                if (string.IsNullOrWhiteSpace(folder)) return null;
                return Path.Combine(folder, FileName);
            }
            catch
            {
                return null;
            }
        }
    }
}
