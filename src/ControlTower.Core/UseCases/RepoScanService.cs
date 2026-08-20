#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Core.UseCases
{
    /// <summary>
    /// Walks root folders looking for git repositories at bounded depth.
    /// Each candidate is classified via <see cref="IGitWorkspaceInspector"/>
    /// (no network IO, no mutation) and dedup'd against the live portfolio
    /// by both filesystem path and credential-stripped remote identity.
    /// The result is the input to the Scan-and-Register dialog.
    /// </summary>
    public sealed class RepoScanService : IRepoScanService
    {
        private static readonly HashSet<string> AlwaysSkipNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".git",
                "node_modules",
                "bin",
                "obj",
                ".vs",
                ".idea",
                "__pycache__"
            };

        private static readonly HashSet<string> SystemDriveRootSkipNames =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Windows",
                "Program Files",
                "Program Files (x86)",
                "ProgramData",
                "$Recycle.Bin",
                "System Volume Information"
            };

        private readonly IGitWorkspaceInspector _inspector;
        private readonly IPortfolioProvider _portfolioProvider;
        private readonly IProjectProvider _projectProvider;
        private readonly IProjectMetadataLocator? _metadataLocator;
        private readonly WorkspaceProfile _activeProfile;

        public RepoScanService(
            IGitWorkspaceInspector inspector,
            IPortfolioProvider portfolioProvider,
            IProjectProvider projectProvider,
            IProjectMetadataLocator? metadataLocator = null,
            WorkspaceProfile? activeProfile = null)
        {
            _inspector = inspector ?? throw new ArgumentNullException(nameof(inspector));
            _portfolioProvider = portfolioProvider ?? throw new ArgumentNullException(nameof(portfolioProvider));
            _projectProvider = projectProvider ?? throw new ArgumentNullException(nameof(projectProvider));
            _metadataLocator = metadataLocator;
            _activeProfile = activeProfile ?? WorkspaceProfilePolicy.CreateAllProjectsProfile();
        }

        private string ResolveMetadataRoot(ProjectRef projectRef)
        {
            if (_metadataLocator != null && projectRef != null && !string.IsNullOrWhiteSpace(projectRef.Id))
            {
                return _metadataLocator.ResolveMetadataRoot(projectRef.Id);
            }

            return projectRef?.Path ?? string.Empty;
        }

        public async Task<ScanResult> ScanAsync(
            IReadOnlyList<string> rootPaths,
            ScanOptions options,
            IProgress<ScanProgressUpdate>? progress,
            CancellationToken ct)
        {
            options ??= new ScanOptions();
            rootPaths ??= Array.Empty<string>();

            var candidates = new List<ScanCandidate>();
            var issues = new List<ScanIssue>();
            int totalWalked = 0;

            BuildPortfolioDedupeMaps(
                out var existingPaths,
                out var existingOrigins,
                out var excludedPaths);
            // In-scan dedupe: track folder paths and remote identities we've
            // already emitted so a user-supplied overlapping pair of roots
            // (e.g. C:\repos and C:\repos\subset) doesn't list the same repo
            // twice. Later occurrences are marked DuplicateKind.Path/Origin.
            var emittedPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var emittedOrigins = new Dictionary<string, string>(StringComparer.Ordinal);
            var emittedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Cap roots — defence in depth on top of any UI cap.
            int rootCount = Math.Min(rootPaths.Count, options.MaxRoots);

            for (int i = 0; i < rootCount; i++)
            {
                ct.ThrowIfCancellationRequested();

                var rawRoot = rootPaths[i];
                if (string.IsNullOrWhiteSpace(rawRoot))
                {
                    continue;
                }

                string normalisedRoot;
                try
                {
                    normalisedRoot = Path.GetFullPath(rawRoot.Trim());
                }
                catch (Exception ex)
                {
                    issues.Add(new ScanIssue(rawRoot, rawRoot, ScanIssueKind.IOError,
                        "Could not normalise root path: " + ex.Message));
                    continue;
                }

                if (!Directory.Exists(normalisedRoot))
                {
                    issues.Add(new ScanIssue(normalisedRoot, normalisedRoot, ScanIssueKind.IOError,
                        "Root folder does not exist."));
                    continue;
                }

                bool isDriveRoot = IsDriveRoot(normalisedRoot);

                await WalkRootAsync(
                    normalisedRoot,
                    isDriveRoot,
                    options,
                    progress,
                    ct,
                    candidates,
                    issues,
                    existingPaths,
                    existingOrigins,
                    excludedPaths,
                    emittedPaths,
                    emittedOrigins,
                    emittedSlugs,
                    walkedCounter: w => totalWalked += w).ConfigureAwait(false);
            }

            return new ScanResult(
                Candidates: candidates,
                Issues: issues,
                TotalFoldersWalked: totalWalked,
                CompletedFully: !ct.IsCancellationRequested);
        }

        private void BuildPortfolioDedupeMaps(
            out Dictionary<string, string> existingPaths,
            out Dictionary<string, string> existingOrigins,
            out HashSet<string> excludedPaths)
        {
            existingPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            existingOrigins = new Dictionary<string, string>(StringComparer.Ordinal);
            excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            PortfolioIndex portfolio;
            try
            {
                portfolio = _portfolioProvider.LoadPortfolio();
            }
            catch
            {
                // Best-effort: a missing or unreadable portfolio simply
                // means we won't pre-mark any duplicates, which is the
                // right default for a fresh-laptop scenario.
                return;
            }

            if (portfolio?.Projects == null)
            {
                return;
            }

            foreach (var project in portfolio.Projects)
            {
                if (project == null || string.IsNullOrWhiteSpace(project.Id))
                {
                    continue;
                }

                if (!_activeProfile.IncludesProject(project.Id))
                {
                    if (!string.IsNullOrWhiteSpace(project.Path))
                    {
                        excludedPaths.Add(NormalizePath(project.Path));
                    }
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(project.Path))
                {
                    var normalised = NormalizePath(project.Path);

                    if (!existingPaths.ContainsKey(normalised))
                    {
                        existingPaths[normalised] = project.Id;
                    }
                }

                string originUrl = project.RemoteUrl ?? string.Empty;

                // Legacy entries may carry no RemoteUrl on the portfolio row.
                // Best-effort read of .controltower/project.yml for the
                // origin; failures are silent (this is opportunistic).
                if (string.IsNullOrWhiteSpace(originUrl) && !string.IsNullOrWhiteSpace(project.Path))
                {
                    try
                    {
                        var metadataRoot = ResolveMetadataRoot(project);
                        var loaded = _projectProvider.LoadProject(project.Path, metadataRoot);
                        if (loaded?.Project?.Locations != null)
                        {
                            originUrl = loaded.Project.Locations.RemoteUrl ?? string.Empty;
                        }
                    }
                    catch
                    {
                        // Folder may have been deleted, yaml may be malformed —
                        // either way we just skip the origin for this entry.
                    }
                }

                if (!string.IsNullOrWhiteSpace(originUrl))
                {
                    var identity = _inspector.GetRemoteIdentity(UrlSanitizer.StripCredentials(originUrl));
                    if (!string.IsNullOrEmpty(identity) && !existingOrigins.ContainsKey(identity))
                    {
                        existingOrigins[identity] = project.Id;
                    }
                }
            }
        }

        private async Task WalkRootAsync(
            string rootPath,
            bool isDriveRoot,
            ScanOptions options,
            IProgress<ScanProgressUpdate>? progress,
            CancellationToken ct,
            List<ScanCandidate> candidates,
            List<ScanIssue> issues,
            Dictionary<string, string> existingPaths,
            Dictionary<string, string> existingOrigins,
            HashSet<string> excludedPaths,
            Dictionary<string, string> emittedPaths,
            Dictionary<string, string> emittedOrigins,
            HashSet<string> emittedSlugs,
            Action<int> walkedCounter)
        {
            var queue = new Queue<(string Path, int Depth, bool AtDriveRoot)>();
            queue.Enqueue((rootPath, 0, isDriveRoot));
            int walked = 0;
            int reposFound = 0;

            while (queue.Count > 0)
            {
                ct.ThrowIfCancellationRequested();

                var (path, depth, atDriveRoot) = queue.Dequeue();
                walked++;

                // A canonical project outside the active profile is not a scan
                // candidate. Stop before filesystem shape checks or git
                // inspection, and do not descend into its working tree.
                if (excludedPaths.Contains(NormalizePath(path)))
                {
                    continue;
                }

                if (walked % 50 == 0)
                {
                    progress?.Report(new ScanProgressUpdate(rootPath, walked, reposFound, path));
                }

                // Cheap shape probe — no git invocation yet.
                bool hasDotGit = Directory.Exists(Path.Combine(path, ".git"))
                    || File.Exists(Path.Combine(path, ".git"));
                bool looksBare =
                    !hasDotGit &&
                    File.Exists(Path.Combine(path, "HEAD")) &&
                    Directory.Exists(Path.Combine(path, "objects")) &&
                    Directory.Exists(Path.Combine(path, "refs"));

                if (hasDotGit || looksBare)
                {
                    var candidate = await BuildCandidateAsync(
                        rootPath, path, hasDotGit, ct,
                        existingPaths, existingOrigins,
                        emittedPaths, emittedOrigins, emittedSlugs).ConfigureAwait(false);

                    if (candidate != null)
                    {
                        candidates.Add(candidate);
                        reposFound++;
                        // Even on NotARepo we stop here — the shape probe matched.
                    }
                    // Do not descend into folders that are themselves a repo.
                    continue;
                }

                if (depth >= options.MaxDepth)
                {
                    continue;
                }

                IEnumerable<string> children;
                try
                {
                    children = Directory.EnumerateDirectories(path);
                }
                catch (UnauthorizedAccessException ex)
                {
                    issues.Add(new ScanIssue(rootPath, path, ScanIssueKind.AccessDenied, ex.Message));
                    continue;
                }
                catch (PathTooLongException ex)
                {
                    issues.Add(new ScanIssue(rootPath, path, ScanIssueKind.PathTooLong, ex.Message));
                    continue;
                }
                catch (IOException ex)
                {
                    issues.Add(new ScanIssue(rootPath, path, ScanIssueKind.IOError, ex.Message));
                    continue;
                }

                foreach (var child in children)
                {
                    ct.ThrowIfCancellationRequested();

                    string name;
                    try
                    {
                        name = Path.GetFileName(child) ?? string.Empty;
                    }
                    catch
                    {
                        continue;
                    }

                    if (AlwaysSkipNames.Contains(name))
                    {
                        continue;
                    }

                    if (atDriveRoot && SystemDriveRootSkipNames.Contains(name))
                    {
                        continue;
                    }

                    // Reparse-point (symlink / junction) guard.
                    if (!options.FollowSymlinks)
                    {
                        try
                        {
                            var attrs = File.GetAttributes(child);
                            if ((attrs & FileAttributes.ReparsePoint) == FileAttributes.ReparsePoint)
                            {
                                continue;
                            }
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            issues.Add(new ScanIssue(rootPath, child, ScanIssueKind.AccessDenied, ex.Message));
                            continue;
                        }
                        catch (PathTooLongException ex)
                        {
                            issues.Add(new ScanIssue(rootPath, child, ScanIssueKind.PathTooLong, ex.Message));
                            continue;
                        }
                        catch (IOException ex)
                        {
                            issues.Add(new ScanIssue(rootPath, child, ScanIssueKind.IOError, ex.Message));
                            continue;
                        }
                    }

                    queue.Enqueue((child, depth + 1, AtDriveRoot: false));
                }
            }

            walkedCounter(walked);
            progress?.Report(new ScanProgressUpdate(rootPath, walked, reposFound, string.Empty));
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch
            {
                return path ?? string.Empty;
            }
        }

        private async Task<ScanCandidate?> BuildCandidateAsync(
            string rootPath,
            string folderPath,
            bool hasDotGit,
            CancellationToken ct,
            Dictionary<string, string> existingPaths,
            Dictionary<string, string> existingOrigins,
            Dictionary<string, string> emittedPaths,
            Dictionary<string, string> emittedOrigins,
            HashSet<string> emittedSlugs)
        {
            GitWorkspaceClassification classification;
            try
            {
                classification = await _inspector.ClassifyAsync(folderPath, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Defensive — inspector promises never to throw on a missing
                // path, but if a transient IO fault sneaks through we'd
                // rather drop this row than abort the whole scan.
                return null;
            }

            if (classification is NotARepo)
            {
                return null;
            }

            string rawOrigin = string.Empty;
            string branch = string.Empty;
            RepoKind kind;

            if (classification is WorkingTreeRepo wt)
            {
                branch = wt.Branch ?? string.Empty;
                rawOrigin = wt.OriginUrl ?? string.Empty;

                if (wt.HasSubmodules)
                {
                    kind = RepoKind.Submodule;
                }
                else if (File.Exists(Path.Combine(folderPath, ".git")) && !Directory.Exists(Path.Combine(folderPath, ".git")))
                {
                    // .git is a file -> worktree pointer (linked worktree) or submodule entry.
                    kind = RepoKind.WorktreePointer;
                }
                else
                {
                    kind = RepoKind.WorkingTree;
                }

                // Submodule detection via parent's .gitmodules: a working tree
                // whose immediate parent declares this folder as a submodule
                // path is best classified as a Submodule, not WorkingTree.
                try
                {
                    var parent = Directory.GetParent(folderPath);
                    if (parent != null && File.Exists(Path.Combine(parent.FullName, ".gitmodules")))
                    {
                        kind = RepoKind.Submodule;
                    }
                }
                catch
                {
                    // best-effort
                }
            }
            else if (classification is BareRepo br)
            {
                kind = RepoKind.BareRepo;
                rawOrigin = string.Empty;
                if (br.Remotes != null)
                {
                    foreach (var r in br.Remotes)
                    {
                        if (string.Equals(r.Name, "origin", StringComparison.OrdinalIgnoreCase))
                        {
                            rawOrigin = r.FetchUrl ?? string.Empty;
                            break;
                        }
                    }
                }
            }
            else
            {
                kind = RepoKind.Other;
            }

            var displayOrigin = UrlSanitizer.StripCredentials(rawOrigin);
            var identity = _inspector.GetRemoteIdentity(displayOrigin);

            RemoteState remoteState;
            if (string.IsNullOrWhiteSpace(rawOrigin))
            {
                remoteState = RemoteState.NoRemote;
            }
            else if (UrlSanitizer.HasCredentials(rawOrigin))
            {
                remoteState = RemoteState.OriginHasCredentials;
            }
            else
            {
                remoteState = RemoteState.HasOrigin;
            }

            string normalisedFolder;
            try
            {
                normalisedFolder = Path.GetFullPath(folderPath);
            }
            catch
            {
                normalisedFolder = folderPath;
            }

            var folderName = Path.GetFileName(normalisedFolder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(folderName))
            {
                folderName = normalisedFolder;
            }

            // Dedupe — order matters. Path match wins over origin match so
            // moving an existing project under a scan root is recognised
            // as the same entry rather than a stranger that happens to
            // share an origin.
            DuplicateKind duplicateKind = DuplicateKind.None;
            string duplicateOfId = string.Empty;

            if (existingPaths.TryGetValue(normalisedFolder, out var portfolioPathHitId))
            {
                duplicateKind = DuplicateKind.Path;
                duplicateOfId = portfolioPathHitId;
            }
            else if (emittedPaths.TryGetValue(normalisedFolder, out var emittedPathHitId))
            {
                duplicateKind = DuplicateKind.Path;
                duplicateOfId = emittedPathHitId;
            }
            else if (!string.IsNullOrEmpty(identity) && existingOrigins.TryGetValue(identity, out var portfolioOriginHitId))
            {
                duplicateKind = DuplicateKind.Origin;
                duplicateOfId = portfolioOriginHitId;
            }
            else if (!string.IsNullOrEmpty(identity) && emittedOrigins.TryGetValue(identity, out var emittedOriginHitId))
            {
                duplicateKind = DuplicateKind.Origin;
                duplicateOfId = emittedOriginHitId;
            }

            var slug = BuildUniqueSlug(folderName, existingPaths, emittedSlugs);
            emittedSlugs.Add(slug);

            // Record this candidate in the in-scan dedupe tables so later
            // occurrences (overlapping roots, nested-but-not-skipped cases)
            // get marked correctly.
            if (!emittedPaths.ContainsKey(normalisedFolder))
            {
                emittedPaths[normalisedFolder] = slug;
            }
            if (!string.IsNullOrEmpty(identity) && !emittedOrigins.ContainsKey(identity))
            {
                emittedOrigins[identity] = slug;
            }

            var detail = BuildDetail(kind, remoteState, identity, duplicateKind, duplicateOfId);

            return new ScanCandidate(
                RootPath: rootPath,
                FolderPath: normalisedFolder,
                FolderName: folderName,
                SuggestedSlug: slug,
                DisplayOriginUrl: displayOrigin,
                RawOriginUrl: rawOrigin,
                DedupeIdentity: identity,
                Branch: branch,
                Kind: kind,
                RemoteState: remoteState,
                DuplicateKind: duplicateKind,
                DuplicateOfProjectId: duplicateOfId,
                Detail: detail);
        }

        private static string BuildDetail(
            RepoKind kind,
            RemoteState remoteState,
            string identity,
            DuplicateKind duplicateKind,
            string duplicateOfId)
        {
            if (duplicateKind == DuplicateKind.Path)
            {
                return "Duplicate path → " + (string.IsNullOrEmpty(duplicateOfId) ? "(in scan)" : duplicateOfId);
            }
            if (duplicateKind == DuplicateKind.Origin)
            {
                return "Duplicate origin → " + (string.IsNullOrEmpty(duplicateOfId) ? "(in scan)" : duplicateOfId);
            }

            var sb = new StringBuilder();
            switch (kind)
            {
                case RepoKind.WorkingTree: sb.Append("Working tree"); break;
                case RepoKind.BareRepo: sb.Append("Bare repo"); break;
                case RepoKind.WorktreePointer: sb.Append("Linked worktree"); break;
                case RepoKind.Submodule: sb.Append("Submodule"); break;
                default: sb.Append("Other"); break;
            }

            switch (remoteState)
            {
                case RemoteState.HasOrigin:
                    if (!string.IsNullOrEmpty(identity))
                    {
                        sb.Append(", origin ").Append(identity);
                    }
                    break;
                case RemoteState.NoRemote:
                    sb.Append(", no remote");
                    break;
                case RemoteState.OriginHasCredentials:
                    sb.Append(", origin URL has credentials — strip before registering");
                    break;
            }

            return sb.ToString();
        }

        private static string BuildUniqueSlug(
            string folderName,
            Dictionary<string, string> existingPaths,
            HashSet<string> emittedSlugs)
        {
            // We can't peek into the portfolio's id set directly from here
            // without re-loading; instead we de-duplicate against the
            // portfolio's project ids by reusing the path map's values
            // (every portfolio entry contributes its id there) plus the
            // in-scan emitted slug set.
            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in existingPaths.Values)
            {
                existingIds.Add(id);
            }
            foreach (var s in emittedSlugs)
            {
                existingIds.Add(s);
            }

            var baseSlug = Slugify(folderName);
            if (string.IsNullOrEmpty(baseSlug))
            {
                baseSlug = "project";
            }

            if (!existingIds.Contains(baseSlug))
            {
                return baseSlug;
            }

            for (int i = 1; i <= 50; i++)
            {
                var candidate = baseSlug + "-" + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (!existingIds.Contains(candidate))
                {
                    return candidate;
                }
            }

            return baseSlug + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        private static string Slugify(string source)
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                return string.Empty;
            }

            var sb = new StringBuilder(source.Length);
            foreach (var raw in source.Trim().ToLowerInvariant())
            {
                if ((raw >= 'a' && raw <= 'z') || (raw >= '0' && raw <= '9'))
                {
                    sb.Append(raw);
                }
                else if (sb.Length == 0 || sb[sb.Length - 1] != '-')
                {
                    sb.Append('-');
                }
            }

            return sb.ToString().Trim('-');
        }

        private static bool IsDriveRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            // Accept "C:\", "C:/", or "C:" forms.
            if (path.Length == 2 && char.IsLetter(path[0]) && path[1] == ':')
            {
                return true;
            }

            if (path.Length == 3 && char.IsLetter(path[0]) && path[1] == ':'
                && (path[2] == '\\' || path[2] == '/'))
            {
                return true;
            }

            return false;
        }
    }
}
