#nullable enable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Infrastructure.Archive
{
    /// <summary>
    /// Flat key=value per-project state file, mirroring the
    /// <c>LocalSnapshotStore</c> cache pattern. Lives under the app's local
    /// state root (never inside the Velopack install directory).
    /// </summary>
    public sealed class ArchiveStateStore : IArchiveStateStore
    {
        private readonly string _stateFolder;

        public ArchiveStateStore(string localStateRoot)
        {
            _stateFolder = Path.Combine(
                string.IsNullOrWhiteSpace(localStateRoot) ? string.Empty : localStateRoot,
                "archive-state");
        }

        public ArchiveRepoState Load(string projectId)
        {
            var state = new ArchiveRepoState { ProjectId = projectId ?? string.Empty };
            if (!ArchiveProjectIdentity.IsSafe(projectId))
            {
                return state;
            }

            var path = GetPath(projectId ?? string.Empty);
            if (string.IsNullOrWhiteSpace(path))
            {
                return state;
            }

            if (!File.Exists(path))
            {
                return state;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(path);
            }
            catch (IOException)
            {
                return state;
            }
            catch (UnauthorizedAccessException)
            {
                return state;
            }

            foreach (var line in lines)
            {
                var index = line.IndexOf('=');
                if (index <= 0)
                {
                    continue;
                }

                var key = line.Substring(0, index);
                var value = line.Substring(index + 1);

                switch (key)
                {
                    case "ProjectId":
                        state.ProjectId = value;
                        break;
                    case "ArchivePath":
                        state.ArchivePath = value;
                        break;
                    case "PreviousArchivePaths":
                        foreach (var archivePath in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        {
                            state.PreviousArchivePaths.Add(archivePath);
                        }
                        break;
                    case "LastSyncedUtc":
                        state.LastSyncedUtc = ParseDate(value);
                        break;
                    case "LastSyncedCommitSha":
                        state.LastSyncedCommitSha = value;
                        break;
                    case "UpstreamStatus":
                        if (Enum.TryParse<ArchiveUpstreamStatus>(value, out var upstream))
                        {
                            state.UpstreamStatus = upstream;
                        }
                        break;
                    case "LastError":
                        state.LastError = value;
                        break;
                    case "AutoSyncEnabled":
                        state.AutoSyncEnabled = !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
                        break;
                    case "LastReleaseTag":
                        state.LastReleaseTag = value;
                        break;
                    case "ReleaseStatus":
                        if (Enum.TryParse<ArchiveReleaseStatus>(value, out var releaseStatus))
                        {
                            state.ReleaseStatus = releaseStatus;
                        }
                        break;
                    case "LastReleaseIntegrity":
                        if (Enum.TryParse<ArchiveReleaseIntegrity>(value, out var integrity))
                        {
                            state.LastReleaseIntegrity = integrity;
                        }
                        break;
                    case "LastReleaseCapturedUtc":
                        state.LastReleaseCapturedUtc = ParseDate(value);
                        break;
                    case "LastReleaseDirectoryName":
                        state.LastReleaseDirectoryName = value;
                        break;
                    case "LastReleaseAssetNames":
                        foreach (var assetName in value.Split('|', StringSplitOptions.RemoveEmptyEntries))
                        {
                            state.LastReleaseAssetNames.Add(assetName);
                        }
                        break;
                    case "LastReleaseError":
                        state.LastReleaseError = value;
                        break;
                    case "UpstreamFullName":
                        state.UpstreamFullName = value;
                        break;
                }
            }

            return state;
        }

        public void Save(ArchiveRepoState state)
        {
            if (state == null || !ArchiveProjectIdentity.IsSafe(state.ProjectId))
            {
                return;
            }

            Directory.CreateDirectory(_stateFolder);

            var lines = new List<string>
            {
                "ProjectId=" + (state.ProjectId ?? string.Empty),
                "ArchivePath=" + (state.ArchivePath ?? string.Empty),
                "PreviousArchivePaths=" + string.Join("|", state.PreviousArchivePaths ?? Array.Empty<string>()),
                "LastSyncedUtc=" + FormatDate(state.LastSyncedUtc),
                "LastSyncedCommitSha=" + (state.LastSyncedCommitSha ?? string.Empty),
                "UpstreamStatus=" + state.UpstreamStatus,
                "LastError=" + (state.LastError ?? string.Empty),
                "AutoSyncEnabled=" + (state.AutoSyncEnabled ? "true" : "false"),
                "LastReleaseTag=" + (state.LastReleaseTag ?? string.Empty),
                "ReleaseStatus=" + state.ReleaseStatus,
                "LastReleaseIntegrity=" + state.LastReleaseIntegrity,
                "LastReleaseCapturedUtc=" + FormatDate(state.LastReleaseCapturedUtc),
                "LastReleaseDirectoryName=" + (state.LastReleaseDirectoryName ?? string.Empty),
                "LastReleaseAssetNames=" + string.Join("|", state.LastReleaseAssetNames ?? Array.Empty<string>()),
                "LastReleaseError=" + (state.LastReleaseError ?? string.Empty),
                "UpstreamFullName=" + (state.UpstreamFullName ?? string.Empty)
            };

            var path = GetPath(state.ProjectId ?? string.Empty);
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            var tempPath = path + ".tmp";
            File.WriteAllLines(tempPath, lines);
            File.Copy(tempPath, path, overwrite: true);
            File.Delete(tempPath);
        }

        private static DateTime? ParseDate(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            return DateTime.TryParse(
                value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }

        private static string FormatDate(DateTime? value) =>
            value.HasValue ? value.Value.ToString("o", CultureInfo.InvariantCulture) : string.Empty;

        private string GetPath(string projectId)
        {
            return ArchiveProjectIdentity.IsSafe(projectId)
                ? Path.Combine(_stateFolder, projectId + ".state")
                : string.Empty;
        }
    }
}
