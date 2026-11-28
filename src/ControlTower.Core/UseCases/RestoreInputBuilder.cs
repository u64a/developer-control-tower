#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Core.UseCases
{
    /// <summary>
    /// Builds the <see cref="ProjectRestoreInput"/> list for the Restore
    /// flow: one entry per portfolio project that has a local clone
    /// location, each carrying the best remote URL known anywhere.
    /// </summary>
    /// <remarks>
    /// The restore scenario is a rebuilt machine, so nothing inside the
    /// repo folders can be relied on. Every source consulted here survives
    /// the loss of the working trees: portfolio.yml, the central project
    /// metadata, and (when the local cache also survived) the last scan.
    /// </remarks>
    public sealed class RestoreInputBuilder
    {
        private readonly IPortfolioProvider _portfolioProvider;
        private readonly IProjectProvider _projectProvider;
        private readonly IStoreProvider _storeProvider;
        private readonly IProjectMetadataLocator? _metadataLocator;
        private readonly ISnapshotStore? _snapshotStore;
        private readonly WorkspaceProfile _activeProfile;

        public RestoreInputBuilder(
            IPortfolioProvider portfolioProvider,
            IProjectProvider projectProvider,
            IStoreProvider storeProvider,
            IProjectMetadataLocator? metadataLocator = null,
            ISnapshotStore? snapshotStore = null,
            WorkspaceProfile? activeProfile = null)
        {
            _portfolioProvider = portfolioProvider ?? throw new ArgumentNullException(nameof(portfolioProvider));
            _projectProvider = projectProvider ?? throw new ArgumentNullException(nameof(projectProvider));
            _storeProvider = storeProvider ?? throw new ArgumentNullException(nameof(storeProvider));
            _metadataLocator = metadataLocator;
            _snapshotStore = snapshotStore;
            _activeProfile = activeProfile ?? WorkspaceProfilePolicy.CreateAllProjectsProfile();
        }

        public IReadOnlyList<ProjectRestoreInput> Build()
        {
            var inputs = new List<ProjectRestoreInput>();
            var portfolio = _portfolioProvider.LoadPortfolio();
            if (portfolio?.Projects == null) return inputs;

            foreach (var projectRef in WorkspaceProfilePolicy.FilterProjects(portfolio.Projects, _activeProfile))
            {
                if (projectRef == null || string.IsNullOrWhiteSpace(projectRef.Id)) continue;

                var metadataRoot = _metadataLocator?.ResolveMetadataRoot(projectRef.Id);

                string expectedPath;
                if (!string.IsNullOrWhiteSpace(projectRef.StoreId))
                {
                    var store = _storeProvider.GetStore(projectRef.StoreId);
                    if (store == null || store.IsSsh) continue;
                    expectedPath = _storeProvider.ResolveProjectPath(
                        projectRef.StoreId, projectRef.Id, projectRef.Folder) ?? string.Empty;
                }
                else
                {
                    expectedPath = projectRef.Path ?? string.Empty;
                }

                if (!IsFullyQualifiedLocalPath(expectedPath)) continue;

                ProjectDefinition? project = null;
                try
                {
                    project = _projectProvider
                        .LoadProject(expectedPath, string.IsNullOrWhiteSpace(metadataRoot) ? expectedPath : metadataRoot)
                        ?.Project;
                }
                catch
                {
                    // Unreadable metadata must not hide the project; it just
                    // contributes no URL.
                }

                // A portfolio entry that points at the metadata stub itself
                // has no local clone (SSH-only projects are registered this
                // way). Restoring "into" it would quarantine the metadata.
                if (!string.IsNullOrWhiteSpace(metadataRoot) && SamePath(expectedPath, metadataRoot))
                {
                    var declaredLocal = project?.Locations?.LocalPath ?? string.Empty;
                    if (!IsFullyQualifiedLocalPath(declaredLocal) || SamePath(declaredLocal, metadataRoot))
                    {
                        continue;
                    }
                    expectedPath = declaredLocal;
                }

                RepoSnapshot? snapshot = null;
                try { snapshot = _snapshotStore?.Load(projectRef.Id); }
                catch { /* cache is opportunistic */ }

                var cached = (projectRef.RemoteUrl ?? string.Empty).Trim();
                var (remoteUrl, source) = PickRemoteUrl(
                    (cached, RestoreUrlSource.PortfolioCache),
                    (project?.Locations?.RemoteUrl, RestoreUrlSource.ProjectMetadata),
                    (snapshot?.OriginUrl, RestoreUrlSource.LastScan),
                    (project?.Launch?.GitHub, RestoreUrlSource.LaunchLink),
                    (project?.Launch?.Ado, RestoreUrlSource.LaunchLink));

                var displayName = project != null && !string.IsNullOrWhiteSpace(project.DisplayName)
                    ? project.DisplayName
                    : projectRef.Id;

                inputs.Add(new ProjectRestoreInput(
                    ProjectId: projectRef.Id,
                    ProjectName: displayName,
                    Slug: string.IsNullOrWhiteSpace(projectRef.Folder) ? projectRef.Id : projectRef.Folder,
                    ExpectedPath: expectedPath,
                    RemoteUrl: remoteUrl,
                    IsLocalStore: true,
                    RemoteUrlSource: source,
                    CachedRemoteUrl: cached));
            }

            return inputs;
        }

        /// <summary>
        /// Chooses which scanned origins should be written back to the
        /// portfolio.yml <c>remote_url</c> cache: any credential-free URL
        /// that differs from what is cached, except launch-link guesses
        /// (those are only cached once a clone from them has succeeded).
        /// Keyed by project id.
        /// </summary>
        public static IReadOnlyDictionary<string, string> SelectOriginsToCache(
            IReadOnlyList<ProjectRestoreInput> inputs,
            IReadOnlyList<RestoreCandidate> candidates)
        {
            var toCache = new Dictionary<string, string>(StringComparer.Ordinal);
            if (inputs == null || candidates == null) return toCache;

            var cachedById = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var input in inputs)
            {
                if (input != null) cachedById[input.ProjectId] = input.CachedRemoteUrl ?? string.Empty;
            }

            foreach (var candidate in candidates)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.RemoteUrl)) continue;
                if (candidate.RemoteUrlSource == RestoreUrlSource.LaunchLink) continue;
                if (UrlSanitizer.HasCredentials(candidate.RemoteUrl)) continue;

                cachedById.TryGetValue(candidate.ProjectId, out var cached);
                if (!string.Equals(cached ?? string.Empty, candidate.RemoteUrl, StringComparison.Ordinal))
                {
                    toCache[candidate.ProjectId] = candidate.RemoteUrl;
                }
            }

            return toCache;
        }

        /// <summary>
        /// Syntactic check that a URL is in a form <c>git clone</c> accepts:
        /// <c>https://</c>, <c>http://</c>, <c>ssh://</c>, <c>git://</c>,
        /// <c>file://</c>, or scp-like <c>user@host:owner/repo</c>. The
        /// clone itself remains the final validation.
        /// </summary>
        public static bool LooksLikeCloneUrl(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            var value = url.Trim();
            if (value.IndexOfAny(new[] { ' ', '\t', '\r', '\n' }) >= 0) return false;

            if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
            {
                var scheme = uri.Scheme;
                return scheme == "https" || scheme == "http" || scheme == "ssh"
                    || scheme == "git" || scheme == "file";
            }

            var at = value.IndexOf('@');
            if (at <= 0) return false;
            var colon = value.IndexOf(':', at);
            return colon > at + 1 && colon < value.Length - 1;
        }

        private static (string Url, RestoreUrlSource Source) PickRemoteUrl(
            params (string? Url, RestoreUrlSource Source)[] sources)
        {
            foreach (var (url, source) in sources)
            {
                if (string.IsNullOrWhiteSpace(url)) continue;
                var trimmed = url.Trim();
                if (UrlSanitizer.HasCredentials(trimmed)) continue;
                if (!LooksLikeCloneUrl(trimmed)) continue;
                return (trimmed, source);
            }

            return (string.Empty, RestoreUrlSource.None);
        }

        private static bool SamePath(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd('\\', '/'),
                    Path.GetFullPath(b).TrimEnd('\\', '/'),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // Drive-rooted, UNC, or absolute POSIX paths only. Rejects
        // drive-relative ("C:foo") and scp-style SSH ("user@host:path").
        private static bool IsFullyQualifiedLocalPath(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            var atIndex = value.IndexOf('@');
            var colonIndex = value.IndexOf(':');
            if (colonIndex > 0)
            {
                bool isDriveLetter = colonIndex == 1
                    && char.IsLetter(value[0])
                    && value.Length > 2 && (value[2] == '\\' || value[2] == '/');
                if (!isDriveLetter && (atIndex >= 0 || colonIndex > 1)) return false;
            }

            try
            {
                return Path.IsPathFullyQualified(value);
            }
            catch
            {
                return false;
            }
        }
    }
}
