#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Core.UseCases
{
    /// <summary>
    /// Pure scanner that decides, for each input project, whether its
    /// expected local path is missing, empty, an actionable conflict, an
    /// already-cloned working tree, or an existing repo we refuse to
    /// overwrite. See <see cref="RestoreClassification"/> for the full
    /// state space.
    /// </summary>
    /// <remarks>
    /// SSH-store projects (<see cref="ProjectRestoreInput.IsLocalStore"/>
    /// false) and projects with an empty <see cref="ProjectRestoreInput.RemoteUrl"/>
    /// are filtered out before classification — they are not candidates.
    /// </remarks>
    public sealed class MissingProjectScanner : IMissingProjectScanner
    {
        private readonly IGitWorkspaceInspector _inspector;

        public MissingProjectScanner(IGitWorkspaceInspector inspector)
        {
            _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
        }

        public async Task<IReadOnlyList<RestoreCandidate>> ScanAsync(
            IReadOnlyList<ProjectRestoreInput> projects,
            CancellationToken ct)
        {
            var results = new List<RestoreCandidate>();
            if (projects == null)
            {
                return results;
            }

            foreach (var project in projects)
            {
                ct.ThrowIfCancellationRequested();

                if (project == null) continue;
                if (!project.IsLocalStore) continue;
                if (string.IsNullOrWhiteSpace(project.ExpectedPath)) continue;

                var candidate = await ClassifyAsync(project, ct).ConfigureAwait(false);
                results.Add(candidate);
            }

            return results;
        }

        private async Task<RestoreCandidate> ClassifyAsync(
            ProjectRestoreInput project, CancellationToken ct)
        {
            var canonicalInputRemote = string.IsNullOrWhiteSpace(project.RemoteUrl)
                ? string.Empty
                : _inspector.CanonicalizeRemote(project.RemoteUrl);

            if (!Directory.Exists(project.ExpectedPath))
            {
                if (string.IsNullOrWhiteSpace(project.RemoteUrl))
                {
                    return MakeCandidate(project, project.RemoteUrl, canonicalInputRemote,
                        RestoreClassification.MissingNeedsUrl,
                        "Folder is missing and no remote URL is cached. Paste the origin URL below to enable clone.");
                }

                return MakeCandidate(project, project.RemoteUrl, canonicalInputRemote,
                    RestoreClassification.Missing,
                    "Folder does not exist.");
            }

            // Empty folder = no entries (files OR subdirs).
            if (!Directory.EnumerateFileSystemEntries(project.ExpectedPath).Any())
            {
                if (string.IsNullOrWhiteSpace(project.RemoteUrl))
                {
                    return MakeCandidate(project, project.RemoteUrl, canonicalInputRemote,
                        RestoreClassification.MissingNeedsUrl,
                        "Folder is empty and no remote URL is cached. Paste the origin URL below to enable clone.");
                }

                return MakeCandidate(project, project.RemoteUrl, canonicalInputRemote,
                    RestoreClassification.EmptyFolder,
                    "Folder exists but is empty.");
            }

            var classification = await _inspector.ClassifyAsync(project.ExpectedPath, ct)
                .ConfigureAwait(false);

            switch (classification)
            {
                case NotARepo:
                    if (string.IsNullOrWhiteSpace(project.RemoteUrl))
                    {
                        return MakeCandidate(project, project.RemoteUrl, canonicalInputRemote,
                            RestoreClassification.MissingNeedsUrl,
                            "Folder has content (not a git repo) and no remote URL is cached. Paste the origin URL below to enable Quarantine & clone.");
                    }

                    return MakeCandidate(project, project.RemoteUrl, canonicalInputRemote,
                        RestoreClassification.ConflictNonEmpty,
                        "Folder has content but is not a git repository.");

                case BareRepo:
                    return MakeCandidate(project, project.RemoteUrl, canonicalInputRemote,
                        RestoreClassification.UnsafeExisting,
                        "Existing bare repository — will not overwrite.");

                case WorkingTreeRepo working:
                    {
                        // Live git origin wins when present — it's the operational truth.
                        // Surface it as the candidate's RemoteUrl so the VM can persist
                        // any change back to portfolio.yml as a cache update.
                        var liveRemote = string.IsNullOrWhiteSpace(working.OriginUrl)
                            ? project.RemoteUrl
                            : working.OriginUrl;
                        var canonicalLiveRemote = string.IsNullOrWhiteSpace(liveRemote)
                            ? string.Empty
                            : _inspector.CanonicalizeRemote(liveRemote);

                        if (working.IsShallow || working.IsSparse || working.IsPartialClone ||
                            working.HasWorktrees || working.HasSubmodules)
                        {
                            var reasons = new List<string>();
                            if (working.IsShallow) reasons.Add("shallow");
                            if (working.IsSparse) reasons.Add("sparse");
                            if (working.IsPartialClone) reasons.Add("partial");
                            if (working.HasWorktrees) reasons.Add("worktrees");
                            if (working.HasSubmodules) reasons.Add("submodules");
                            return MakeCandidate(project, liveRemote, canonicalLiveRemote,
                                RestoreClassification.UnsafeExisting,
                                "Existing repo is " + string.Join("/", reasons) + " — will not overwrite.");
                        }

                        // Compare canonical input remote (if known) to the live origin
                        // to decide AlreadyCloned vs UnsafeExisting. Empty input means
                        // we have nothing to compare against — surface as Unsafe so
                        // the user can verify before any action is taken.
                        if (!string.IsNullOrWhiteSpace(canonicalLiveRemote) &&
                            !string.IsNullOrWhiteSpace(canonicalInputRemote) &&
                            string.Equals(canonicalLiveRemote, canonicalInputRemote, StringComparison.OrdinalIgnoreCase))
                        {
                            return MakeCandidate(project, liveRemote, canonicalLiveRemote,
                                RestoreClassification.AlreadyCloned,
                                "Already cloned (origin matches).");
                        }

                        if (string.IsNullOrWhiteSpace(canonicalLiveRemote))
                        {
                            return MakeCandidate(project, liveRemote, canonicalLiveRemote,
                                RestoreClassification.UnsafeExisting,
                                "Existing repo has no origin — will not overwrite.");
                        }

                        if (string.IsNullOrWhiteSpace(canonicalInputRemote))
                        {
                            // No cached/input URL but we discovered one live — treat as
                            // AlreadyCloned (it's a healthy repo at the expected path)
                            // and let the VM persist the discovered origin to cache.
                            return MakeCandidate(project, liveRemote, canonicalLiveRemote,
                                RestoreClassification.AlreadyCloned,
                                "Already cloned. Origin URL will be cached for future restores.");
                        }

                        return MakeCandidate(project, liveRemote, canonicalLiveRemote,
                            RestoreClassification.UnsafeExisting,
                            "Existing repo has origin " + working.OriginUrl + " — will not overwrite.");
                    }

                default:
                    return MakeCandidate(project, project.RemoteUrl, canonicalInputRemote,
                        RestoreClassification.UnsafeExisting,
                        "Unknown git workspace classification.");
            }
        }

        private static RestoreCandidate MakeCandidate(
            ProjectRestoreInput project, string remoteUrl, string canonicalRemote,
            RestoreClassification classification, string detail)
        {
            return new RestoreCandidate(
                ProjectId: project.ProjectId,
                ProjectName: project.ProjectName,
                Slug: project.Slug,
                ExpectedPath: project.ExpectedPath,
                RemoteUrl: remoteUrl ?? string.Empty,
                CanonicalRemoteUrl: canonicalRemote ?? string.Empty,
                Classification: classification,
                Detail: detail);
        }
    }
}
