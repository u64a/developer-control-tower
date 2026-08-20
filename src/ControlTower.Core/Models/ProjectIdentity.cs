using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ControlTower.Core.Models
{
    /// <summary>
    /// Central helper for project identity stability and sentinel detection.
    ///
    /// Background: <c>ProjectYamlProvider</c> assigns shared sentinel ids to unconfigured
    /// folders — <c>"missing.project"</c> (no project.yml) and <c>"invalid.project"</c>
    /// (project.yml present but no <c>id:</c> field). In a real portfolio, many projects
    /// may legitimately lack a config file. These shared sentinels collide everywhere an
    /// id is used as a key: snapshot cache, seed/refresh look-ups, and the dedup in
    /// <c>ControlTowerService.LoadPortfolio()</c>. The result is data loss — multiple
    /// real projects collapse into one row.
    ///
    /// This class provides:
    /// <list type="bullet">
    ///   <item><see cref="IsUnstable"/> — detects any id that must never be used as a
    ///     stable key (empty, or carrying a known prefix).</item>
    ///   <item><see cref="CreateFallback"/> — generates a deterministic, path-derived
    ///     replacement id that is unique per folder yet still recognisably "unstable"
    ///     via its prefix, so downstream code can always detect it.</item>
    /// </list>
    /// </summary>
    public static class ProjectIdentity
    {
        /// <summary>Prefix used by <c>ProjectYamlProvider</c> when no project.yml exists.</summary>
        public const string MissingPrefix = "missing.";

        /// <summary>Prefix used by <c>ProjectYamlProvider</c> when project.yml has no <c>id:</c>.</summary>
        public const string InvalidPrefix = "invalid.";

        /// <summary>
        /// Returns <see langword="true"/> when <paramref name="id"/> is not a stable,
        /// canonical project identity — i.e. it is null/whitespace or starts with a
        /// known sentinel prefix. Such ids must never be used as dedup keys or cache keys.
        /// </summary>
        public static bool IsUnstable(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return true;

            return id.StartsWith(MissingPrefix, StringComparison.OrdinalIgnoreCase)
                || id.StartsWith(InvalidPrefix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Creates a deterministic fallback id for an unconfigured project folder.
        /// The id is formed as <c><paramref name="prefix"/> + 12-char-lowercase-hex</c>
        /// where the hex is the first 12 characters of the SHA-256 of the
        /// case-normalised, fully-resolved folder path.
        ///
        /// Properties:
        /// <list type="bullet">
        ///   <item>Distinct folders always produce distinct ids.</item>
        ///   <item>The same folder across reloads always produces the same id (stable).</item>
        ///   <item>The prefix keeps <see cref="IsUnstable"/> returning <see langword="true"/>,
        ///     so downstream code can always identify the id as non-canonical.</item>
        /// </list>
        /// </summary>
        /// <param name="prefix">
        /// One of <see cref="MissingPrefix"/> or <see cref="InvalidPrefix"/>.
        /// </param>
        /// <param name="projectRootPath">
        /// The filesystem path of the project folder. May be null or empty (hash falls
        /// back to the empty string rather than throwing).
        /// </param>
        public static string CreateFallback(string prefix, string projectRootPath)
        {
            var normalised = Normalise(projectRootPath);
            var hash = ComputeShortHash(normalised);
            return prefix + hash;
        }

        // ── internals ────────────────────────────────────────────────────────────

        private static string Normalise(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFullPath(path).ToLowerInvariant();
            }
            catch
            {
                return path.ToLowerInvariant();
            }
        }

        private static string ComputeShortHash(string input)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            var hash = SHA256.HashData(bytes);
            // Take first 6 bytes → 12 lowercase hex chars.
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }
    }
}
