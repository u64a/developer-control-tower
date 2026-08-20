using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Infrastructure.Cache
{
    public sealed class LocalSnapshotStore : ISnapshotStore
    {
        private readonly string _cacheFolder;

        public LocalSnapshotStore()
        {
            _cacheFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DeveloperControlTower",
                "cache");
        }

        // Test seam: lets tests target a temp folder without touching LocalAppData.
        public LocalSnapshotStore(string cacheFolder)
        {
            _cacheFolder = cacheFolder ?? string.Empty;
        }

        /// <summary>
        /// Loads a cached snapshot for <paramref name="projectId"/>. If the
        /// stored file is missing, truncated, contains invalid scalars, or is
        /// otherwise corrupt, returns <c>null</c> and records the issue via
        /// <see cref="LastIssues"/> with code <c>cache/corrupt</c>. Never
        /// throws on I/O or parse problems.
        /// </summary>
        public RepoSnapshot Load(string projectId)
        {
            return Load(projectId, out _);
        }

        /// <summary>
        /// Overload that exposes any corruption/IO issues encountered while
        /// reading the cached snapshot. <c>issues</c> is always non-null.
        /// </summary>
        public RepoSnapshot Load(string projectId, out IReadOnlyList<ValidationIssue> issues)
        {
            var collected = new List<ValidationIssue>();
            issues = collected;

            if (string.IsNullOrWhiteSpace(projectId))
            {
                return null;
            }

            string path;
            try
            {
                path = GetPath(projectId);
            }
            catch (Exception ex)
            {
                collected.Add(new ValidationIssue(
                    IssueSeverity.Warning,
                    "cache/corrupt",
                    "Snapshot cache path could not be resolved: " + ex.Message));
                return null;
            }

            if (!File.Exists(path))
            {
                return null;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (Exception ex)
            {
                collected.Add(new ValidationIssue(
                    IssueSeverity.Warning,
                    "cache/corrupt",
                    "Snapshot cache could not be read: " + ex.Message));
                return null;
            }

            var snapshot = new RepoSnapshot();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var corruptFields = new List<string>();

            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                if (string.IsNullOrEmpty(line))
                {
                    continue;
                }

                var index = line.IndexOf('=');
                if (index <= 0)
                {
                    corruptFields.Add("line " + (lineIndex + 1) + " (malformed)");
                    continue;
                }

                string key;
                string value;
                try
                {
                    key = line.Substring(0, index);
                    value = line.Substring(index + 1);
                }
                catch (Exception)
                {
                    corruptFields.Add("line " + (lineIndex + 1) + " (truncated)");
                    continue;
                }

                seenKeys.Add(key);

                if (key == "RepoPath")
                {
                    snapshot.RepoPath = value;
                }
                else if (key == "IsAvailable")
                {
                    if (!TryParseBool(value, out var available))
                    {
                        corruptFields.Add("IsAvailable");
                        continue;
                    }
                    snapshot.IsAvailable = available;
                }
                else if (key == "Branch")
                {
                    snapshot.Branch = value;
                }
                else if (key == "IsDirty")
                {
                    if (!TryParseBool(value, out var dirty))
                    {
                        corruptFields.Add("IsDirty");
                        continue;
                    }
                    snapshot.IsDirty = dirty;
                }
                else if (key == "HasUpstream")
                {
                    if (!TryParseBool(value, out var upstream))
                    {
                        corruptFields.Add("HasUpstream");
                        continue;
                    }
                    snapshot.HasUpstream = upstream;
                }
                else if (key == "AheadBy")
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    {
                        corruptFields.Add("AheadBy");
                        continue;
                    }
                    snapshot.AheadBy = parsed;
                }
                else if (key == "BehindBy")
                {
                    if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                    {
                        corruptFields.Add("BehindBy");
                        continue;
                    }
                    snapshot.BehindBy = parsed;
                }
                else if (key == "LastCommitUtc")
                {
                    if (string.IsNullOrEmpty(value))
                    {
                        snapshot.LastCommitUtc = null;
                    }
                    else if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                    {
                        snapshot.LastCommitUtc = parsed;
                    }
                    else
                    {
                        corruptFields.Add("LastCommitUtc");
                    }
                }
                else if (key == "StatusMessage")
                {
                    snapshot.StatusMessage = value;
                }
                else if (key == "OriginUrl")
                {
                    snapshot.OriginUrl = value;
                }
            }

            // Truncated file: required key set incomplete. We treat any missing
            // mandatory scalar as corruption rather than silently defaulting.
            var required = new[] { "RepoPath", "IsAvailable", "Branch", "IsDirty", "HasUpstream", "AheadBy", "BehindBy", "LastCommitUtc", "StatusMessage", "OriginUrl" };
            var missing = new List<string>();
            foreach (var key in required)
            {
                if (!seenKeys.Contains(key))
                {
                    missing.Add(key);
                }
            }

            if (corruptFields.Count > 0 || missing.Count > 0)
            {
                var parts = new List<string>();
                if (corruptFields.Count > 0)
                {
                    parts.Add("invalid: " + string.Join(", ", corruptFields));
                }
                if (missing.Count > 0)
                {
                    parts.Add("missing: " + string.Join(", ", missing));
                }

                collected.Add(new ValidationIssue(
                    IssueSeverity.Warning,
                    "cache/corrupt",
                    "Snapshot cache for '" + projectId + "' is corrupt (" + string.Join("; ", parts) + "). Cache discarded; refresh to rebuild."));
                return null;
            }

            return snapshot;
        }

        public void Save(string projectId, RepoSnapshot snapshot)
        {
            if (snapshot == null || string.IsNullOrWhiteSpace(projectId))
            {
                return;
            }

            Directory.CreateDirectory(_cacheFolder);

            var lines = new List<string>();
            lines.Add("RepoPath=" + (snapshot.RepoPath ?? string.Empty));
            lines.Add("IsAvailable=" + snapshot.IsAvailable.ToString().ToLowerInvariant());
            lines.Add("Branch=" + (snapshot.Branch ?? string.Empty));
            lines.Add("IsDirty=" + snapshot.IsDirty.ToString().ToLowerInvariant());
            lines.Add("HasUpstream=" + snapshot.HasUpstream.ToString().ToLowerInvariant());
            lines.Add("AheadBy=" + snapshot.AheadBy.ToString(CultureInfo.InvariantCulture));
            lines.Add("BehindBy=" + snapshot.BehindBy.ToString(CultureInfo.InvariantCulture));
            lines.Add("LastCommitUtc=" + (snapshot.LastCommitUtc.HasValue ? snapshot.LastCommitUtc.Value.ToString("o", CultureInfo.InvariantCulture) : string.Empty));
            lines.Add("StatusMessage=" + (snapshot.StatusMessage ?? string.Empty));
            lines.Add("OriginUrl=" + (snapshot.OriginUrl ?? string.Empty));

            File.WriteAllLines(GetPath(projectId), lines.ToArray());
        }

        private static bool TryParseBool(string value, out bool parsed)
        {
            if (string.Equals(value, "true", StringComparison.OrdinalIgnoreCase))
            {
                parsed = true;
                return true;
            }
            if (string.Equals(value, "false", StringComparison.OrdinalIgnoreCase))
            {
                parsed = false;
                return true;
            }
            parsed = false;
            return false;
        }

        private string GetPath(string projectId)
        {
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                projectId = projectId.Replace(invalid, '_');
            }

            return Path.Combine(_cacheFolder, projectId + ".snapshot");
        }
    }
}
