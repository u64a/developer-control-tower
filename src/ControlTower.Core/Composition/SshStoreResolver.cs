using System;
using System.Collections.Generic;
using ControlTower.Core.Models;

namespace ControlTower.Core.Composition
{
    /// <summary>
    /// Pure function that resolves a store ID and folder name from an SSH target
    /// string by matching against configured SSH stores. No IO — fully testable.
    /// </summary>
    public static class SshStoreResolver
    {
        /// <summary>
        /// Attempts to match an SSH target string (e.g.
        /// <c>"devuser@192.168.64.10:d:\repos\myproject"</c>) against the
        /// configured SSH stores. Returns <c>true</c> plus the matched
        /// <paramref name="storeId"/> and <paramref name="folder"/> on an exact,
        /// unambiguous match; returns <c>false</c> (and empty out-params) otherwise.
        ///
        /// <para>Match criteria — ALL must hold:</para>
        /// <list type="number">
        ///   <item>The SSH target parses as <c>[user@]host:remotepath</c> with the
        ///   host being at least two characters before the first colon.</item>
        ///   <item>Exactly one SSH store's Host matches the target host
        ///   (case-insensitive). Stores with a trimmed Host shorter than two
        ///   characters are skipped.</item>
        ///   <item>If the target specifies a username, the store must have a non-blank
        ///   <see cref="RepoStore.User"/> that matches exactly (case-sensitive). A
        ///   store with blank/different user is rejected. If the target omits the
        ///   username, any store on that host is eligible.</item>
        ///   <item>The target remote path starts with the store Root (separator-
        ///   boundary checked) and the remainder is exactly one segment.</item>
        ///   <item>No ambiguity — only one store satisfies all above.</item>
        ///   <item>Stores with ambiguous User (contains '@') or Host (contains ':')
        ///   are skipped — they cannot round-trip through SSH target syntax.</item>
        /// </list>
        /// </summary>
        public static bool TryResolve(
            string sshTarget,
            IReadOnlyList<RepoStore> stores,
            out string storeId,
            out string folder)
        {
            storeId = string.Empty;
            folder = string.Empty;

            if (string.IsNullOrWhiteSpace(sshTarget) || stores == null || stores.Count == 0)
                return false;

            // Parse "[user@]host:remotepath".
            // sep <= 1 rejects bare Windows drive letters (e.g. "D:\repos\proj" where sep == 1
            // at the drive colon) and degenerate single-character "hostnames".
            var sep = sshTarget.IndexOf(':');
            if (sep <= 1 || sep >= sshTarget.Length - 1)
                return false;

            var hostWithUser = sshTarget.Substring(0, sep).Trim();
            var remotePath = sshTarget.Substring(sep + 1).Trim();

            // Parse the optional "user@" prefix.
            var atPos = hostWithUser.IndexOf('@');
            string targetUser = atPos >= 0 ? hostWithUser.Substring(0, atPos) : null;
            var targetHost = atPos >= 0 ? hostWithUser.Substring(atPos + 1) : hostWithUser;

            if (string.IsNullOrWhiteSpace(targetHost) || string.IsNullOrWhiteSpace(remotePath))
                return false;

            // Detect raw backslash presence BEFORE any normalization — needed for
            // POSIX backslash rejection (POSIX paths must not contain '\').
            bool rawRemoteHasBackslash = remotePath.IndexOf('\\') >= 0;

            string matchedStoreId = null;
            string matchedFolder = null;

            foreach (var store in stores)
            {
                if (!store.IsSsh)
                    continue;

                // Ambiguous persisted syntax: '@' in User or ':' in Host would make
                // the "user@host:path" target format unparseable — skip entirely.
                if (!string.IsNullOrEmpty(store.User) && store.User.IndexOf('@') >= 0)
                    continue;
                if (!string.IsNullOrEmpty(store.Host) && store.Host.IndexOf(':') >= 0)
                    continue;

                // Skip stores with Host too short to be valid (< 2 characters).
                if (string.IsNullOrEmpty(store.Host) || store.Host.Trim().Length < 2)
                    continue;

                // Case-insensitive exact host match.
                if (!string.Equals(store.Host, targetHost, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Username safety: if the target specifies a user, the candidate
                // store must have a matching non-blank User (case-sensitive). When
                // the target omits a user, any store on that host is eligible.
                if (targetUser != null)
                {
                    if (string.IsNullOrWhiteSpace(store.User))
                        continue;
                    if (!string.Equals(store.User, targetUser, StringComparison.Ordinal))
                        continue;
                }

                var rawRoot = store.Root ?? string.Empty;
                if (string.IsNullOrWhiteSpace(rawRoot))
                    continue;

                // POSIX filesystem root "/" special case.
                bool isPosixFilesystemRoot = rawRoot.Length > 0 && rawRoot.TrimStart('/').Length == 0;

                // Classify root style: Windows (drive letter), POSIX (leading '/'), or relative.
                bool rootIsWindows = !isPosixFilesystemRoot && IsWindowsStylePath(rawRoot);
                bool rootIsPosix = isPosixFilesystemRoot || IsPosixStylePath(rawRoot);
                bool rootIsRelative = !rootIsWindows && !rootIsPosix;

                // Effective style: absolute roots dictate style; relative roots
                // infer from the raw target separators.
                bool isWindows, isPosix;
                if (rootIsRelative)
                {
                    isWindows = rawRemoteHasBackslash;
                    isPosix = !rawRemoteHasBackslash;
                }
                else
                {
                    isWindows = rootIsWindows;
                    isPosix = rootIsPosix;
                }

                // POSIX rejects any backslash rather than converting it.
                if (isPosix && rawRemoteHasBackslash)
                    continue;

                // Normalize: only Windows style converts '\' to '/'.
                var normalizedRemotePath = isWindows ? remotePath.Replace('\\', '/') : remotePath;
                var normalizedRoot = isWindows ? rawRoot.Replace('\\', '/') : rawRoot;

                // Trim trailing separators; handle filesystem root "/" → empty prefix.
                normalizedRoot = isPosixFilesystemRoot ? string.Empty : normalizedRoot.TrimEnd('/');

                // For absolute roots, reject mismatched absolute targets.
                if (!rootIsRelative)
                {
                    bool remoteIsAbsWindows = IsWindowsStylePath(normalizedRemotePath);
                    bool remoteIsAbsPosix = IsPosixStylePath(normalizedRemotePath);
                    if (remoteIsAbsWindows || remoteIsAbsPosix)
                    {
                        if (isWindows && !remoteIsAbsWindows) continue;
                        if (isPosix && !remoteIsAbsPosix) continue;
                    }
                }

                // POSIX is case-sensitive; Windows is not.
                var pathComparison = isPosix
                    ? StringComparison.Ordinal
                    : StringComparison.OrdinalIgnoreCase;

                // Prefix check: "root/" boundary prevents partial-segment matches.
                var requiredPrefix = normalizedRoot + "/";
                if (!normalizedRemotePath.StartsWith(requiredPrefix, pathComparison))
                    continue;

                var remainder = normalizedRemotePath.Substring(requiredPrefix.Length);

                // Exactly one non-empty segment — no nesting allowed.
                if (string.IsNullOrEmpty(remainder) || remainder.IndexOf('/') >= 0)
                    continue;

                // Ambiguity guard: a second matching store → reject entirely.
                if (matchedStoreId != null)
                    return false;

                matchedStoreId = store.Id;
                matchedFolder = remainder;
            }

            if (matchedStoreId == null)
                return false;

            storeId = matchedStoreId;
            folder = matchedFolder;
            return true;
        }

        /// <summary>Windows-style path: second character is ':' after a drive letter.</summary>
        private static bool IsWindowsStylePath(string path) =>
            path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':';

        /// <summary>POSIX-style path: starts with '/'.</summary>
        private static bool IsPosixStylePath(string path) =>
            path.Length > 0 && path[0] == '/';
    }
}
