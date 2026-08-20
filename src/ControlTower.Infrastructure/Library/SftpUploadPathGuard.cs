using System;
using System.Collections.Generic;
using System.Linq;
using Renci.SshNet;

namespace ControlTower.Infrastructure.Library
{
    internal enum SftpPathEntryKind
    {
        Directory,
        RegularFile,
        SymbolicLink,
        Other,
    }

    internal sealed class SftpPathEntry
    {
        public SftpPathEntry(string name, SftpPathEntryKind kind)
        {
            Name = name ?? string.Empty;
            Kind = kind;
        }

        public string Name { get; }
        public SftpPathEntryKind Kind { get; }
    }

    internal interface ISftpPathAccessor
    {
        IReadOnlyList<SftpPathEntry> ListDirectory(string path);
        void CreateDirectory(string path);
    }

    internal sealed class SshNetSftpPathAccessor : ISftpPathAccessor
    {
        private readonly SftpClient _client;

        public SshNetSftpPathAccessor(SftpClient client)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
        }

        public IReadOnlyList<SftpPathEntry> ListDirectory(string path)
        {
            return _client.ListDirectory(path)
                .Where(entry => entry.Name != "." && entry.Name != "..")
                .Select(entry => new SftpPathEntry(
                    entry.Name,
                    entry.IsSymbolicLink
                        ? SftpPathEntryKind.SymbolicLink
                        : entry.IsDirectory
                            ? SftpPathEntryKind.Directory
                            : entry.IsRegularFile
                                ? SftpPathEntryKind.RegularFile
                                : SftpPathEntryKind.Other))
                .ToList();
        }

        public void CreateDirectory(string path)
        {
            _client.CreateDirectory(path);
        }
    }

    internal static class SftpUploadPathGuard
    {
        public static bool TryPrepareUpload(
            ISftpPathAccessor accessor,
            string trustedRoot,
            IReadOnlyList<string> descendantSegments,
            bool caseInsensitiveNames,
            out string uploadPath,
            out string issue)
        {
            uploadPath = string.Empty;
            issue = string.Empty;

            if (accessor == null)
            {
                issue = "No SFTP path accessor is available.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(trustedRoot))
            {
                issue = "The trusted SFTP project root is blank.";
                return false;
            }
            if (descendantSegments == null || descendantSegments.Count == 0)
            {
                issue = "The upload target must be beneath the trusted project root.";
                return false;
            }

            foreach (var segment in descendantSegments)
            {
                if (!IsSafeSftpSegment(segment))
                {
                    issue = $"The SFTP path segment '{segment}' is invalid.";
                    return false;
                }
            }

            var comparison = caseInsensitiveNames
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            var normalizedRoot = TrimSftpTrailingSeparators(trustedRoot);
            var current = normalizedRoot;

            for (var i = 0; i < descendantSegments.Count - 1; i++)
            {
                var segment = descendantSegments[i];
                if (!TryFindEntry(
                        accessor,
                        current,
                        segment,
                        comparison,
                        out var entry,
                        out issue))
                {
                    return false;
                }

                var childPath = JoinSftpPath(current, segment);
                if (entry == null)
                {
                    try
                    {
                        accessor.CreateDirectory(childPath);
                    }
                    catch (Exception ex)
                    {
                        issue = $"SFTP directory creation failed for '{childPath}': {ex.Message}";
                        return false;
                    }

                    if (!TryFindEntry(
                            accessor,
                            current,
                            segment,
                            comparison,
                            out entry,
                            out issue))
                    {
                        return false;
                    }
                    if (entry == null)
                    {
                        issue = $"Created SFTP directory '{childPath}' could not be verified.";
                        return false;
                    }
                }

                if (!IsVerifiedDirectory(entry, childPath, out issue))
                {
                    return false;
                }

                current = childPath;
            }

            // Re-enumerate every descendant after creation. READDIR exposes the
            // child entry's link/type metadata without STAT-ing that child path.
            // Servers that omit a usable type produce Other and fail closed.
            return TryValidateExistingChain(
                accessor,
                normalizedRoot,
                descendantSegments,
                comparison,
                out uploadPath,
                out issue);
        }

        private static bool TryValidateExistingChain(
            ISftpPathAccessor accessor,
            string trustedRoot,
            IReadOnlyList<string> descendantSegments,
            StringComparison comparison,
            out string uploadPath,
            out string issue)
        {
            uploadPath = string.Empty;
            issue = string.Empty;
            var current = trustedRoot;

            for (var i = 0; i < descendantSegments.Count; i++)
            {
                var segment = descendantSegments[i];
                var childPath = JoinSftpPath(current, segment);
                if (!TryFindEntry(
                        accessor,
                        current,
                        segment,
                        comparison,
                        out var entry,
                        out issue))
                {
                    return false;
                }

                var isFileTarget = i == descendantSegments.Count - 1;
                if (entry == null)
                {
                    if (!isFileTarget)
                    {
                        issue = $"SFTP parent directory '{childPath}' disappeared during validation.";
                        return false;
                    }

                    uploadPath = childPath;
                    return true;
                }

                if (entry.Kind == SftpPathEntryKind.SymbolicLink)
                {
                    issue = $"SFTP path '{childPath}' is a symbolic link. Upload refused.";
                    return false;
                }

                if (entry.Kind == SftpPathEntryKind.Other)
                {
                    issue =
                        $"SFTP path type for '{childPath}' cannot be proven non-link. Upload refused.";
                    return false;
                }

                if (!isFileTarget)
                {
                    if (entry.Kind != SftpPathEntryKind.Directory)
                    {
                        issue = $"SFTP parent path '{childPath}' is not a directory.";
                        return false;
                    }
                }
                else if (entry.Kind != SftpPathEntryKind.RegularFile)
                {
                    issue = $"SFTP upload target '{childPath}' is not a regular file.";
                    return false;
                }

                current = childPath;
            }

            uploadPath = current;
            return true;
        }

        private static bool TryFindEntry(
            ISftpPathAccessor accessor,
            string parentPath,
            string childName,
            StringComparison comparison,
            out SftpPathEntry entry,
            out string issue)
        {
            entry = null;
            issue = string.Empty;

            IReadOnlyList<SftpPathEntry> entries;
            try
            {
                entries = accessor.ListDirectory(parentPath);
            }
            catch (Exception ex)
            {
                issue =
                    $"SFTP directory '{parentPath}' could not be inspected without following child links: {ex.Message}";
                return false;
            }

            if (entries == null)
            {
                issue = $"SFTP directory '{parentPath}' returned no inspectable entry metadata.";
                return false;
            }

            var matches = entries
                .Where(candidate =>
                    candidate != null &&
                    string.Equals(candidate.Name, childName, comparison))
                .ToList();
            if (matches.Count > 1)
            {
                issue =
                    $"SFTP directory '{parentPath}' contains an ambiguous entry named '{childName}'.";
                return false;
            }

            entry = matches.Count == 1 ? matches[0] : null;
            return true;
        }

        private static bool IsVerifiedDirectory(
            SftpPathEntry entry,
            string path,
            out string issue)
        {
            issue = string.Empty;
            if (entry.Kind == SftpPathEntryKind.SymbolicLink)
            {
                issue = $"SFTP parent path '{path}' is a symbolic link. Upload refused.";
                return false;
            }
            if (entry.Kind != SftpPathEntryKind.Directory)
            {
                issue = entry.Kind == SftpPathEntryKind.Other
                    ? $"SFTP parent path type for '{path}' cannot be proven non-link."
                    : $"SFTP parent path '{path}' is not a directory.";
                return false;
            }

            return true;
        }

        private static bool IsSafeSftpSegment(string segment)
        {
            return !string.IsNullOrEmpty(segment) &&
                   segment != "." &&
                   segment != ".." &&
                   segment.IndexOf('/') < 0 &&
                   segment.IndexOf('\\') < 0 &&
                   segment.IndexOf('\0') < 0 &&
                   segment.IndexOf('\r') < 0 &&
                   segment.IndexOf('\n') < 0;
        }

        private static string TrimSftpTrailingSeparators(string path)
        {
            var normalized = path.Replace('\\', '/');
            while (normalized.Length > 1 &&
                   normalized.EndsWith("/", StringComparison.Ordinal) &&
                   !IsSftpDriveRoot(normalized))
            {
                normalized = normalized.Substring(0, normalized.Length - 1);
            }
            return normalized;
        }

        private static string JoinSftpPath(string parent, string child)
        {
            return parent.EndsWith("/", StringComparison.Ordinal)
                ? parent + child
                : parent + "/" + child;
        }

        private static bool IsSftpDriveRoot(string path)
        {
            return path.Length == 4 &&
                   path[0] == '/' &&
                   char.IsLetter(path[1]) &&
                   path[2] == ':' &&
                   path[3] == '/';
        }
    }
}
