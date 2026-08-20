using System;
using System.Collections.Generic;
using System.IO;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Core.UseCases
{
    public sealed class ControlTowerService
    {
        private readonly IPortfolioProvider _portfolioProvider;
        private readonly IProjectProvider _projectProvider;
        private readonly IProductMapProvider _productMapProvider;
        private readonly IPlanningBoardProvider _planningBoardProvider;
        private readonly IRepoScanner _repoScanner;
        private readonly ISnapshotStore _snapshotStore;
        private readonly ILaunchService _launchService;
        private readonly IProjectRegistrationService _projectRegistrationService;
        private readonly IRoadmapResolver _roadmapResolver;
        private readonly IProjectMetadataLocator _metadataLocator;
        private readonly IStoreProvider _storeProvider;
        private readonly WorkspaceProfile _activeProfile;

        public ControlTowerService(
            IPortfolioProvider portfolioProvider,
            IProjectProvider projectProvider,
            IProductMapProvider productMapProvider,
            IPlanningBoardProvider planningBoardProvider,
            IRepoScanner repoScanner,
            ISnapshotStore snapshotStore,
            ILaunchService launchService,
            IProjectRegistrationService projectRegistrationService,
            IRoadmapResolver roadmapResolver = null,
            IProjectMetadataLocator metadataLocator = null,
            IStoreProvider storeProvider = null,
            WorkspaceProfile activeProfile = null)
        {
            _portfolioProvider = portfolioProvider;
            _projectProvider = projectProvider;
            _productMapProvider = productMapProvider;
            _planningBoardProvider = planningBoardProvider;
            _repoScanner = repoScanner;
            _snapshotStore = snapshotStore;
            _launchService = launchService;
            _projectRegistrationService = projectRegistrationService;
            _roadmapResolver = roadmapResolver;
            _metadataLocator = metadataLocator;
            _storeProvider = storeProvider;
            _activeProfile = activeProfile ?? WorkspaceProfilePolicy.CreateAllProjectsProfile();
        }

        /// <summary>
        /// Resolves where a project's <c>.controltower</c> metadata lives. When a
        /// metadata locator is wired (production) this is the central
        /// <c>portfolio-projects\{id}</c> stub; otherwise it falls back to the
        /// portfolio entry path so legacy/test setups (metadata co-located with
        /// the working tree) keep working unchanged.
        /// </summary>
        private string ResolveMetadataRoot(ProjectRef projectRef)
        {
            if (_metadataLocator != null && projectRef != null && !string.IsNullOrWhiteSpace(projectRef.Id))
            {
                return _metadataLocator.ResolveMetadataRoot(projectRef.Id);
            }

            return projectRef?.Path;
        }

        public IReadOnlyList<ProjectOverview> LoadPortfolio()
        {
            var results = new List<ProjectOverview>();
            var portfolio = _portfolioProvider.LoadPortfolio();

            // Profile projection is applied to canonical ProjectRef identities
            // before any project metadata, composition, cache, or repo work.
            // The canonical portfolio object remains complete and unmodified for
            // writers that own portfolio.yml.
            var projectedProjects = WorkspaceProfilePolicy.FilterProjects(
                portfolio.Projects,
                _activeProfile);
            foreach (var projectRef in projectedProjects)
            {
                results.Add(LoadProject(projectRef, false));
            }

            // Dedup by canonical stable id: collapse only overviews that carry a REAL,
            // stable id (i.e. not null/empty and not a sentinel prefix like "missing." or
            // "invalid."). Sentinel/unstable ids are NEVER collapsed — each unconfigured
            // project folder must always appear as its own distinct row, regardless of how
            // many share the same shared-sentinel literal. This guards against data loss
            // when many projects have no project.yml (all would compose to "missing.project"
            // and, if collapsed, all but the first would vanish from the portfolio list).
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<ProjectOverview>(results.Count);
            foreach (var overview in results)
            {
                if (ProjectIdentity.IsUnstable(overview.Id) || seen.Add(overview.Id))
                {
                    deduped.Add(overview);
                }
            }

            return deduped;
        }

        public ProjectOverview LoadProject(ProjectRef projectRef, bool includeRepoScan)
        {
            return LoadProject(projectRef, includeRepoScan ? ScanPolicy.FullExplicit : ScanPolicy.CacheOnly);
        }

        public ProjectOverview LoadProject(ProjectRef projectRef, ScanPolicy policy)
        {
            var issues = new List<ValidationIssue>();
            var metadataRoot = ResolveMetadataRoot(projectRef);
            var projectResult = _projectProvider.LoadProject(projectRef.Path, metadataRoot);
            ProjectDefinition project = projectResult.Project;

            AddIssues(issues, projectResult.Issues);

            string sourceRef = project.Planning == null ? string.Empty : project.Planning.SourceRef;
            var productResult = _productMapProvider.LoadProductMap(metadataRoot, sourceRef);
            AddIssues(issues, productResult.Issues);

            // Roadmap resolution can hit the network for SSH-hosted projects.
            // Skip it on the cheap initial portfolio scan; only resolve when
            // the caller explicitly asks for a full repo scan (Refresh).
            PlanningBoardLoadResult planningBoardResult;
            if (policy == ScanPolicy.FullExplicit)
            {
                var resolved = _roadmapResolver?.Resolve(project);
                if (resolved != null && !string.IsNullOrWhiteSpace(resolved.Yaml))
                {
                    planningBoardResult = _planningBoardProvider.ParseFromContent(resolved.Yaml, resolved.SourceLabel);
                }
                else
                {
                    planningBoardResult = _planningBoardProvider.LoadPlanningBoard(projectRef.Path);
                }
            }
            else
            {
                // Cheap path — only the local file lookup, no SSH probe.
                planningBoardResult = _planningBoardProvider.LoadPlanningBoard(projectRef.Path);
            }
            AddIssues(issues, planningBoardResult.Issues);

            RepoSnapshot snapshot = ResolveSnapshot(project, projectRef, policy);

            var overview = ProjectContextComposer.Compose(project, productResult.Summary, planningBoardResult.Summary, snapshot, issues);

            // Stamp portfolio-entry identity so callers (e.g. the edit dialog)
            // can round-trip the store/folder back without re-reading the portfolio.
            overview.StoreId = projectRef.StoreId ?? string.Empty;

            // Compute effective folder: SavePortfolio intentionally omits 'folder'
            // when folder == id (convention: project ID is the implicit folder name).
            // A blank or whitespace-only Folder on a store-backed entry means "same as id"
            // — aligned with StoreProvider.ResolveProjectPath which uses IsNullOrWhiteSpace.
            overview.Folder = (projectRef.UsesStore && string.IsNullOrWhiteSpace(projectRef.Folder))
                ? projectRef.Id
                : (projectRef.Folder ?? string.Empty);

            // For remote-only path-based SSH projects, derive StoreId/Folder from
            // the project's SSH target matched against configured stores (ADR-010
            // Option B). Remote-only means the project has an SSH target but no real
            // local path — if LocalPath is non-blank the project is hybrid and
            // derivation is skipped because AddProjectWindow is single-store; saving
            // a derived SSH store would drop the local side.
            if (!projectRef.UsesStore
                && project.Locations != null
                && !string.IsNullOrWhiteSpace(project.Locations.SshTarget)
                && string.IsNullOrWhiteSpace(project.Locations.LocalPath)
                && string.IsNullOrWhiteSpace(overview.StoreId)
                && _storeProvider != null)
            {
                var stores = _storeProvider.GetStores();
                if (SshStoreResolver.TryResolve(project.Locations.SshTarget, stores, out var derivedStoreId, out var derivedFolder))
                {
                    overview.StoreId = derivedStoreId;
                    overview.Folder = derivedFolder;
                    overview.IsStoreIdentityDerived = true;
                }
            }

            return overview;
        }

        private RepoSnapshot ResolveSnapshot(ProjectDefinition project, ProjectRef projectRef, ScanPolicy policy)
        {
            if (policy == ScanPolicy.CacheOnly)
            {
                return string.IsNullOrWhiteSpace(project.Id) ? null : _snapshotStore.Load(project.Id);
            }

            if (policy == ScanPolicy.LocalOnly)
            {
                // Never probe SSH on the automatic seed: scan a local clone only
                // when it actually exists, else fall back to cached state.
                var local = project.Locations == null ? null : project.Locations.LocalPath;
                if (!string.IsNullOrWhiteSpace(local) && Directory.Exists(local))
                {
                    var localSnap = _repoScanner.Scan(local);
                    if (localSnap != null)
                    {
                        localSnap.ScannedAtUtc = DateTime.UtcNow;
                        if (!string.IsNullOrWhiteSpace(project.Id))
                        {
                            _snapshotStore.Save(project.Id, localSnap);
                        }
                    }
                    return localSnap;
                }

                return string.IsNullOrWhiteSpace(project.Id) ? null : _snapshotStore.Load(project.Id);
            }

            // FullExplicit — user-invoked; may probe SSH-hosted repositories.
            var repoPath = projectRef.Path;
            if (project.Locations != null &&
                !string.IsNullOrWhiteSpace(project.Locations.SshTarget))
            {
                repoPath = project.Locations.SshTarget;
            }
            else if (project.Locations != null &&
                     !string.IsNullOrWhiteSpace(project.Locations.LocalPath) &&
                     Directory.Exists(project.Locations.LocalPath))
            {
                repoPath = project.Locations.LocalPath;
            }

            var snapshot = _repoScanner.Scan(repoPath);
            if (snapshot != null)
            {
                snapshot.ScannedAtUtc = DateTime.UtcNow;
                if (!string.IsNullOrWhiteSpace(project.Id))
                {
                    _snapshotStore.Save(project.Id, snapshot);
                }
            }
            return snapshot;
        }

        public ProjectDefinition GetProjectDefinition(ProjectRef projectRef)
        {
            if (projectRef == null)
            {
                return null;
            }

            return _projectProvider.LoadProject(projectRef.Path, ResolveMetadataRoot(projectRef)).Project;
        }

        public LaunchResult Launch(ProjectRef projectRef, LaunchTargetKind targetKind)
        {
            var projectResult = _projectProvider.LoadProject(projectRef.Path, ResolveMetadataRoot(projectRef));
            var project = projectResult.Project;

            // For GitHub/Ado launches, fall back to the cached repo origin URL
            // when project.yml has no explicit launch.github / launch.ado set.
            if ((targetKind == LaunchTargetKind.GitHub || targetKind == LaunchTargetKind.Ado) &&
                project?.Launch != null && !string.IsNullOrWhiteSpace(project.Id))
            {
                var snapshot = _snapshotStore.Load(project.Id);
                if (string.IsNullOrWhiteSpace(project.Launch.GitHub))
                {
                    project.Launch.GitHub = Composition.OriginUrlResolver.ResolveGitHubUrl(project, snapshot);
                }
                if (string.IsNullOrWhiteSpace(project.Launch.Ado))
                {
                    project.Launch.Ado = Composition.OriginUrlResolver.ResolveAdoUrl(project, snapshot);
                }
            }

            return _launchService.Launch(project, targetKind);
        }

        public ProjectRegistrationResult RegisterProject(ProjectRegistrationRequest request)
        {
            return _projectRegistrationService.RegisterProject(request);
        }

        public ProjectRegistrationResult RemoveProject(ProjectRef projectRef)
        {
            if (projectRef == null || string.IsNullOrWhiteSpace(projectRef.Id))
            {
                return new ProjectRegistrationResult
                {
                    Success = false,
                    Message = "No project selected"
                };
            }

            return _projectRegistrationService.RemoveProject(projectRef.Id);
        }

        private static void AddIssues(ICollection<ValidationIssue> target, IEnumerable<ValidationIssue> source)
        {
            if (source == null)
            {
                return;
            }

            foreach (var item in source)
            {
                target.Add(item);
            }
        }
    }
}
