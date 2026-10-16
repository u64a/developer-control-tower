using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ControlTower.Infrastructure.Configuration
{
    /// <summary>
    /// Resolves a fully-qualified Windows path through the nearest existing
    /// directory handle so SUBST, short-name, and mapped-drive aliases cannot
    /// be mistaken for their lexical spelling.
    /// </summary>
    internal static class WindowsPhysicalPathResolver
    {
        private const uint FileReadAttributes = 0x80;
        private const uint FileShareRead = 0x1;
        private const uint FileShareWrite = 0x2;
        private const uint FileShareDelete = 0x4;
        private const uint OpenExisting = 3;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const uint DriveRemovable = 2;
        private const uint DriveFixed = 3;
        private const uint DriveCdRom = 5;
        private const uint DriveRamDisk = 6;

        public static bool TryResolveNearestExistingLocalPath(
            string fullPath,
            out string physicalPath)
        {
            physicalPath = string.Empty;
            if (!OperatingSystem.IsWindows() ||
                string.IsNullOrWhiteSpace(fullPath) ||
                !Path.IsPathFullyQualified(fullPath) ||
                fullPath.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            try
            {
                var normalized = Path.GetFullPath(fullPath);
                if (AllowedSettingsRoots.HasExistingReparsePoint(normalized) ||
                    !TryFindExistingDirectory(normalized, out var existingDirectory, out var missingSegments) ||
                    !TryGetFinalLocalPath(existingDirectory, out var resolvedExisting))
                {
                    return false;
                }

                var rebuilt = resolvedExisting;
                foreach (var segment in missingSegments)
                {
                    if (string.IsNullOrWhiteSpace(segment) ||
                        segment == "." ||
                        segment == ".." ||
                        segment.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                        segment.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                    {
                        return false;
                    }

                    rebuilt = Path.GetFullPath(Path.Combine(rebuilt, segment));
                }

                if (AllowedSettingsRoots.HasExistingReparsePoint(rebuilt))
                {
                    return false;
                }

                physicalPath = rebuilt;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryFindExistingDirectory(
            string path,
            out string existingDirectory,
            out IReadOnlyList<string> missingSegments)
        {
            existingDirectory = string.Empty;
            missingSegments = Array.Empty<string>();
            var missing = new List<string>();
            var current = path;

            while (true)
            {
                if (!AllowedSettingsRoots.TryGetProvableAttributes(current, out var attributes, out var definitelyMissing))
                {
                    if (!definitelyMissing)
                    {
                        // Access-denied or another unprovable I/O condition:
                        // this ancestor's existence/identity cannot be
                        // confirmed, so it must never be walked past and
                        // reconstructed as if it were absent.
                        return false;
                    }
                }
                else if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // A reparse point — including a dangling one whose target
                    // no longer resolves — cannot prove physical identity.
                    return false;
                }
                else if ((attributes & FileAttributes.Directory) != 0)
                {
                    break;
                }
                else
                {
                    // Exists, but as a plain file where a directory is required.
                    return false;
                }

                var parent = Path.GetDirectoryName(current);
                var leaf = Path.GetFileName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.IsNullOrWhiteSpace(leaf) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                missing.Add(leaf);
                current = parent;
            }

            missing.Reverse();
            existingDirectory = current;
            missingSegments = missing;
            return true;
        }

        private static bool TryGetFinalLocalPath(string existingDirectory, out string physicalPath)
        {
            physicalPath = string.Empty;
            using var handle = CreateFile(
                existingDirectory,
                FileReadAttributes,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                FileFlagBackupSemantics,
                IntPtr.Zero);
            if (handle.IsInvalid)
            {
                return false;
            }

            var capacity = 512u;
            while (capacity <= 32768)
            {
                var buffer = new StringBuilder((int)capacity);
                var length = GetFinalPathNameByHandle(handle, buffer, capacity, 0);
                if (length == 0)
                {
                    return false;
                }

                if (length < capacity)
                {
                    return TryNormalizeLocalFinalPath(buffer.ToString(), out physicalPath);
                }

                capacity = length + 1;
            }

            return false;
        }

        private static bool TryNormalizeLocalFinalPath(string finalPath, out string physicalPath)
        {
            physicalPath = string.Empty;
            const string ExtendedPrefix = @"\\?\";
            const string ExtendedUncPrefix = @"\\?\UNC\";
            if (string.IsNullOrWhiteSpace(finalPath) ||
                finalPath.StartsWith(ExtendedUncPrefix, StringComparison.OrdinalIgnoreCase) ||
                !finalPath.StartsWith(ExtendedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var candidate = finalPath.Substring(ExtendedPrefix.Length);
            if (!Path.IsPathFullyQualified(candidate) ||
                candidate.StartsWith(@"\\", StringComparison.Ordinal))
            {
                return false;
            }

            var root = Path.GetPathRoot(candidate);
            var driveType = string.IsNullOrWhiteSpace(root) ? 0 : GetDriveType(root);
            if (driveType != DriveRemovable &&
                driveType != DriveFixed &&
                driveType != DriveCdRom &&
                driveType != DriveRamDisk)
            {
                return false;
            }

            physicalPath = Path.GetFullPath(candidate);
            return true;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(
            string fileName,
            uint desiredAccess,
            uint shareMode,
            IntPtr securityAttributes,
            uint creationDisposition,
            uint flagsAndAttributes,
            IntPtr templateFile);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFinalPathNameByHandle(
            SafeFileHandle file,
            StringBuilder path,
            uint pathLength,
            uint flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetDriveType(string rootPath);
    }
}
