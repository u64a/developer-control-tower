using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

/// <summary>
/// Regression suite that closes the false-confidence gap exposed by the old
/// <c>FakeProjectProvider</c> returning unique <c>"auto-"+path</c> ids — a
/// behaviour that prevented reproduction of the real data-loss scenario where
/// many unconfigured folders all shared the sentinel id <c>"missing.project"</c>
/// and were collapsed to a single row by the dedup pass.
///
/// Covers four complementary axes:
/// <list type="number">
///   <item><b>a) Uniqueness</b> — REAL provider, two distinct missing-yml folders → different fallback ids.</item>
///   <item><b>b) Stability</b> — REAL provider, same folder loaded twice → same id both times.</item>
///   <item><b>c) Invalid yml</b> — REAL provider, yml present but no id field → "invalid." prefix + issue.</item>
///   <item><b>d) Dedup backstop</b> — 9 portfolio entries all returning the OLD shared sentinel
///     <c>"missing.project"</c> (via a fake that simulates the pre-fix provider) must ALL survive;
///     two entries with the same REAL stable id must collapse to one.</item>
/// </list>
/// </summary>
public class SentinelIdRegressionTests
{
    // ── Helpers ─────────────────────────────────────────────────────────────

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-sentinel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── a) REAL provider: two distinct missing-yml folders → unique ids ─────

    [Fact]
    public void RealProvider_TwoDistinctMissingYmlFolders_ProduceDifferentUnstableIds()
    {
        var dir1 = MakeTempDir();
        var dir2 = MakeTempDir();
        try
        {
            var provider = new ProjectYamlProvider();
            var r1 = provider.LoadProject(dir1);
            var r2 = provider.LoadProject(dir2);

            // Both ids must be unstable (start with "missing.").
            Assert.True(ProjectIdentity.IsUnstable(r1.Project.Id),
                $"dir1 id should be unstable but was: {r1.Project.Id}");
            Assert.True(r1.Project.Id.StartsWith("missing.", StringComparison.OrdinalIgnoreCase),
                $"dir1 id should start with 'missing.' but was: {r1.Project.Id}");

            Assert.True(ProjectIdentity.IsUnstable(r2.Project.Id),
                $"dir2 id should be unstable but was: {r2.Project.Id}");
            Assert.StartsWith("missing.", r2.Project.Id);            // The two ids must differ — each path gets a unique fingerprint.
            Assert.NotEqual(r1.Project.Id, r2.Project.Id);

            // Both must surface the expected ValidationIssue.
            Assert.Contains(r1.Issues, i =>
                i.Severity == IssueSeverity.Error &&
                i.Message.Contains("Missing", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(r2.Issues, i =>
                i.Severity == IssueSeverity.Error &&
                i.Message.Contains("Missing", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir1, recursive: true);
            Directory.Delete(dir2, recursive: true);
        }
    }

    // ── b) REAL provider: same folder → stable id across two loads ──────────

    [Fact]
    public void RealProvider_SameMissingYmlFolder_ProducesSameIdBothTimes()
    {
        var dir = MakeTempDir();
        try
        {
            var provider = new ProjectYamlProvider();
            var r1 = provider.LoadProject(dir);
            var r2 = provider.LoadProject(dir);

            Assert.Equal(r1.Project.Id, r2.Project.Id);
            Assert.StartsWith("missing.", r1.Project.Id);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── c) REAL provider: yml present, no id field → "invalid." prefix ──────

    [Fact]
    public void RealProvider_YmlWithNoId_ProducesInvalidPrefixAndIssue()
    {
        var dir = MakeTempDir();
        try
        {
            var metaDir = Path.Combine(dir, ".controltower");
            Directory.CreateDirectory(metaDir);
            File.WriteAllText(
                Path.Combine(metaDir, "project.yml"),
                "kind: developer-control-tower/project\nschema_version: 0\ndisplay_name: NoId\n");

            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            Assert.True(ProjectIdentity.IsUnstable(result.Project.Id),
                $"id should be unstable but was: {result.Project.Id}");
            Assert.True(result.Project.Id.StartsWith("invalid.", StringComparison.OrdinalIgnoreCase),
                $"id should start with 'invalid.' but was: {result.Project.Id}");

            Assert.Contains(result.Issues, i =>
                i.Severity == IssueSeverity.Error &&
                i.Message.Contains("missing id", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ── d) DEDUP BACKSTOP — the exact data-loss scenario ────────────────────

    // Fakes used only inside this test class.

    private sealed class FakePortfolioProvider : IPortfolioProvider
    {
        public PortfolioIndex Index { get; set; } = new PortfolioIndex();
        public PortfolioIndex LoadPortfolio() => Index;
        public void SavePortfolio(PortfolioIndex portfolio) => Index = portfolio;
    }

    /// <summary>
    /// Simulates the pre-fix <c>ProjectYamlProvider</c> behaviour: every path
    /// that has no explicit mapping returns the SHARED sentinel literal
    /// <c>"missing.project"</c> — the exact value that caused data loss before
    /// <c>ProjectIdentity.CreateFallback</c> was introduced.
    /// </summary>
    private sealed class LegacySentinelProjectProvider : IProjectProvider
    {
        public Dictionary<string, ProjectLoadResult> Map { get; } = new();

        public ProjectLoadResult LoadProject(string projectRootPath)
        {
            if (Map.TryGetValue(projectRootPath, out var r)) return r;

            // Pre-fix behaviour: every unmapped path gets the SHARED sentinel.
            var result = new ProjectLoadResult();
            result.Project.Id = "missing.project";
            result.Project.ProjectRootPath = projectRootPath;
            result.Issues.Add(new ValidationIssue(IssueSeverity.Error, "Missing .controltower\\project.yml"));
            return result;
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

    private static ProjectDefinition MakeRealProject(string id, string root)
    {
        var p = new ProjectDefinition { Id = id, DisplayName = id, ProjectRootPath = root };
        p.Locations.LocalPath = root;
        return p;
    }

    private static ControlTowerService BuildService(
        FakePortfolioProvider portfolio,
        IProjectProvider projects)
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

    /// <summary>
    /// Data-loss backstop: 9 portfolio entries whose projects all share the
    /// OLD shared sentinel id <c>"missing.project"</c> must ALL survive the
    /// dedup pass — none may be collapsed.
    ///
    /// Mirrors Chris's real-world scenario where 9 unconfigured repo folders
    /// were silently collapsed into a single row before the fix.
    ///
    /// Additionally asserts that two entries with the same REAL stable id DO
    /// collapse to one, confirming the dedup still works for legitimate cases.
    /// </summary>
    [Fact]
    public void LoadPortfolio_NineSentinelEntries_AllRetained_RealIdDuplicateCollapsed()
    {
        var portfolio = new FakePortfolioProvider();
        var projects = new LegacySentinelProjectProvider();

        const int SentinelCount = 9;

        // Add 9 entries that will each resolve to "missing.project".
        for (int i = 1; i <= SentinelCount; i++)
        {
            var path = $@"C:\repos\unconfigured-{i:D2}";
            portfolio.Index.Projects.Add(new ProjectRef { Id = $"entry-{i:D2}", Path = path });
            // No mapping in projects.Map → LegacySentinelProjectProvider returns "missing.project"
        }

        // Add two entries with the SAME REAL stable id (should collapse to 1).
        projects.Map[@"C:\repos\real-alpha"] =
            new ProjectLoadResult { Project = MakeRealProject("my-real-project", @"C:\repos\real-alpha") };
        projects.Map[@"C:\repos\real-beta"] =
            new ProjectLoadResult { Project = MakeRealProject("my-real-project", @"C:\repos\real-beta") };

        portfolio.Index.Projects.Add(new ProjectRef { Id = "real-alpha", Path = @"C:\repos\real-alpha" });
        portfolio.Index.Projects.Add(new ProjectRef { Id = "real-beta",  Path = @"C:\repos\real-beta" });

        var svc = BuildService(portfolio, projects);
        var result = svc.LoadPortfolio();

        // All 9 sentinel entries must survive — the shared "missing.project" id
        // is unstable and must NEVER be used as a dedup key.
        var sentinelRows = result.Where(o => o.Id == "missing.project").ToList();
        Assert.Equal(SentinelCount, sentinelRows.Count);

        // The two real-id entries must collapse to exactly one.
        var realRows = result.Where(o => o.Id == "my-real-project").ToList();
        Assert.Single(realRows);

        // Total: 9 sentinel rows + 1 deduped real row = 10.
        Assert.Equal(SentinelCount + 1, result.Count);
    }
}
