#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Security;
using System.Text;

namespace ControlTower.Infrastructure.Configuration
{
    public static class LegacyInstallLocator
    {
        private const string ExecutableName = "ControlTower.Desktop.exe";
        private const string SourceSentinelName = "update-repo-root.txt";

        public static bool TryRecordCurrentSourceInstall(
            string markerPath,
            string applicationRoot)
        {
            if (!TryValidateLegacyRoot(applicationRoot, out var validatedRoot))
            {
                return false;
            }

            try
            {
                var markerDirectory = Path.GetDirectoryName(markerPath);
                if (string.IsNullOrWhiteSpace(markerDirectory))
                {
                    return false;
                }

                Directory.CreateDirectory(markerDirectory);
                var temporaryPath = markerPath + ".tmp";
                File.WriteAllText(
                    temporaryPath,
                    validatedRoot + Environment.NewLine,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                File.Move(temporaryPath, markerPath, overwrite: true);
                return true;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException ||
                ex is SecurityException)
            {
                return false;
            }
        }

        public static string Resolve(
            string markerPath,
            string currentApplicationRoot)
        {
            if (string.IsNullOrWhiteSpace(markerPath) ||
                !File.Exists(markerPath))
            {
                return string.Empty;
            }

            try
            {
                var candidate = File.ReadAllLines(markerPath)
                    .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))
                    ?.Trim();
                if (!TryValidateLegacyRoot(candidate, out var validatedRoot))
                {
                    return string.Empty;
                }

                var currentRoot = Path.GetFullPath(currentApplicationRoot)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                return string.Equals(
                    currentRoot,
                    validatedRoot,
                    StringComparison.OrdinalIgnoreCase)
                    ? string.Empty
                    : validatedRoot;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException ||
                ex is SecurityException)
            {
                return string.Empty;
            }
        }

        internal static bool TryValidateLegacyRoot(
            string? path,
            out string validatedRoot)
        {
            validatedRoot = string.Empty;
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            try
            {
                var fullPath = Path.GetFullPath(path)
                    .TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar);
                if (!Directory.Exists(fullPath) ||
                    (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0 ||
                    !File.Exists(Path.Combine(fullPath, ExecutableName)) ||
                    !File.Exists(Path.Combine(fullPath, SourceSentinelName)))
                {
                    return false;
                }

                validatedRoot = fullPath;
                return true;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException ||
                ex is SecurityException)
            {
                return false;
            }
        }
    }
}
