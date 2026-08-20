using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace ControlTower.Infrastructure.Library
{
    internal static class LibraryPathContainment
    {
        private static readonly StringComparison LocalPathComparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        public static bool TryResolveLocalDescendant(
            string root,
            string relativePath,
            bool inspectRootForReparsePoint,
            out string resolvedPath,
            out string issue)
        {
            resolvedPath = string.Empty;
            issue = string.Empty;

            if (string.IsNullOrWhiteSpace(root))
            {
                issue = "The containing root is blank.";
                return false;
            }

            if (string.IsNullOrWhiteSpace(relativePath))
            {
                issue = "The relative path is blank.";
                return false;
            }

            if (IsRootedOnAnySupportedPlatform(relativePath))
            {
                issue = "The path must be relative to the containing root.";
                return false;
            }

            try
            {
                var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var normalizedRelative = relativePath
                    .Replace('\\', Path.DirectorySeparatorChar)
                    .Replace('/', Path.DirectorySeparatorChar);
                var canonicalCandidate = Path.GetFullPath(
                    Path.Combine(canonicalRoot, normalizedRelative));

                if (!IsStrictLocalDescendant(canonicalCandidate, canonicalRoot))
                {
                    issue = "The path resolves outside the containing root.";
                    return false;
                }

                if (!TryInspectReparsePoints(
                        canonicalRoot,
                        canonicalCandidate,
                        inspectRootForReparsePoint,
                        out issue))
                {
                    return false;
                }

                resolvedPath = canonicalCandidate;
                return true;
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is IOException ||
                ex is NotSupportedException ||
                ex is UnauthorizedAccessException)
            {
                issue = "The path could not be safely resolved: " + ex.Message;
                return false;
            }
        }

        public static bool TryValidateLocalDescendant(
            string root,
            string candidatePath,
            bool inspectRootForReparsePoint,
            out string resolvedPath,
            out string issue)
        {
            resolvedPath = string.Empty;
            issue = string.Empty;

            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(candidatePath))
            {
                issue = "The containing root and candidate path are required.";
                return false;
            }

            try
            {
                var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
                var canonicalCandidate = Path.GetFullPath(candidatePath);

                if (!IsStrictLocalDescendant(canonicalCandidate, canonicalRoot))
                {
                    issue = "The path resolves outside the containing root.";
                    return false;
                }

                if (!TryInspectReparsePoints(
                        canonicalRoot,
                        canonicalCandidate,
                        inspectRootForReparsePoint,
                        out issue))
                {
                    return false;
                }

                resolvedPath = canonicalCandidate;
                return true;
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is IOException ||
                ex is NotSupportedException ||
                ex is UnauthorizedAccessException)
            {
                issue = "The path could not be safely resolved: " + ex.Message;
                return false;
            }
        }

        public static bool LocalPathsEqual(string first, string second)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(first),
                    Path.GetFullPath(second),
                    LocalPathComparison);
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is IOException ||
                ex is NotSupportedException)
            {
                return false;
            }
        }

        public static bool TryResolveRemoteTarget(
            string projectRoot,
            string relativeTarget,
            bool remoteIsWindows,
            out string resolvedTarget,
            out string issue)
        {
            resolvedTarget = string.Empty;
            issue = string.Empty;

            if (!TryParseRemotePath(projectRoot, remoteIsWindows, out var root, out issue))
            {
                issue = "The project root is invalid: " + issue;
                return false;
            }

            if (!TryNormalizeRemoteRelativePath(
                    relativeTarget,
                    remoteIsWindows,
                    out var targetSegments,
                    out issue))
            {
                return false;
            }

            var combined = new RemotePathParts(
                root.Kind,
                root.Anchor,
                root.Segments.Concat(targetSegments).ToArray());

            if (!IsRemotePathContained(combined, root, remoteIsWindows, allowEqual: true))
            {
                issue = "The resolved target falls outside the project root.";
                return false;
            }

            resolvedTarget = RenderRemotePath(combined, remoteIsWindows);
            return true;
        }

        public static bool TryValidateRemotePathWithinRoot(
            string rootPath,
            string candidatePath,
            bool remoteIsWindows,
            bool allowEqual,
            out string normalizedCandidate,
            out string issue)
        {
            return TryGetRemoteDescendantSegments(
                rootPath,
                candidatePath,
                remoteIsWindows,
                allowEqual,
                out _,
                out normalizedCandidate,
                out _,
                out issue);
        }

        public static bool TryGetRemoteDescendantSegments(
            string rootPath,
            string candidatePath,
            bool remoteIsWindows,
            bool allowEqual,
            out string normalizedRoot,
            out string normalizedCandidate,
            out IReadOnlyList<string> descendantSegments,
            out string issue)
        {
            normalizedRoot = string.Empty;
            normalizedCandidate = string.Empty;
            descendantSegments = Array.Empty<string>();
            issue = string.Empty;

            if (!TryParseRemotePath(rootPath, remoteIsWindows, out var root, out issue))
            {
                issue = "The containing remote root is invalid: " + issue;
                return false;
            }

            if (!TryParseRemotePath(candidatePath, remoteIsWindows, out var candidate, out issue))
            {
                issue = "The remote candidate path is invalid: " + issue;
                return false;
            }

            if (!IsRemotePathContained(candidate, root, remoteIsWindows, allowEqual))
            {
                issue = "The remote path resolves outside the containing root.";
                return false;
            }

            normalizedRoot = RenderRemotePath(root, remoteIsWindows);
            normalizedCandidate = RenderRemotePath(candidate, remoteIsWindows);
            descendantSegments = candidate.Segments
                .Skip(root.Segments.Count)
                .ToArray();
            return true;
        }

        public static bool RemotePathsEqual(string first, string second, bool remoteIsWindows)
        {
            if (!TryParseRemotePath(first, remoteIsWindows, out var firstPath, out _) ||
                !TryParseRemotePath(second, remoteIsWindows, out var secondPath, out _))
            {
                return false;
            }

            return IsSameRemotePath(firstPath, secondPath, remoteIsWindows);
        }

        private static bool IsRootedOnAnySupportedPlatform(string path)
        {
            return Path.IsPathRooted(path) ||
                   path.StartsWith("/", StringComparison.Ordinal) ||
                   path.StartsWith("\\", StringComparison.Ordinal) ||
                   HasDrivePrefix(path);
        }

        private static bool HasDrivePrefix(string path)
        {
            return path.Length >= 2 &&
                   char.IsLetter(path[0]) &&
                   path[1] == ':';
        }

        private static bool IsStrictLocalDescendant(string candidate, string root)
        {
            if (string.Equals(candidate, root, LocalPathComparison))
            {
                return false;
            }

            var rootWithSeparator = Path.EndsInDirectorySeparator(root)
                ? root
                : root + Path.DirectorySeparatorChar;
            return candidate.StartsWith(rootWithSeparator, LocalPathComparison);
        }

        private static bool TryInspectReparsePoints(
            string root,
            string candidate,
            bool inspectRoot,
            out string issue)
        {
            issue = string.Empty;
            var current = root;
            var rootIsReparsePoint = false;

            if (inspectRoot && !TryInspectExistingPath(current, out rootIsReparsePoint, out issue))
            {
                return false;
            }
            if (inspectRoot && rootIsReparsePoint)
            {
                issue = $"The containing root is a reparse point: '{root}'.";
                return false;
            }

            var relative = Path.GetRelativePath(root, candidate);
            var segments = relative.Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                current = Path.Combine(current, segment);
                if (!TryInspectExistingPath(current, out var isReparsePoint, out issue))
                {
                    return false;
                }
                if (isReparsePoint)
                {
                    issue = $"The path traverses a reparse point: '{current}'.";
                    return false;
                }
            }

            return true;
        }

        private static bool TryInspectExistingPath(
            string path,
            out bool isReparsePoint,
            out string issue)
        {
            isReparsePoint = false;
            issue = string.Empty;

            try
            {
                var attributes = File.GetAttributes(path);
                isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;
                return true;
            }
            catch (FileNotFoundException)
            {
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                return true;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is NotSupportedException ||
                ex is ArgumentException)
            {
                issue = $"The path attributes could not be inspected for '{path}': {ex.Message}";
                return false;
            }
        }

        private static bool TryNormalizeRemoteRelativePath(
            string relativePath,
            bool remoteIsWindows,
            out IReadOnlyList<string> segments,
            out string issue)
        {
            segments = Array.Empty<string>();
            issue = string.Empty;
            relativePath ??= string.Empty;

            if (ContainsInvalidRemoteCharacters(relativePath))
            {
                issue = "The target contains an invalid control character.";
                return false;
            }

            if (relativePath.StartsWith("/", StringComparison.Ordinal) ||
                relativePath.StartsWith("\\", StringComparison.Ordinal))
            {
                issue = "The target must be relative; rooted and UNC paths are not allowed.";
                return false;
            }

            if (HasDrivePrefix(relativePath))
            {
                issue = "The target must be relative; drive-qualified paths are not allowed.";
                return false;
            }

            var normalized = new List<string>();
            foreach (var segment in relativePath.Split(
                         new[] { '/', '\\' },
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (normalized.Count == 0)
                    {
                        issue = "The target escapes the project root through '..'.";
                        return false;
                    }

                    normalized.RemoveAt(normalized.Count - 1);
                    continue;
                }

                if (remoteIsWindows &&
                    !TryValidateWindowsPathSegment(segment, out issue))
                {
                    return false;
                }

                normalized.Add(segment);
            }

            segments = normalized;
            return true;
        }

        private static bool TryParseRemotePath(
            string path,
            bool remoteIsWindows,
            out RemotePathParts parsed,
            out string issue)
        {
            parsed = default;
            issue = string.Empty;

            if (string.IsNullOrWhiteSpace(path))
            {
                issue = "The path is blank.";
                return false;
            }

            if (ContainsInvalidRemoteCharacters(path))
            {
                issue = "The path contains an invalid control character.";
                return false;
            }

            return remoteIsWindows
                ? TryParseWindowsRemotePath(path, out parsed, out issue)
                : TryParsePosixRemotePath(path, out parsed, out issue);
        }

        private static bool TryParsePosixRemotePath(
            string path,
            out RemotePathParts parsed,
            out string issue)
        {
            var normalized = path.Replace('\\', '/');
            var absolute = normalized.StartsWith("/", StringComparison.Ordinal);
            if (!TryNormalizeFullPathSegments(
                    normalized.Split('/', StringSplitOptions.RemoveEmptyEntries),
                    absolute,
                    validateWindowsSegments: false,
                    out var segments,
                    out issue))
            {
                parsed = default;
                return false;
            }

            parsed = new RemotePathParts(
                absolute ? RemoteRootKind.PosixAbsolute : RemoteRootKind.Relative,
                absolute ? "/" : string.Empty,
                segments);
            return true;
        }

        private static bool TryParseWindowsRemotePath(
            string path,
            out RemotePathParts parsed,
            out string issue)
        {
            parsed = default;
            issue = string.Empty;
            var normalized = path.Replace('/', '\\');

            if (HasWindowsDeviceOrExtendedPrefix(normalized))
            {
                issue = "Windows device and extended path prefixes are not allowed.";
                return false;
            }

            if (normalized.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var uncParts = normalized
                    .TrimStart('\\')
                    .Split('\\', StringSplitOptions.RemoveEmptyEntries);
                if (uncParts.Length < 2 ||
                    uncParts[0] is "." or ".." ||
                    uncParts[1] is "." or ".." ||
                    !TryValidateWindowsPathSegment(uncParts[0], out issue) ||
                    !TryValidateWindowsPathSegment(uncParts[1], out issue))
                {
                    if (string.IsNullOrEmpty(issue))
                    {
                        issue = "A UNC path must include a valid server and share.";
                    }
                    return false;
                }

                if (!TryNormalizeFullPathSegments(
                        uncParts.Skip(2),
                        absolute: true,
                        validateWindowsSegments: true,
                        out var uncSegments,
                        out issue))
                {
                    return false;
                }

                parsed = new RemotePathParts(
                    RemoteRootKind.WindowsUnc,
                    @"\\" + uncParts[0] + @"\" + uncParts[1],
                    uncSegments);
                return true;
            }

            if (HasDrivePrefix(normalized))
            {
                var absolute = normalized.Length >= 3 && normalized[2] == '\\';
                if (!TryNormalizeFullPathSegments(
                        normalized.Substring(absolute ? 3 : 2)
                            .Split('\\', StringSplitOptions.RemoveEmptyEntries),
                        absolute,
                        validateWindowsSegments: true,
                        out var driveSegments,
                        out issue))
                {
                    return false;
                }

                parsed = new RemotePathParts(
                    absolute
                        ? RemoteRootKind.WindowsDriveAbsolute
                        : RemoteRootKind.WindowsDriveRelative,
                    char.ToUpperInvariant(normalized[0]) + ":",
                    driveSegments);
                return true;
            }

            var rooted = normalized.StartsWith("\\", StringComparison.Ordinal);
            if (!TryNormalizeFullPathSegments(
                    normalized.Split('\\', StringSplitOptions.RemoveEmptyEntries),
                    rooted,
                    validateWindowsSegments: true,
                    out var rootedSegments,
                    out issue))
            {
                return false;
            }

            parsed = new RemotePathParts(
                rooted ? RemoteRootKind.WindowsRooted : RemoteRootKind.Relative,
                rooted ? @"\" : string.Empty,
                rootedSegments);
            return true;
        }

        private static bool TryNormalizeFullPathSegments(
            IEnumerable<string> source,
            bool absolute,
            bool validateWindowsSegments,
            out IReadOnlyList<string> segments,
            out string issue)
        {
            var normalized = new List<string>();
            issue = string.Empty;

            foreach (var segment in source)
            {
                if (segment == ".")
                {
                    continue;
                }

                if (segment == "..")
                {
                    if (normalized.Count > 0 && normalized[^1] != "..")
                    {
                        normalized.RemoveAt(normalized.Count - 1);
                    }
                    else if (!absolute)
                    {
                        normalized.Add(segment);
                    }
                    continue;
                }

                if (validateWindowsSegments &&
                    !TryValidateWindowsPathSegment(segment, out issue))
                {
                    segments = Array.Empty<string>();
                    return false;
                }

                normalized.Add(segment);
            }

            segments = normalized;
            return true;
        }

        private static bool TryValidateWindowsPathSegment(
            string segment,
            out string issue)
        {
            issue = string.Empty;

            if (string.IsNullOrEmpty(segment))
            {
                issue = "Windows path segments must not be empty.";
                return false;
            }

            if (segment.EndsWith(" ", StringComparison.Ordinal) ||
                segment.EndsWith(".", StringComparison.Ordinal))
            {
                issue = "Windows path segments must not end with a dot or space.";
                return false;
            }

            if (segment.IndexOf(':') >= 0)
            {
                issue = "Windows alternate-data-stream path segments are not allowed.";
                return false;
            }

            if (segment.IndexOfAny(new[] { '<', '>', '"', '|', '?', '*' }) >= 0 ||
                segment.Any(char.IsControl))
            {
                issue = "The Windows path contains characters that Win32 cannot address safely.";
                return false;
            }

            var deviceBaseName = segment.Split('.')[0];
            if (IsReservedWindowsDeviceName(deviceBaseName))
            {
                issue = $"Windows device name '{deviceBaseName}' is not allowed in a remote path.";
                return false;
            }

            return true;
        }

        private static bool HasWindowsDeviceOrExtendedPrefix(string path)
        {
            return path.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"\\.\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"\??\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"\\??\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"\Device\", StringComparison.OrdinalIgnoreCase) ||
                   path.StartsWith(@"\Global??\", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsReservedWindowsDeviceName(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            if (value.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("CONIN$", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("CONOUT$", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("CLOCK$", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return value.Length == 4 &&
                   (value.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                    value.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                   value[3] is >= '1' and <= '9';
        }

        private static bool IsRemotePathContained(
            RemotePathParts candidate,
            RemotePathParts root,
            bool remoteIsWindows,
            bool allowEqual)
        {
            var comparison = remoteIsWindows
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

            if (candidate.Kind != root.Kind ||
                !string.Equals(candidate.Anchor, root.Anchor, comparison) ||
                candidate.Segments.Count < root.Segments.Count)
            {
                return false;
            }

            if (!allowEqual && candidate.Segments.Count == root.Segments.Count)
            {
                return false;
            }

            for (var i = 0; i < root.Segments.Count; i++)
            {
                if (!string.Equals(candidate.Segments[i], root.Segments[i], comparison))
                {
                    return false;
                }
            }

            if (root.Kind == RemoteRootKind.Relative)
            {
                for (var i = root.Segments.Count; i < candidate.Segments.Count; i++)
                {
                    if (candidate.Segments[i] == "..")
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsSameRemotePath(
            RemotePathParts first,
            RemotePathParts second,
            bool remoteIsWindows)
        {
            return first.Segments.Count == second.Segments.Count &&
                   IsRemotePathContained(first, second, remoteIsWindows, allowEqual: true) &&
                   IsRemotePathContained(second, first, remoteIsWindows, allowEqual: true);
        }

        private static string RenderRemotePath(
            RemotePathParts path,
            bool remoteIsWindows)
        {
            var separator = remoteIsWindows ? "\\" : "/";
            var body = string.Join(separator, path.Segments);

            return path.Kind switch
            {
                RemoteRootKind.PosixAbsolute =>
                    body.Length == 0 ? "/" : "/" + body,
                RemoteRootKind.WindowsRooted =>
                    body.Length == 0 ? @"\" : @"\" + body,
                RemoteRootKind.WindowsDriveAbsolute =>
                    body.Length == 0 ? path.Anchor + @"\" : path.Anchor + @"\" + body,
                RemoteRootKind.WindowsDriveRelative =>
                    body.Length == 0 ? path.Anchor : path.Anchor + body,
                RemoteRootKind.WindowsUnc =>
                    body.Length == 0 ? path.Anchor : path.Anchor + @"\" + body,
                _ => body.Length == 0 ? "." : body,
            };
        }

        private static bool ContainsInvalidRemoteCharacters(string path)
        {
            return path.IndexOf('\0') >= 0 ||
                   path.IndexOf('\r') >= 0 ||
                   path.IndexOf('\n') >= 0;
        }

        private enum RemoteRootKind
        {
            Relative,
            PosixAbsolute,
            WindowsRooted,
            WindowsDriveAbsolute,
            WindowsDriveRelative,
            WindowsUnc,
        }

        private readonly struct RemotePathParts
        {
            public RemotePathParts(
                RemoteRootKind kind,
                string anchor,
                IReadOnlyList<string> segments)
            {
                Kind = kind;
                Anchor = anchor;
                Segments = segments;
            }

            public RemoteRootKind Kind { get; }
            public string Anchor { get; }
            public IReadOnlyList<string> Segments { get; }
        }
    }
}
