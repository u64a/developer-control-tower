using System;
using System.IO;
using System.Linq;
using System.Text;

namespace ControlTower.Infrastructure.Registration
{
    /// <summary>
    /// Ensures a project's .gitignore excludes the local .controltower folder
    /// so app-managed metadata doesn't accidentally get committed.
    /// </summary>
    public static class GitIgnoreManager
    {
        private const string Marker = "# Developer Control Tower";
        private const string Pattern = ".controltower/";

        public static void EnsureControlTowerIgnored(string projectRootPath)
        {
            if (string.IsNullOrWhiteSpace(projectRootPath) || !Directory.Exists(projectRootPath))
            {
                return;
            }

            // Only act on git repos — if there's no .git here we'd be writing
            // a stray .gitignore the user didn't ask for.
            if (!Directory.Exists(Path.Combine(projectRootPath, ".git")))
            {
                return;
            }

            var path = Path.Combine(projectRootPath, ".gitignore");

            try
            {
                if (!File.Exists(path))
                {
                    var sb = new StringBuilder();
                    sb.AppendLine(Marker);
                    sb.AppendLine(Pattern);
                    File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
                    return;
                }

                var existingLines = File.ReadAllLines(path);
                if (existingLines.Any(l => MatchesPattern(l)))
                {
                    return; // Already ignored.
                }

                using var sw = new StreamWriter(path, append: true, new UTF8Encoding(false));
                if (existingLines.Length > 0 && !string.IsNullOrEmpty(existingLines[existingLines.Length - 1]))
                {
                    sw.WriteLine();
                }
                sw.WriteLine(Marker);
                sw.WriteLine(Pattern);
            }
            catch
            {
                // Non-fatal — gitignore management shouldn't break project setup.
            }
        }

        private static bool MatchesPattern(string line)
        {
            var trimmed = (line ?? string.Empty).Trim();
            if (trimmed.StartsWith("#")) return false;
            // Accept the various ways someone might already have ignored it.
            return string.Equals(trimmed, ".controltower/", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, ".controltower", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "/.controltower/", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(trimmed, "/.controltower", StringComparison.OrdinalIgnoreCase);
        }
    }
}
