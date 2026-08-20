#nullable enable
using System;
using System.Collections.Generic;

namespace ControlTower.Core.Models
{
    public enum RepoKind { WorkingTree, BareRepo, WorktreePointer, Submodule, Other }

    public enum RemoteState { HasOrigin, NoRemote, OriginHasCredentials }

    public enum DuplicateKind { None, Path, Origin }

    public enum ScanIssueKind { AccessDenied, PathTooLong, IOError, Cancelled }

    public sealed record ScanOptions(
        int MaxDepth = 3,
        int MaxRoots = 4,
        bool FollowSymlinks = false,
        TimeSpan? TimeoutPerRoot = null);

    public sealed record ScanCandidate(
        string RootPath,
        string FolderPath,
        string FolderName,
        string SuggestedSlug,
        string DisplayOriginUrl,
        string RawOriginUrl,
        string DedupeIdentity,
        string Branch,
        RepoKind Kind,
        RemoteState RemoteState,
        DuplicateKind DuplicateKind,
        string DuplicateOfProjectId,
        string Detail);

    public sealed record ScanIssue(
        string RootPath,
        string FolderPath,
        ScanIssueKind Kind,
        string Message);

    public sealed record ScanProgressUpdate(
        string RootPath,
        int FoldersWalked,
        int ReposFound,
        string CurrentPath);

    public sealed record ScanResult(
        IReadOnlyList<ScanCandidate> Candidates,
        IReadOnlyList<ScanIssue> Issues,
        int TotalFoldersWalked,
        bool CompletedFully);

    /// <summary>
    /// Single source of truth for credential handling on git remote URLs.
    /// Referenced from the repo scanner, the desktop view model, and the
    /// registration request validator so all three layers agree on what
    /// counts as a credential and how to strip it for safe display /
    /// persistence.
    /// </summary>
    public static class UrlSanitizer
    {
        /// <summary>
        /// Returns a credential-free form of <paramref name="url"/> suitable
        /// for display in the UI and persistence to <c>portfolio.yml</c> or
        /// <c>project.yml</c>. The scheme and host are preserved. For
        /// <c>ssh://user@host/path</c> the user portion is retained as part
        /// of the identity; only an embedded password is stripped. For
        /// <c>HTTP URL containing user-info credentials</c> the entire user-info is
        /// removed. Malformed input is returned trimmed but otherwise
        /// unchanged.
        /// </summary>
        public static string StripCredentials(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return string.Empty;
            }

            var trimmed = url.Trim();

            // scp-like "user@host:path" cannot carry a password — return as-is.
            if (!trimmed.Contains("://"))
            {
                return trimmed;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return trimmed;
            }

            var scheme = uri.Scheme.ToLowerInvariant();
            var userInfo = uri.UserInfo ?? string.Empty;
            string newUserInfoWithAt = string.Empty;

            if (!string.IsNullOrEmpty(userInfo))
            {
                if (scheme == "ssh")
                {
                    // Keep the user part (it's identity); drop any password.
                    var colon = userInfo.IndexOf(':');
                    var user = colon >= 0 ? userInfo.Substring(0, colon) : userInfo;
                    if (!string.IsNullOrEmpty(user))
                    {
                        newUserInfoWithAt = user + "@";
                    }
                }
                else
                {
                    // For https/http/git: user-info is always credential — strip entirely.
                    newUserInfoWithAt = string.Empty;
                }
            }

            var host = uri.Host;
            var port = uri.IsDefaultPort ? string.Empty
                : ":" + uri.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var pathAndQuery = uri.PathAndQuery;
            var fragment = uri.Fragment ?? string.Empty;

            return scheme + "://" + newUserInfoWithAt + host + port + pathAndQuery + fragment;
        }

        /// <summary>
        /// Returns true if <paramref name="url"/> embeds a credential we'd
        /// refuse to persist. An HTTP URL containing user-info
        /// counts (HTTPS user-info is always a credential of some form,
        /// including PATs and "x-access-token" patterns). <c>ssh://</c>
        /// only counts when a password is present (<c>user:pass@host</c>).
        /// scp-like <c>user@host:path</c> never carries a credential.
        /// </summary>
        public static bool HasCredentials(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return false;
            }

            var trimmed = url.Trim();
            if (!trimmed.Contains("://"))
            {
                return false;
            }

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
            {
                return false;
            }

            if (string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            if (string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase))
            {
                return uri.UserInfo.Contains(':');
            }

            // https, http, git, file: any user-info component is treated as credential.
            return true;
        }
    }
}
