using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Infrastructure.Registration
{
    public sealed class RepoBootstrapService : IRepoBootstrapService
    {
        private readonly IPortfolioProvider _portfolioProvider;
        private readonly IProjectProvider _projectProvider;
        private readonly IStoreProvider _storeProvider;
        private readonly ISshService _sshService;
        private readonly ICredentialStore _credentialStore;
        private readonly IAsyncCloneService _cloneService;
        private readonly IProjectMetadataLocator _metadataLocator;

        public RepoBootstrapService(
            IPortfolioProvider portfolioProvider,
            IProjectProvider projectProvider,
            IStoreProvider storeProvider,
            ISshService sshService,
            ICredentialStore credentialStore)
            : this(portfolioProvider, projectProvider, storeProvider, sshService, credentialStore, cloneService: null)
        {
        }

        public RepoBootstrapService(
            IPortfolioProvider portfolioProvider,
            IProjectProvider projectProvider,
            IStoreProvider storeProvider,
            ISshService sshService,
            ICredentialStore credentialStore,
            IAsyncCloneService cloneService,
            IProjectMetadataLocator metadataLocator = null)
        {
            _portfolioProvider = portfolioProvider;
            _projectProvider = projectProvider;
            _storeProvider = storeProvider;
            _sshService = sshService;
            _credentialStore = credentialStore;
            _cloneService = cloneService ?? new AsyncCloneService(new GitProcessAdapter());
            _metadataLocator = metadataLocator;
        }

        private string ResolveMetadataRoot(ProjectRef projectRef)
        {
            if (_metadataLocator != null && projectRef != null && !string.IsNullOrWhiteSpace(projectRef.Id))
            {
                return _metadataLocator.ResolveMetadataRoot(projectRef.Id);
            }

            return projectRef?.Path ?? string.Empty;
        }

        public IReadOnlyList<MissingProject> DetectMissing()
        {
            var missing = new List<MissingProject>();
            var portfolio = _portfolioProvider.LoadPortfolio();

            foreach (var projectRef in portfolio.Projects)
            {
                if (string.IsNullOrWhiteSpace(projectRef.Path))
                {
                    continue;
                }

                bool isSsh = projectRef.UsesStore &&
                    _storeProvider.GetStore(projectRef.StoreId)?.IsSsh == true;

                // For SSH projects, we skip missing detection (can't cheaply stat remote)
                if (isSsh)
                {
                    continue;
                }

                // For local projects, check if path exists
                if (!Directory.Exists(projectRef.Path))
                {
                    var cloneUrl = ResolveCloneUrl(projectRef);
                    missing.Add(new MissingProject
                    {
                        ProjectId = projectRef.Id,
                        StoreId = projectRef.StoreId,
                        ExpectedPath = projectRef.Path,
                        CloneUrl = cloneUrl,
                        IsSsh = false
                    });
                }
            }

            return missing;
        }

        public BootstrapResult Clone(MissingProject project)
        {
            if (project == null)
            {
                return BootstrapResult.Fail("", "No project specified.");
            }

            if (!project.HasCloneUrl)
            {
                return BootstrapResult.Fail(project.ProjectId,
                    "No clone URL available. Add a GitHub or ADO remote URL to the project.");
            }

            if (project.IsSsh)
            {
                return CloneViaSsh(project);
            }
            else
            {
                return CloneLocally(project);
            }
        }

        private BootstrapResult CloneLocally(MissingProject project)
        {
            try
            {
                var request = new CloneRequest(
                    RemoteUrl: project.CloneUrl,
                    DestinationPath: project.ExpectedPath);

                // Synchronous shim over the async clone service so existing
                // callers (and Phase 0 tests) continue to work unchanged.
                // Phase A wiring uses the async path directly.
                var result = _cloneService.CloneAsync(request, progress: null, ct: CancellationToken.None)
                    .GetAwaiter().GetResult();

                if (result.Success)
                {
                    return BootstrapResult.Ok(project.ProjectId, $"Cloned from {project.CloneUrl}");
                }

                return BootstrapResult.Fail(project.ProjectId, "Clone failed: " + result.Message);
            }
            catch (Exception ex)
            {
                return BootstrapResult.Fail(project.ProjectId, $"Clone error: {ex.Message}");
            }
        }

        private BootstrapResult CloneViaSsh(MissingProject project)
        {
            var store = _storeProvider.GetStore(project.StoreId);
            if (store == null)
            {
                return BootstrapResult.Fail(project.ProjectId, $"Store '{project.StoreId}' not found.");
            }

            var password = !string.IsNullOrWhiteSpace(store.CredentialTarget)
                ? _credentialStore.GetPassword(store.CredentialTarget)
                : string.Empty;

            if (string.IsNullOrEmpty(password))
            {
                return BootstrapResult.Fail(project.ProjectId,
                    "No SSH credential available for remote clone.");
            }

            int port = store.Port > 0 ? store.Port : 22;
            var result = _sshService.RunCommand(store.Host, port, store.User, password,
                $"git clone \"{project.CloneUrl}\" \"{project.ExpectedPath}\"");

            return result.Success
                ? BootstrapResult.Ok(project.ProjectId, $"Cloned remotely from {project.CloneUrl}")
                : BootstrapResult.Fail(project.ProjectId, $"Remote clone failed: {result.Error}");
        }

        private string ResolveCloneUrl(ProjectRef projectRef)
        {
            if (string.IsNullOrWhiteSpace(projectRef.Path))
            {
                return string.Empty;
            }

            // Try to load project definition for external_refs
            try
            {
                var projectResult = _projectProvider.LoadProject(projectRef.Path, ResolveMetadataRoot(projectRef));
                var project = projectResult?.Project;
                if (project?.ExternalRefs != null)
                {
                    // Prefer GitHub repo URL
                    if (!string.IsNullOrWhiteSpace(project.ExternalRefs.GitHubRepo))
                    {
                        var repo = project.ExternalRefs.GitHubRepo.Trim();
                        if (repo.StartsWith("http", StringComparison.OrdinalIgnoreCase) ||
                            repo.StartsWith("git@", StringComparison.OrdinalIgnoreCase))
                        {
                            return repo;
                        }
                        // Assume org/repo format
                        return $"https://github.com/{repo}.git";
                    }
                }

                // Try launch.github URL
                if (project?.Launch != null && !string.IsNullOrWhiteSpace(project.Launch.GitHub))
                {
                    var ghUrl = project.Launch.GitHub.Trim();
                    if (!ghUrl.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
                    {
                        ghUrl += ".git";
                    }
                    return ghUrl;
                }
            }
            catch
            {
                // Project metadata may not exist if folder is missing
            }

            return string.Empty;
        }
    }
}
