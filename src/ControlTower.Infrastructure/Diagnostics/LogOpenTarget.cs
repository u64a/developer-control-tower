using System;
using System.IO;

namespace ControlTower.Infrastructure.Diagnostics
{
    /// <summary>
    /// Resolves the path the "View log" affordance should open: today's
    /// per-day log file if it has been written, otherwise the log folder
    /// (so the user still gets *somewhere* useful when the app hasn't
    /// logged anything yet this session).
    /// </summary>
    public static class LogOpenTarget
    {
        public static string Resolve()
        {
            return Resolve(File.Exists, Directory.Exists);
        }

        public static string Resolve(Func<string, bool> fileExists, Func<string, bool> directoryExists)
        {
            fileExists ??= _ => false;
            directoryExists ??= _ => false;

            var today = AppLogger.CurrentLogFile;
            if (!string.IsNullOrWhiteSpace(today) && fileExists(today))
            {
                return today;
            }

            var folder = AppLogger.LogFolder;
            if (!string.IsNullOrWhiteSpace(folder) && directoryExists(folder))
            {
                return folder;
            }

            // Fall back to the folder anyway — the caller may create it
            // before opening. Keeps the contract "always returns something".
            return folder;
        }
    }
}
