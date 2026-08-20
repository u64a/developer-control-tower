using System;
using System.Collections.Generic;
using System.IO;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

public class ControlTowerServiceTests
{
    // ---- Fakes ------------------------------------------------------------

    private sealed class FakePortfolioProvider : IPortfolioProvider
    {
        public PortfolioIndex Index { get; set; } = new PortfolioIndex();
        public int LoadCount;
        public PortfolioIndex LoadPortfolio() { LoadCount++; return Index; }
        public void SavePortfolio(PortfolioIndex portfolio) { Index = portfolio; }
    }

    private sealed class FakeProjectProvider : IProjectProvider
    {
        public Dictionary<string, ProjectLoadResult> Map { get; } = new();
        public List<string> LoadedPaths { get; } = new();
        public bool ThrowOnLoad;
        public int LoadCount;
        public ProjectLoadResult LoadProject(string projectRootPath)
        {
            LoadCount++;
            LoadedPaths.Add(projectRootPath);
            if (ThrowOnLoad) throw new InvalidOperationException("provider boom");
            if (Map.TryGetValue(projectRootPath, out var r)) return r;
            var fallback = new ProjectLoadResult();
            fallback.Project.Id = "auto-" + projectRootPath;
            fallback.Project.ProjectRootPath = projectRootPath;
            return fallback;
        }
    }

    private sealed class FakeProductMapProvider : IProductMapProvider
    {
        public ProductMapLoadResult Result { get; set; } = new();
        public ProductMapLoadResult LoadProductMap(string projectRootPath, string sourceRef) => Result;
    }

    private sealed class FakePlanningBoardProvider : IPlanningBoardProvider
    {
        public PlanningBoardLoadResult LoadResult { get; set; } = new();
        public PlanningBoardLoadResult ParseResult { get; set; } = new();
        public int LoadCallCount;
        public int ParseCallCount;
        public PlanningBoardLoadResult LoadPlanningBoard(string projectRootPath)
        {
            LoadCallCount++;
            return LoadResult;
        }
        public PlanningBoardLoadResult ParseFromContent(string yamlContent, string sourceLabel)
        {
            ParseCallCount++;
            return ParseResult;
        }
    }

    private sealed class FakeRepoScanner : IRepoScanner
    {
        public RepoSnapshot Snapshot { get; set; } = new() { IsAvailable = true, Branch = "main", RepoPath = "X" };
        public string? LastPath;
        public int ScanCount;
        public RepoSnapshot Scan(string repoPath) { ScanCount++; LastPath = repoPath; return Snapshot; }
    }

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public Dictionary<string, RepoSnapshot> Saved { get; } = new();
        public int SaveCount;
        public int LoadCount;
        public RepoSnapshot? Load(string projectId) { LoadCount++; return Saved.TryGetValue(projectId, out var s) ? s : null; }
        public void Save(string projectId, RepoSnapshot snapshot) { SaveCount++; Saved[projectId] = snapshot; }
    }

    private sealed class FakeLaunchService : ILaunchService
    {
        public ProjectDefinition? LastProject;
        public LaunchTargetKind LastKind;
        public LaunchResult Result { get; set; } = new LaunchResult { Status = LaunchStatus.Ok, Success = true };
        public bool Throw;
        public LaunchResult Launch(ProjectDefinition project, LaunchTargetKind targetKind)
        {
            LastProject = project; LastKind = targetKind;
            if (Throw) throw new InvalidOperationException("launch boom");
            return Result;
        }
    }

    private sealed class FakeRegistrationService : IProjectRegistrationService
    {
        public ProjectRegistrationRequest? LastRegister;
        public string? LastRemoveId;
        public ProjectRegistrationResult RegisterResult { get; set; } = new ProjectRegistrationResult { Success = true, ProjectId = "newp" };
        public ProjectRegistrationResult RemoveResult { get; set; } = new ProjectRegistrationResult { Success = true };
        public ProjectRegistrationResult RegisterProject(ProjectRegistrationRequest request) { LastRegister = request; return RegisterResult; }
        public ProjectRegistrationResult RemoveProject(string projectId) { LastRemoveId = projectId; return RemoveResult; }
    }

    private sealed class FakeRoadmapResolver : IRoadmapResolver
    {
        public RoadmapContent? Content { get; set; }
        public int Calls;
        public RoadmapContent Resolve(ProjectDefinition project) { Calls++; return Content!; }
    }

    private sealed class FakeStoreProvider : IStoreProvider
    {
        private readonly List<RepoStore> _stores;
        public FakeStoreProvider(params RepoStore[] stores) => _stores = stores.ToList();
        public IReadOnlyList<RepoStore> GetStores() => _stores;
        public RepoStore? GetStore(string storeId) =>
            _stores.FirstOrDefault(s => string.Equals(s.Id, storeId, StringComparison.OrdinalIgnoreCase));
        public string ResolveProjectPath(string storeId, string projectId, string folder) => string.Empty;
    }

    // ---- Helpers ----------------------------------------------------------

    private static ProjectDefinition NewProject(string id, string root)
    {
        var p = new ProjectDefinition { Id = id, DisplayName = id, ProjectRootPath = root };
        p.Locations.LocalPath = root;
        return p;
    }

    private (ControlTowerService svc,
             FakePortfolioProvider portfolio,
             FakeProjectProvider projects,
             FakeProductMapProvider productMap,
             FakePlanningBoardProvider board,
             FakeRepoScanner scanner,
             FakeSnapshotStore store,
             FakeLaunchService launch,
             FakeRegistrationService reg,
             FakeRoadmapResolver roadmap) Build(WorkspaceProfile? activeProfile = null)
    {
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var productMap = new FakeProductMapProvider();
        var board = new FakePlanningBoardProvider();
        var scanner = new FakeRepoScanner();
        var store = new FakeSnapshotStore();
        var launch = new FakeLaunchService();
        var reg = new FakeRegistrationService();
        var roadmap = new FakeRoadmapResolver();
        var svc = new ControlTowerService(
            portfolio,
            projects,
            productMap,
            board,
            scanner,
            store,
            launch,
            reg,
            roadmap,
            metadataLocator: null,
            storeProvider: null,
            activeProfile: activeProfile);
        return (svc, portfolio, projects, productMap, board, scanner, store, launch, reg, roadmap);
    }

    // ---- LoadPortfolio ----------------------------------------------------

    [Fact]
    public void LoadPortfolio_Happy_ReturnsOneOverviewPerProject()
    {
        var t = Build();
        t.portfolio.Index.Projects.Add(new ProjectRef { Id = "a", Path = @"C:\a" });
        t.portfolio.Index.Projects.Add(new ProjectRef { Id = "b", Path = @"C:\b" });
        t.projects.Map[@"C:\a"] = new ProjectLoadResult { Project = NewProject("a", @"C:\a") };
        t.projects.Map[@"C:\b"] = new ProjectLoadResult { Project = NewProject("b", @"C:\b") };

        var list = t.svc.LoadPortfolio();
        Assert.Equal(2, list.Count);
        Assert.Equal("a", list[0].Id);
        Assert.Equal("b", list[1].Id);
        // Portfolio load is the cheap path: scanner must not be called.
        Assert.Equal(0, t.scanner.ScanCount);
    }

    [Fact]
    public void LoadPortfolio_EmptyPortfolio_ReturnsEmpty()
    {
        var t = Build();
        var list = t.svc.LoadPortfolio();
        Assert.Empty(list);
    }

    [Fact]
    public void LoadPortfolio_ProfileFiltersBeforeProjectLoadAndLeavesCanonicalRefsUnchanged()
    {
        var profile = new WorkspaceProfile
        {
            Id = Guid.NewGuid(),
            Name = "Focused"
        };
        profile.Members.Add("INCLUDED");
        var t = Build(profile);
        var included = new ProjectRef
        {
            Id = "included",
            Path = @"C:\included",
            StoreId = "local",
            Folder = "included-folder"
        };
        var excluded = new ProjectRef
        {
            Id = "excluded",
            Path = @"C:\excluded",
            StoreId = "ssh",
            Folder = "excluded-folder"
        };
        t.portfolio.Index.Projects.Add(included);
        t.portfolio.Index.Projects.Add(excluded);
        t.projects.Map[included.Path] = new ProjectLoadResult
        {
            Project = NewProject(included.Id, included.Path)
        };

        var list = t.svc.LoadPortfolio();

        Assert.Equal("included", Assert.Single(list).Id);
        Assert.Equal(new[] { included.Path }, t.projects.LoadedPaths);
        Assert.Equal(0, t.scanner.ScanCount);
        Assert.Equal(2, t.portfolio.Index.Projects.Count);
        Assert.Same(included, t.portfolio.Index.Projects[0]);
        Assert.Same(excluded, t.portfolio.Index.Projects[1]);
        Assert.Equal("ssh", excluded.StoreId);
        Assert.Equal("excluded-folder", excluded.Folder);
        Assert.Equal(@"C:\excluded", excluded.Path);
    }

    // ---- LoadProject ------------------------------------------------------

    [Fact]
    public void LoadProject_WithRepoScan_ScansAndSavesSnapshot()
    {
        var t = Build();
        var pr = new ProjectRef { Id = "x", Path = @"C:\x" };
        t.projects.Map[@"C:\x"] = new ProjectLoadResult { Project = NewProject("x", @"C:\x") };

        var ov = t.svc.LoadProject(pr, includeRepoScan: true);

        Assert.Equal("x", ov.Id);
        Assert.Equal(1, t.scanner.ScanCount);
        Assert.True(t.store.Saved.ContainsKey("x"));
    }

    [Fact]
    public void LoadProject_WithoutRepoScan_UsesCachedSnapshot()
    {
        var t = Build();
        var pr = new ProjectRef { Id = "y", Path = @"C:\y" };
        t.projects.Map[@"C:\y"] = new ProjectLoadResult { Project = NewProject("y", @"C:\y") };
        t.store.Saved["y"] = new RepoSnapshot { IsAvailable = true, Branch = "feat", HasUpstream = true };

        var ov = t.svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal(0, t.scanner.ScanCount);
        Assert.Equal(1, t.store.LoadCount);
        Assert.Equal("feat", ov.Branch);
    }

    [Fact]
    public void LoadProject_WithRoadmapResolver_UsesParseFromContent()
    {
        var t = Build();
        var pr = new ProjectRef { Id = "z", Path = @"C:\z" };
        t.projects.Map[@"C:\z"] = new ProjectLoadResult { Project = NewProject("z", @"C:\z") };
        t.roadmap.Content = new RoadmapContent { Yaml = "yaml: here", SourceLabel = "ssh" };

        t.svc.LoadProject(pr, includeRepoScan: true);

        Assert.Equal(1, t.roadmap.Calls);
        Assert.Equal(1, t.board.ParseCallCount);
        Assert.Equal(0, t.board.LoadCallCount);
    }

    [Fact]
    public void LoadProject_AdoAuthority_SuppressesPlanningSummary()
    {
        // Failure-mode/edge: authority/mismatch path through ProjectContextComposer.
        var t = Build();
        var pr = new ProjectRef { Id = "ado1", Path = @"C:\ado1" };
        var proj = NewProject("ado1", @"C:\ado1");
        proj.Planning.Authority = "ado";
        t.projects.Map[@"C:\ado1"] = new ProjectLoadResult { Project = proj };
        t.productMap.Result.Summary.ProductTitle = "X";
        t.productMap.Result.Summary.PlanningAuthority = "ado";
        t.productMap.Result.Summary.TopLevelInitiatives.Add("Should-Be-Hidden");

        var ov = t.svc.LoadProject(pr, includeRepoScan: false);
        Assert.Equal("None", ov.PlanningSource);
        Assert.Equal("No product map", ov.ProductTitle);
    }

    [Fact]
    public void LoadProject_ProviderThrows_BubblesUpForVisibility()
    {
        // Failure mode: an exception is allowed to surface — the orchestrator
        // does not silently swallow IO failures.
        var t = Build();
        t.projects.ThrowOnLoad = true;
        var pr = new ProjectRef { Id = "boom", Path = @"C:\boom" };
        Assert.Throws<InvalidOperationException>(() => t.svc.LoadProject(pr, includeRepoScan: false));
    }

    // ---- Edit-form regression: StoreId / Folder stamped on overview -------

    [Fact]
    public void LoadProject_SshStoreRef_StampsStoreIdAndFolderOnOverview()
    {
        // Regression: EditProjectClick builds its ProjectCreationRequest from
        // ProjectOverview. Before the fix, StoreId and Folder were never
        // propagated from the ProjectRef to the overview, so the edit dialog
        // always fell back to store index 0 (Local) and an empty folder.
        var t = Build();
        var pr = new ProjectRef
        {
            Id = "ssh-proj",
            Path = @"C:\stores\devbox\ssh-proj",
            StoreId = "devbox",
            Folder = "ssh-proj"
        };
        t.projects.Map[@"C:\stores\devbox\ssh-proj"] = new ProjectLoadResult
        {
            Project = NewProject("ssh-proj", @"C:\stores\devbox\ssh-proj")
        };

        var ov = t.svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal("devbox", ov.StoreId);
        Assert.Equal("ssh-proj", ov.Folder);
    }

    [Fact]
    public void LoadProject_StoreRefWithOverrideFolder_StampsOverrideFolderOnOverview()
    {
        var t = Build();
        var pr = new ProjectRef
        {
            Id = "my-project",
            Path = @"C:\stores\local\custom-folder",
            StoreId = "local",
            Folder = "custom-folder"
        };
        t.projects.Map[@"C:\stores\local\custom-folder"] = new ProjectLoadResult
        {
            Project = NewProject("my-project", @"C:\stores\local\custom-folder")
        };

        var ov = t.svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal("local", ov.StoreId);
        Assert.Equal("custom-folder", ov.Folder);
    }

    [Fact]
    public void LoadProject_LegacyEntryWithNoStore_StoreIdAndFolderAreEmpty()
    {
        // Legacy v0 entries use explicit path, no store reference.
        var t = Build();
        var pr = new ProjectRef { Id = "legacy", Path = @"C:\legacy", StoreId = "", Folder = "" };
        t.projects.Map[@"C:\legacy"] = new ProjectLoadResult
        {
            Project = NewProject("legacy", @"C:\legacy")
        };

        var ov = t.svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal(string.Empty, ov.StoreId);
        Assert.Equal(string.Empty, ov.Folder);
    }

    [Fact]
    public void LoadProject_StoreBackedBlankFolder_EffectiveFolderIsProjectId()
    {
        // Regression: PortfolioYamlProvider.SavePortfolio intentionally omits
        // 'folder' from YAML when folder == id (convention). On reload,
        // ProjectRef.Folder is "". The edit dialog must show the effective
        // folder (= project ID), not a blank string.
        var t = Build();
        var pr = new ProjectRef
        {
            Id = "my-ssh-proj",
            Path = @"C:\stores\devbox\my-ssh-proj",
            StoreId = "devbox",
            Folder = ""   // blank — simulates YAML entry with no explicit folder field
        };
        t.projects.Map[@"C:\stores\devbox\my-ssh-proj"] = new ProjectLoadResult
        {
            Project = NewProject("my-ssh-proj", @"C:\stores\devbox\my-ssh-proj")
        };

        var ov = t.svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal("devbox", ov.StoreId);
        Assert.Equal("my-ssh-proj", ov.Folder); // effective folder = project ID
    }

    [Fact]
    public void LoadProject_StoreBackedWhitespaceOnlyFolder_EffectiveFolderIsProjectId()
    {
        // StoreProvider.ResolveProjectPath uses IsNullOrWhiteSpace to detect an
        // implicit folder. The stamping logic must use the same predicate so that
        // a whitespace-only Folder value (e.g. from a malformed YAML entry) is
        // treated identically to a blank string.
        var t = Build();
        var pr = new ProjectRef
        {
            Id = "ws-proj",
            Path = @"C:\stores\devbox\ws-proj",
            StoreId = "devbox",
            Folder = "   "   // whitespace-only — canonically equivalent to blank
        };
        t.projects.Map[@"C:\stores\devbox\ws-proj"] = new ProjectLoadResult
        {
            Project = NewProject("ws-proj", @"C:\stores\devbox\ws-proj")
        };

        var ov = t.svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal("devbox", ov.StoreId);
        Assert.Equal("ws-proj", ov.Folder); // effective folder = project ID
    }

    [Fact]
    public void LoadPortfolio_SshEntry_OverviewCarriesStoreIdAndFolder()
    {
        // End-to-end: portfolio with an SSH store entry → all overviews carry
        // the store identity, so the edit dialog can pre-select the right store.
        var t = Build();
        var pr = new ProjectRef
        {
            Id = "ssh-p",
            Path = @"C:\devbox\ssh-p",
            StoreId = "devbox",
            Folder = "ssh-p"
        };
        t.portfolio.Index.Projects.Add(pr);
        t.projects.Map[@"C:\devbox\ssh-p"] = new ProjectLoadResult
        {
            Project = NewProject("ssh-p", @"C:\devbox\ssh-p")
        };

        var list = t.svc.LoadPortfolio();

        Assert.Single(list);
        Assert.Equal("devbox", list[0].StoreId);
        Assert.Equal("ssh-p", list[0].Folder);
    }

    // ---- Launch -----------------------------------------------------------

    [Fact]
    public void Launch_VsCode_DelegatesToLaunchService()
    {
        var t = Build();
        var pr = new ProjectRef { Id = "L", Path = @"C:\L" };
        t.projects.Map[@"C:\L"] = new ProjectLoadResult { Project = NewProject("L", @"C:\L") };

        var result = t.svc.Launch(pr, LaunchTargetKind.Code);
        Assert.Equal(LaunchStatus.Ok, result.Status);
        Assert.Equal(LaunchTargetKind.Code, t.launch.LastKind);
    }

    [Fact]
    public void Launch_GitHub_FallsBackToCachedOrigin()
    {
        var t = Build();
        var pr = new ProjectRef { Id = "G", Path = @"C:\G" };
        var proj = NewProject("G", @"C:\G");
        proj.Launch.GitHub = ""; // empty -> use cached origin
        t.projects.Map[@"C:\G"] = new ProjectLoadResult { Project = proj };
        t.store.Saved["G"] = new RepoSnapshot { OriginUrl = "https://github.com/me/repo.git", IsAvailable = true };

        t.svc.Launch(pr, LaunchTargetKind.GitHub);
        Assert.NotNull(t.launch.LastProject);
        Assert.False(string.IsNullOrEmpty(t.launch.LastProject.Launch.GitHub));
    }

    [Fact]
    public void Launch_ServiceThrows_PropagatesForUiVisibility()
    {
        var t = Build();
        var pr = new ProjectRef { Id = "L2", Path = @"C:\L2" };
        t.projects.Map[@"C:\L2"] = new ProjectLoadResult { Project = NewProject("L2", @"C:\L2") };
        t.launch.Throw = true;
        Assert.Throws<InvalidOperationException>(() => t.svc.Launch(pr, LaunchTargetKind.Code));
    }

    // ---- RegisterProject --------------------------------------------------

    [Fact]
    public void RegisterProject_DelegatesToRegistrationService()
    {
        var t = Build();
        var req = new ProjectRegistrationRequest { ProjectId = "p", SourcePath = @"C:\p" };
        var result = t.svc.RegisterProject(req);
        Assert.True(result.Success);
        Assert.Same(req, t.reg.LastRegister);
    }

    [Fact]
    public void RegisterProject_ServiceReportsFailure_IsSurfaced()
    {
        var t = Build();
        t.reg.RegisterResult = new ProjectRegistrationResult { Success = false, Message = "duplicate id" };
        var result = t.svc.RegisterProject(new ProjectRegistrationRequest { ProjectId = "p" });
        Assert.False(result.Success);
        Assert.Equal("duplicate id", result.Message);
    }

    // ---- RemoveProject ----------------------------------------------------

    [Fact]
    public void RemoveProject_DelegatesById()
    {
        var t = Build();
        var pr = new ProjectRef { Id = "rm", Path = @"C:\rm" };
        var result = t.svc.RemoveProject(pr);
        Assert.True(result.Success);
        Assert.Equal("rm", t.reg.LastRemoveId);
    }

    [Fact]
    public void RemoveProject_NullRef_ReturnsFailureWithoutCallingService()
    {
        var t = Build();
        var result = t.svc.RemoveProject(null);
        Assert.False(result.Success);
        Assert.Null(t.reg.LastRemoveId);
    }

    [Fact]
    public void RemoveProject_RefWithoutId_ReturnsFailureWithoutCallingService()
    {
        var t = Build();
        var result = t.svc.RemoveProject(new ProjectRef { Id = "", Path = @"C:\x" });
        Assert.False(result.Success);
        Assert.Null(t.reg.LastRemoveId);
    }

    // ---- ADR-010 regression: derive StoreId/Folder for path-based SSH entries -----
    //
    // COVERAGE GAP: the existing StoreId/Folder tests above all use store-backed
    // ProjectRef entries (UsesStore == true, StoreId set by portfolio). The tests
    // below prove derivation from project.Locations.SshTarget for path-based entries
    // (UsesStore == false), and lock the hybrid-skip and no-override invariants.
    // POSIX/Windows case-sensitivity is covered exhaustively in SshStoreResolverTests.

    private ControlTowerService BuildSvcWithStore(
        FakePortfolioProvider portfolio,
        FakeProjectProvider projects,
        IStoreProvider? storeProvider = null) =>
        new(portfolio, projects,
            new FakeProductMapProvider(),
            new FakePlanningBoardProvider(),
            new FakeRepoScanner(),
            new FakeSnapshotStore(),
            new FakeLaunchService(),
            new FakeRegistrationService(),
            storeProvider: storeProvider);

    [Fact]
    public void LoadProject_PathBasedSshEntry_DerivesStoreIdAndFolderFromSshTarget()
    {
        // Remote-only SSH project: SshTarget is set, LocalPath is blank.
        // Before ADR-010 hybrid-safety fix, NewProject set LocalPath = root,
        // making this fixture accidentally hybrid. Clearing LocalPath ensures
        // the test exercises the remote-only derivation path.
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"d:\repos", User = "devuser" });

        // Path-based portfolio entry — UsesStore == false, StoreId == "".
        var pr = new ProjectRef { Id = "myproject", Path = @"d:\repos\myproject", StoreId = "", Folder = "" };

        // Project's SshTarget matches the devbox store configured above.
        // LocalPath must be blank — this is a remote-only SSH project.
        var proj = NewProject("myproject", @"d:\repos\myproject");
        proj.Locations.SshTarget = @"devuser@192.168.64.10:d:\repos\myproject";
        proj.Locations.LocalPath = string.Empty;
        projects.Map[@"d:\repos\myproject"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal("devbox", ov.StoreId);
        Assert.Equal("myproject", ov.Folder);
    }

    [Fact]
    public void LoadProject_PathBasedSshEntry_NoMatchingStore_StoreIdAndFolderRemainEmpty()
    {
        // SshTarget present but no configured store matches the host → derivation returns false.
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "otherbox", Type = "ssh", Host = "10.0.0.1", Root = @"d:\repos", User = "devuser" });

        var pr = new ProjectRef { Id = "myproject", Path = @"C:\stubs\myproject", StoreId = "", Folder = "" };

        var proj = NewProject("myproject", @"C:\stubs\myproject");
        proj.Locations.SshTarget = @"devuser@192.168.64.10:d:\repos\myproject"; // host doesn't match otherbox
        proj.Locations.LocalPath = string.Empty;
        projects.Map[@"C:\stubs\myproject"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal(string.Empty, ov.StoreId);
        Assert.Equal(string.Empty, ov.Folder);
    }

    [Fact]
    public void LoadProject_StoreBackedEntry_SshTargetDoesNotOverrideExistingStoreId()
    {
        // Store-backed entry (UsesStore == true) — the derivation block is skipped entirely.
        // The StoreId already set on the portfolio entry takes precedence.
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"d:\repos", User = "devuser" });

        var pr = new ProjectRef
        {
            Id = "myproject",
            Path = @"devuser@192.168.64.10:d:\repos\myproject",
            StoreId = "devbox",
            Folder = "myproject"
        };

        var proj = NewProject("myproject", @"devuser@192.168.64.10:d:\repos\myproject");
        proj.Locations.SshTarget = @"devuser@192.168.64.10:d:\repos\myproject";
        projects.Map[@"devuser@192.168.64.10:d:\repos\myproject"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        // Existing store-backed values are preserved — derivation block is NOT entered.
        Assert.Equal("devbox", ov.StoreId);
        Assert.Equal("myproject", ov.Folder);
    }

    [Fact]
    public void LoadProject_LocalPathEntry_NoSshTarget_StoreIdAndFolderRemainEmpty()
    {
        // Local project with no SshTarget — the derivation guard short-circuits and
        // StoreId/Folder stay empty (correct behavior for non-SSH entries unchanged).
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "local", Type = "local", Root = @"C:\repos" });

        var pr = new ProjectRef { Id = "localproj", Path = @"C:\repos\localproj", StoreId = "", Folder = "" };

        var proj = NewProject("localproj", @"C:\repos\localproj");
        // SshTarget deliberately left empty — simulates a plain local project.
        projects.Map[@"C:\repos\localproj"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal(string.Empty, ov.StoreId);
        Assert.Equal(string.Empty, ov.Folder);
    }

    // ---- ADR-010 hybrid-safety & POSIX regression tests ----

    [Fact]
    public void LoadProject_HybridSshAndLocalPath_DoesNotDeriveStoreId()
    {
        // Hybrid project: has both SshTarget AND a real LocalPath. Derivation
        // must be skipped because AddProjectWindow is single-store — saving a
        // derived SSH store would drop the local side.
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"d:\repos", User = "devuser" });

        var pr = new ProjectRef { Id = "hybrid", Path = @"C:\local\hybrid", StoreId = "", Folder = "" };

        var proj = NewProject("hybrid", @"C:\local\hybrid");
        proj.Locations.SshTarget = @"devuser@192.168.64.10:d:\repos\hybrid";
        proj.Locations.LocalPath = @"C:\local\hybrid"; // non-blank → hybrid
        projects.Map[@"C:\local\hybrid"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        // StoreId must remain empty — hybrid project must not derive SSH store.
        Assert.Equal(string.Empty, ov.StoreId);
        Assert.Equal(string.Empty, ov.Folder);
    }

    [Fact]
    public void LoadProject_RemoteOnlySshEntry_LocalPathBlank_DerivesSuccessfully()
    {
        // Explicit verification that the fixture's LocalPath is truly blank
        // (not inherited from NewProject's root parameter).
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "lnxbox", Type = "ssh", Host = "linuxhost", Root = "/srv/repos", User = "devuser" });

        var pr = new ProjectRef { Id = "posixproj", Path = @"C:\stubs\posixproj", StoreId = "", Folder = "" };

        var proj = NewProject("posixproj", @"C:\stubs\posixproj");
        proj.Locations.SshTarget = "devuser@linuxhost:/srv/repos/posixproj";
        proj.Locations.LocalPath = string.Empty;
        projects.Map[@"C:\stubs\posixproj"] = new ProjectLoadResult { Project = proj };

        // Verify fixture precondition
        Assert.True(string.IsNullOrWhiteSpace(proj.Locations.LocalPath),
            "Fixture LocalPath must be blank for remote-only derivation");

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal("lnxbox", ov.StoreId);
        Assert.Equal("posixproj", ov.Folder);
    }

    // ---- IsStoreIdentityDerived provenance tests ----

    [Fact]
    public void LoadProject_DerivedSshIdentity_StampsIsStoreIdentityDerivedTrue()
    {
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"d:\repos", User = "dev" });

        var pr = new ProjectRef { Id = "proj1", Path = @"C:\stubs\proj1", StoreId = "", Folder = "" };
        var proj = NewProject("proj1", @"C:\stubs\proj1");
        proj.Locations.SshTarget = @"dev@192.168.64.10:d:\repos\proj1";
        proj.Locations.LocalPath = string.Empty;
        projects.Map[@"C:\stubs\proj1"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        Assert.True(ov.IsStoreIdentityDerived);
        Assert.Equal("devbox", ov.StoreId);
        Assert.Equal("proj1", ov.Folder);
    }

    [Fact]
    public void LoadProject_StoreBackedEntry_IsStoreIdentityDerivedFalse()
    {
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"d:\repos", User = "dev" });

        var pr = new ProjectRef { Id = "proj2", Path = @"C:\stores\devbox\proj2", StoreId = "devbox", Folder = "proj2" };
        var proj = NewProject("proj2", @"C:\stores\devbox\proj2");
        proj.Locations.SshTarget = @"dev@192.168.64.10:d:\repos\proj2";
        projects.Map[@"C:\stores\devbox\proj2"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        Assert.False(ov.IsStoreIdentityDerived);
        Assert.Equal("devbox", ov.StoreId);
    }

    [Fact]
    public void LoadProject_UnresolvedSshEntry_IsStoreIdentityDerivedFalse()
    {
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "10.0.0.1", Root = @"d:\repos", User = "dev" });

        var pr = new ProjectRef { Id = "nope", Path = @"C:\stubs\nope", StoreId = "", Folder = "" };
        var proj = NewProject("nope", @"C:\stubs\nope");
        proj.Locations.SshTarget = @"dev@192.168.64.99:d:\repos\nope"; // host mismatch
        proj.Locations.LocalPath = string.Empty;
        projects.Map[@"C:\stubs\nope"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        Assert.False(ov.IsStoreIdentityDerived);
        Assert.Equal(string.Empty, ov.StoreId);
    }

    [Fact]
    public void LoadProject_HybridEntry_IsStoreIdentityDerivedFalse()
    {
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"d:\repos", User = "dev" });

        var pr = new ProjectRef { Id = "hyb", Path = @"C:\local\hyb", StoreId = "", Folder = "" };
        var proj = NewProject("hyb", @"C:\local\hyb");
        proj.Locations.SshTarget = @"dev@192.168.64.10:d:\repos\hyb";
        proj.Locations.LocalPath = @"C:\local\hyb";
        projects.Map[@"C:\local\hyb"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov = svc.LoadProject(pr, includeRepoScan: false);

        Assert.False(ov.IsStoreIdentityDerived);
        Assert.Equal(string.Empty, ov.StoreId);
    }

    [Fact]
    public void LoadProject_DerivedTarget_Changes_BetweenRefreshes_ResolvesToDifferentStore()
    {
        // Simulates project.yml SSH target changing between refreshes.
        // Because derived identity is not persisted in the ProjectRef, each load
        // re-resolves against current project state and current stores.
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var store1 = new RepoStore { Id = "box1", Type = "ssh", Host = "host1", Root = "/srv/repos", User = "dev" };
        var store2 = new RepoStore { Id = "box2", Type = "ssh", Host = "host2", Root = "/data/repos", User = "dev" };
        var storeProvider = new FakeStoreProvider(store1, store2);

        var pr = new ProjectRef { Id = "drift", Path = @"C:\stubs\drift", StoreId = "", Folder = "" };

        // First load: resolves to box1
        var proj = NewProject("drift", @"C:\stubs\drift");
        proj.Locations.SshTarget = "dev@host1:/srv/repos/drift";
        proj.Locations.LocalPath = string.Empty;
        projects.Map[@"C:\stubs\drift"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov1 = svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal("box1", ov1.StoreId);
        Assert.True(ov1.IsStoreIdentityDerived);

        // Simulate target change in project.yml between refreshes
        var proj2 = NewProject("drift", @"C:\stubs\drift");
        proj2.Locations.SshTarget = "dev@host2:/data/repos/drift";
        proj2.Locations.LocalPath = string.Empty;
        projects.Map[@"C:\stubs\drift"] = new ProjectLoadResult { Project = proj2 };

        // Re-load with same ProjectRef (StoreId still blank — as ToProjectRef would produce)
        var ov2 = svc.LoadProject(pr, includeRepoScan: false);

        Assert.Equal("box2", ov2.StoreId);
        Assert.Equal("drift", ov2.Folder);
        Assert.True(ov2.IsStoreIdentityDerived);
    }

    [Fact]
    public void LoadProject_LocalPathAppears_DerivedIdentityNotReapplied()
    {
        // Project gains a LocalPath between refreshes → becomes hybrid → derivation skipped.
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"d:\repos", User = "dev" });

        var pr = new ProjectRef { Id = "evolve", Path = @"C:\stubs\evolve", StoreId = "", Folder = "" };

        // First load: remote-only → derived
        var proj1 = NewProject("evolve", @"C:\stubs\evolve");
        proj1.Locations.SshTarget = @"dev@192.168.64.10:d:\repos\evolve";
        proj1.Locations.LocalPath = string.Empty;
        projects.Map[@"C:\stubs\evolve"] = new ProjectLoadResult { Project = proj1 };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);
        var ov1 = svc.LoadProject(pr, includeRepoScan: false);
        Assert.True(ov1.IsStoreIdentityDerived);
        Assert.Equal("devbox", ov1.StoreId);

        // Second load: LocalPath now set → hybrid → no derivation
        var proj2 = NewProject("evolve", @"C:\stubs\evolve");
        proj2.Locations.SshTarget = @"dev@192.168.64.10:d:\repos\evolve";
        proj2.Locations.LocalPath = @"C:\local\evolve";
        projects.Map[@"C:\stubs\evolve"] = new ProjectLoadResult { Project = proj2 };

        var ov2 = svc.LoadProject(pr, includeRepoScan: false);
        Assert.False(ov2.IsStoreIdentityDerived);
        Assert.Equal(string.Empty, ov2.StoreId);
        Assert.Equal(string.Empty, ov2.Folder);
    }

    [Fact]
    public void LoadProject_StoreBackedIdentity_SurvivesRefreshCycle()
    {
        // Genuine store-backed entry: StoreId/Folder survive across loads (not cleared).
        var portfolio = new FakePortfolioProvider();
        var projects = new FakeProjectProvider();
        var storeProvider = new FakeStoreProvider(
            new RepoStore { Id = "devbox", Type = "ssh", Host = "192.168.64.10", Root = @"d:\repos", User = "dev" });

        var pr = new ProjectRef { Id = "stable", Path = @"C:\stores\devbox\stable", StoreId = "devbox", Folder = "stable" };
        var proj = NewProject("stable", @"C:\stores\devbox\stable");
        proj.Locations.SshTarget = @"dev@192.168.64.10:d:\repos\stable";
        projects.Map[@"C:\stores\devbox\stable"] = new ProjectLoadResult { Project = proj };

        var svc = BuildSvcWithStore(portfolio, projects, storeProvider);

        // First load
        var ov1 = svc.LoadProject(pr, includeRepoScan: false);
        Assert.False(ov1.IsStoreIdentityDerived);
        Assert.Equal("devbox", ov1.StoreId);
        Assert.Equal("stable", ov1.Folder);

        // Simulate what ToProjectRef does for store-backed: keeps StoreId/Folder
        var pr2 = new ProjectRef { Id = ov1.Id, Path = ov1.SourcePath, StoreId = ov1.StoreId, Folder = ov1.Folder };
        var ov2 = svc.LoadProject(pr2, includeRepoScan: false);
        Assert.False(ov2.IsStoreIdentityDerived);
        Assert.Equal("devbox", ov2.StoreId);
        Assert.Equal("stable", ov2.Folder);
    }

    // ---- Real-provider integration proofs (ADR-009 / ADR-010) ---------------

    private static string WriteTempPortfolio(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "ct-regression-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, content);
        return path;
    }

    private ControlTowerService BuildSvcWithRealProviders(
        IPortfolioProvider portfolio,
        IProjectProvider projects,
        IStoreProvider? storeProvider = null) =>
        new(portfolio, projects,
            new FakeProductMapProvider(),
            new FakePlanningBoardProvider(),
            new FakeRepoScanner(),
            new FakeSnapshotStore(),
            new FakeLaunchService(),
            new FakeRegistrationService(),
            storeProvider: storeProvider);

    /// <summary>
    /// Real-provider proof for ADR-009 effective-folder behaviour.
    /// Schema v1 SSH store entry with no explicit <c>folder:</c> field round-trips
    /// through real YAML parsing and the service layer, yielding effective folder = project id.
    /// </summary>
    [Fact]
    public void RealProviders_SchemaV1_SshStoreEntry_OmittedFolder_StampsStoreIdAndEffectiveFolder()
    {
        var yaml = @"schema_version: 1
projects:
  - id: my-ssh-proj
    store: devbox
";
        var tempPath = WriteTempPortfolio(yaml);
        try
        {
            var store = new RepoStore
            {
                Id = "devbox",
                Type = "ssh",
                Host = "192.168.64.10",
                Root = @"d:\repos",
                User = string.Empty
            };
            var storeProvider = new StoreProvider(new[] { store });
            var portfolioProvider = new PortfolioYamlProvider(tempPath, storeProvider);
            var portfolio = portfolioProvider.LoadPortfolio();

            var pRef = Assert.Single(portfolio.Projects);
            Assert.Equal("my-ssh-proj", pRef.Id);
            Assert.Equal("devbox", pRef.StoreId);
            Assert.Equal(string.Empty, pRef.Folder); // folder omitted from YAML → blank
            Assert.True(pRef.UsesStore);

            var resolvedPath = storeProvider.ResolveProjectPath("devbox", "my-ssh-proj", string.Empty);
            var fakeProjects = new FakeProjectProvider();
            fakeProjects.Map[resolvedPath] = new ProjectLoadResult
            {
                Project = new ProjectDefinition
                {
                    Id = "my-ssh-proj",
                    DisplayName = "My SSH Project",
                    ProjectRootPath = resolvedPath
                }
            };

            var svc = BuildSvcWithRealProviders(portfolioProvider, fakeProjects, storeProvider);
            var ov = svc.LoadProject(pRef, includeRepoScan: false);

            Assert.Equal("devbox", ov.StoreId);
            Assert.Equal("my-ssh-proj", ov.Folder); // effective folder = project id
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    /// <summary>
    /// Real-provider proof for ADR-010 Option B fix.
    /// Schema v0 path-based SSH entry (no <c>store:</c> field) derives <c>StoreId</c>
    /// and <c>Folder</c> from the project's <c>SshTarget</c> through real providers.
    /// </summary>
    [Fact]
    public void RealProviders_SchemaV0_PathBasedSshEntry_DerivesStoreIdAndFolderFromSshTarget()
    {
        const string ProjectId = "my-ssh-proj";
        const string LocalStubPath = @"C:\fake-stubs\my-ssh-proj";

        var yaml = $@"schema_version: 0
projects:
  - id: {ProjectId}
    path: '{LocalStubPath}'
";
        var tempPath = WriteTempPortfolio(yaml);
        try
        {
            var store = new RepoStore
            {
                Id = "devbox",
                Type = "ssh",
                Host = "192.168.64.10",
                Root = @"d:\repos",
                User = "devuser"
            };
            var storeProvider = new StoreProvider(new[] { store });
            var portfolioProvider = new PortfolioYamlProvider(tempPath, storeProvider);
            var portfolio = portfolioProvider.LoadPortfolio();

            var pRef = Assert.Single(portfolio.Projects);
            Assert.Equal(ProjectId, pRef.Id);
            Assert.Equal(string.Empty, pRef.StoreId); // no store: in YAML
            Assert.Equal(string.Empty, pRef.Folder);
            Assert.False(pRef.UsesStore);             // path-based entry
            Assert.Equal(LocalStubPath, pRef.Path);

            const string SshTarget = @"devuser@192.168.64.10:d:\repos\my-ssh-proj";
            var sshProjectDef = new ProjectDefinition
            {
                Id = ProjectId,
                DisplayName = "My SSH Project",
                ProjectRootPath = LocalStubPath
            };
            sshProjectDef.Locations.SshTarget = SshTarget;

            var fakeProjects = new FakeProjectProvider();
            fakeProjects.Map[LocalStubPath] = new ProjectLoadResult { Project = sshProjectDef };

            var svc = BuildSvcWithRealProviders(portfolioProvider, fakeProjects, storeProvider);
            var ov = svc.LoadProject(pRef, includeRepoScan: false);

            Assert.Equal("devbox", ov.StoreId);
            Assert.Equal("my-ssh-proj", ov.Folder);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }
}
