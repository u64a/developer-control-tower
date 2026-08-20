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

/// <summary>
/// Regression tests for the duplicate-overview bug where the same physical project
/// reachable under two portfolio entries produced two rows in LoadPortfolio().
///
/// PRIMARY FIX (Core seam): ControlTowerService.LoadPortfolio() now deduplicates
/// composed overviews by overview.Id (OrdinalIgnoreCase, keeps first, preserves order;
/// empty ids are not collapsed).
///
/// SECONDARY FIX (Infra seam): PortfolioYamlProvider.LoadPortfolio() self-heals
/// two entries with different ids but the same physical path → returns one entry.
/// </summary>
public class DuplicationRegressionTests
{
    // ── Fakes (copied/inlined from ControlTowerServiceTests to keep this file
    //    self-contained and untracked-file-safe across git stash operations) ──

    private sealed class FakePortfolioProvider : IPortfolioProvider
    {
        public PortfolioIndex Index { get; set; } = new PortfolioIndex();
        public PortfolioIndex LoadPortfolio() => Index;
        public void SavePortfolio(PortfolioIndex portfolio) => Index = portfolio;
    }

    private sealed class FakeProjectProvider : IProjectProvider
    {
        public Dictionary<string, ProjectLoadResult> Map { get; } = new();
        public ProjectLoadResult LoadProject(string projectRootPath)
        {
            if (Map.TryGetValue(projectRootPath, out var r)) return r;
            var fb = new ProjectLoadResult();
            fb.Project.Id = "auto-" + projectRootPath;
            fb.Project.ProjectRootPath = projectRootPath;
            return fb;
        }
    }

    private sealed class FakeProductMapProvider : IProductMapProvider
    {
        public ProductMapLoadResult LoadProductMap(string path, string sourceRef) => new();
    }

    private sealed class FakePlanningBoardProvider : IPlanningBoardProvider
    {
        public PlanningBoardLoadResult LoadPlanningBoard(string path) => new();
        public PlanningBoardLoadResult ParseFromContent(string yaml, string label) => new();
    }

    private sealed class FakeRepoScanner : IRepoScanner
    {
        public RepoSnapshot Scan(string path) =>
            new() { IsAvailable = true, Branch = "main", RepoPath = path };
    }

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public RepoSnapshot? Load(string id) => null;
        public void Save(string id, RepoSnapshot snap) { }
    }

    private sealed class FakeLaunchService : ILaunchService
    {
        public LaunchResult Launch(ProjectDefinition project, LaunchTargetKind kind) =>
            new() { Status = LaunchStatus.Ok, Success = true };
    }

    private sealed class FakeRegistrationService : IProjectRegistrationService
    {
        public ProjectRegistrationResult RegisterProject(ProjectRegistrationRequest req) => new() { Success = true };
        public ProjectRegistrationResult RemoveProject(string id) => new() { Success = true };
    }

    private static ProjectDefinition MakeProject(string id, string root)
    {
        var p = new ProjectDefinition { Id = id, DisplayName = id, ProjectRootPath = root };
        p.Locations.LocalPath = root;
        return p;
    }

    private static ControlTowerService BuildService(
        FakePortfolioProvider portfolio,
        FakeProjectProvider projects)
    {
        return new ControlTowerService(
            portfolio,
            projects,
            new FakeProductMapProvider(),
            new FakePlanningBoardProvider(),
            new FakeRepoScanner(),
            new FakeSnapshotStore(),
            new FakeLaunchService(),
            new FakeRegistrationService());
    }

    // ── PRIMARY REGRESSION (Core seam) ─────────────────────────────────────

    /// <summary>
    /// The real duplication condition: two portfolio entries (different ref ids,
    /// different paths) both resolve to a ProjectDefinition whose Id is the same
    /// stable local id — i.e., the same physical project.yml reached two ways.
    ///
    /// BEFORE FIX: LoadPortfolio() returned 2 overviews with Id = "my-project".
    /// AFTER FIX:  LoadPortfolio() returns exactly 1 (first occurrence wins).
    /// </summary>
    [Fact]
    public void LoadPortfolio_TwoEntriesSameOverviewId_ReturnsOnce()
    {
        var portfolio = new FakePortfolioProvider();
        // Two portfolio refs with distinct paths — simulates the real scenario
        // where the same repo was registered twice under different portfolio ids.
        portfolio.Index.Projects.Add(new ProjectRef { Id = "entry-alpha", Path = @"C:\repos\my-project" });
        portfolio.Index.Projects.Add(new ProjectRef { Id = "entry-beta",  Path = @"C:\repos\my-project-alias" });

        var projects = new FakeProjectProvider();
        // Both paths resolve to the same stable project id (same project.yml content).
        projects.Map[@"C:\repos\my-project"]       = new ProjectLoadResult { Project = MakeProject("my-project", @"C:\repos\my-project") };
        projects.Map[@"C:\repos\my-project-alias"] = new ProjectLoadResult { Project = MakeProject("my-project", @"C:\repos\my-project-alias") };

        var svc = BuildService(portfolio, projects);

        var result = svc.LoadPortfolio();

        // Exactly one row — the duplicate produced by the second entry must be suppressed.
        Assert.Single(result);
        Assert.Equal("my-project", result[0].Id);
    }

    /// <summary>
    /// Dedup is case-insensitive: "My-Project" and "my-project" are the same stable id.
    /// </summary>
    [Fact]
    public void LoadPortfolio_TwoEntriesSameOverviewIdDifferentCase_ReturnsOnce()
    {
        var portfolio = new FakePortfolioProvider();
        portfolio.Index.Projects.Add(new ProjectRef { Id = "e1", Path = @"C:\repos\proj" });
        portfolio.Index.Projects.Add(new ProjectRef { Id = "e2", Path = @"C:\repos\proj-alt" });

        var projects = new FakeProjectProvider();
        projects.Map[@"C:\repos\proj"]     = new ProjectLoadResult { Project = MakeProject("My-Project", @"C:\repos\proj") };
        projects.Map[@"C:\repos\proj-alt"] = new ProjectLoadResult { Project = MakeProject("my-project", @"C:\repos\proj-alt") };

        var svc = BuildService(portfolio, projects);
        var result = svc.LoadPortfolio();

        Assert.Single(result);
        // First occurrence wins — keeps the original casing from the first entry.
        Assert.Equal("My-Project", result[0].Id);
    }

    /// <summary>
    /// Empty-id overviews are NOT collapsed — they lack a stable identity.
    /// Two empty-id entries must both survive the dedup pass.
    /// </summary>
    [Fact]
    public void LoadPortfolio_TwoEntriesWithEmptyId_BothRetained()
    {
        var portfolio = new FakePortfolioProvider();
        portfolio.Index.Projects.Add(new ProjectRef { Id = "x1", Path = @"C:\repos\unnamed-a" });
        portfolio.Index.Projects.Add(new ProjectRef { Id = "x2", Path = @"C:\repos\unnamed-b" });

        var projects = new FakeProjectProvider();
        projects.Map[@"C:\repos\unnamed-a"] = new ProjectLoadResult { Project = MakeProject("", @"C:\repos\unnamed-a") };
        projects.Map[@"C:\repos\unnamed-b"] = new ProjectLoadResult { Project = MakeProject("", @"C:\repos\unnamed-b") };

        var svc = BuildService(portfolio, projects);
        var result = svc.LoadPortfolio();

        // Both retained — empty id is not a stable identity to collapse on.
        Assert.Equal(2, result.Count);
    }

    /// <summary>
    /// Order is preserved: first occurrence of a duplicated id must be kept, not the second.
    /// </summary>
    [Fact]
    public void LoadPortfolio_DuplicateId_FirstOccurrenceWins()
    {
        var portfolio = new FakePortfolioProvider();
        portfolio.Index.Projects.Add(new ProjectRef { Id = "first",  Path = @"C:\repos\first-path" });
        portfolio.Index.Projects.Add(new ProjectRef { Id = "second", Path = @"C:\repos\second-path" });
        portfolio.Index.Projects.Add(new ProjectRef { Id = "third",  Path = @"C:\repos\third-path" });

        var projects = new FakeProjectProvider();
        // "first-path" and "third-path" both resolve to "shared-id".
        // "second-path" is distinct.
        projects.Map[@"C:\repos\first-path"]  = new ProjectLoadResult { Project = MakeProject("shared-id",  @"C:\repos\first-path") };
        projects.Map[@"C:\repos\second-path"] = new ProjectLoadResult { Project = MakeProject("unique-id",  @"C:\repos\second-path") };
        projects.Map[@"C:\repos\third-path"]  = new ProjectLoadResult { Project = MakeProject("shared-id",  @"C:\repos\third-path") };

        var svc = BuildService(portfolio, projects);
        var result = svc.LoadPortfolio();

        Assert.Equal(2, result.Count);
        Assert.Equal("shared-id", result[0].Id);   // from first-path
        Assert.Equal("unique-id",  result[1].Id);   // from second-path; third-path suppressed
    }

    // ── SECONDARY REGRESSION (Infra seam — PortfolioYamlProvider) ──────────

    /// <summary>
    /// PortfolioYamlProvider self-heal: two entries with DIFFERENT ids but the
    /// SAME physical path are deduplicated to one entry (keeps first, by path).
    ///
    /// BEFORE FIX: both entries survived into the portfolio index, causing the
    /// downstream Core dedup to be the only safety net.
    /// AFTER FIX:  the provider itself collapses them so the index is clean.
    /// </summary>
    [Fact]
    public void PortfolioYamlProvider_TwoEntriesSamePath_ReturnsOne()
    {
        var yaml = @"schema_version: 0
projects:
  - id: project-first
    path: 'C:\Repos\SharedPath'
  - id: project-second
    path: 'C:\Repos\SharedPath'
";
        var path = Path.Combine(Path.GetTempPath(), "ct-dedup-test-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, yaml);
        try
        {
            var provider = new PortfolioYamlProvider(path);
            var result = provider.LoadPortfolio();

            Assert.Single(result.Projects);
            Assert.Equal("project-first", result.Projects[0].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
