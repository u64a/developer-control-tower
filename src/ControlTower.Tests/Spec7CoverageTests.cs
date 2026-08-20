using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Git;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

// Targets v0-spec §7 rows that the existing 192 tests do not yet cover:
//   * "Invalid project.yml" — explicit *partial loading semantics* (id missing
//     but other fields salvaged; no auto-rewrite; placeholder id returned).
//   * "GitHub or ADO unavailable" — external launches may fail but local
//     metadata must remain intact and queryable. Modelled here with fake
//     launch / roadmap providers that throw HttpRequestException /
//     TaskCanceledException, the way real network-bound adapters would.
public class Spec7CoverageTests
{
    // ---- §7: invalid project.yml — partial loading ------------------------

    private static string CreateTempProjectDir(string projectYaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-spec7-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, ".controltower"));
        File.WriteAllText(Path.Combine(dir, ".controltower", "project.yml"), projectYaml);
        return dir;
    }

    [Fact]
    public void InvalidProjectYml_MissingId_SalvagesOtherFieldsAndReportsError()
    {
        // Spec §7: "Mark the project invalid with clear validation errors; do
        // not infer a new ID or rewrite the file." → other fields must still
        // be readable so the UI can show *why* the project is invalid.
        var yaml = @"kind: developer-control-tower/project
schema_version: 0

display_name: Has-Name-No-Id
summary: Should still be readable
lifecycle_state: active
planning:
  authority: ado
";
        var dir = CreateTempProjectDir(yaml);
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            Assert.Contains(result.Issues, i =>
                i.Severity == IssueSeverity.Error &&
                i.Message.Contains("missing id", StringComparison.OrdinalIgnoreCase));

            // Placeholder id is used so downstream code never crashes on null,
            // but it is clearly NOT a salvaged guess of a real id.
            // New contract: id is unstable (starts with "invalid.") but is unique per path
            // (not the shared literal "invalid.project"). ProjectIdentity.IsUnstable must return true.
            Assert.True(ProjectIdentity.IsUnstable(result.Project.Id),
                $"Expected an unstable id but got: {result.Project.Id}");
            Assert.True(result.Project.Id.StartsWith("invalid.", StringComparison.OrdinalIgnoreCase),
                $"Expected id to start with 'invalid.' but got: {result.Project.Id}");

            // Partial-load semantics: non-id fields are preserved.
            Assert.Equal("Has-Name-No-Id", result.Project.DisplayName);
            Assert.Equal("Should still be readable", result.Project.Summary);
            Assert.Equal("active", result.Project.LifecycleState);
            Assert.Equal("ado", result.Project.Planning.Authority);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InvalidProjectYml_MissingDisplayName_DegradesToFolderName_AsWarning()
    {
        // Display name is a warning, not an error: the project is still
        // usable, but we tell the user something is off.
        var yaml = @"kind: developer-control-tower/project
schema_version: 0

id: ok-id
";
        var dir = CreateTempProjectDir(yaml);
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            Assert.Equal("ok-id", result.Project.Id);
            Assert.Contains(result.Issues, i =>
                i.Severity == IssueSeverity.Warning &&
                i.Message.Contains("display_name", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(Path.GetFileName(dir), result.Project.DisplayName);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InvalidProjectYml_DoesNotRewriteFileOnDisk()
    {
        // Spec §7 explicitly: the loader must not rewrite project.yml when
        // it is invalid. We snapshot the on-disk bytes around a LoadProject
        // call and assert byte-equality.
        var yaml = "kind: developer-control-tower/project\nschema_version: 0\nsummary: only summary\n";
        var dir = CreateTempProjectDir(yaml);
        var path = Path.Combine(dir, ".controltower", "project.yml");
        try
        {
            var before = File.ReadAllBytes(path);

            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            var after = File.ReadAllBytes(path);
            Assert.Equal(before, after);
            Assert.NotEmpty(result.Issues);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ---- §7: GitHub / ADO unavailable ------------------------------------

    private sealed class FakePortfolio : IPortfolioProvider
    {
        public PortfolioIndex Index { get; } = new PortfolioIndex();
        public int LoadCount;
        public PortfolioIndex LoadPortfolio() { LoadCount++; return Index; }
        public void SavePortfolio(PortfolioIndex portfolio) { /* no-op for tests */ }
    }

    private sealed class FakeProjects : IProjectProvider
    {
        public Dictionary<string, ProjectLoadResult> Map { get; } = new();
        public ProjectLoadResult LoadProject(string projectRootPath)
        {
            if (Map.TryGetValue(projectRootPath, out var r)) return r;
            var fallback = new ProjectLoadResult();
            fallback.Project.Id = "auto";
            fallback.Project.ProjectRootPath = projectRootPath;
            return fallback;
        }
    }

    private sealed class FakeProductMap : IProductMapProvider
    {
        public ProductMapLoadResult LoadProductMap(string p, string s) => new();
    }

    private sealed class FakeBoard : IPlanningBoardProvider
    {
        public PlanningBoardLoadResult LoadPlanningBoard(string p) => new();
        public PlanningBoardLoadResult ParseFromContent(string y, string s) => new();
    }

    private sealed class FakeScanner : IRepoScanner
    {
        public RepoSnapshot Scan(string repoPath) =>
            new() { IsAvailable = true, Branch = "main", RepoPath = repoPath, HasUpstream = true };
    }

    private sealed class FakeStore : ISnapshotStore
    {
        public Dictionary<string, RepoSnapshot> Saved { get; } = new();
        public RepoSnapshot? Load(string projectId) => Saved.TryGetValue(projectId, out var s) ? s : null;
        public void Save(string projectId, RepoSnapshot snapshot) { Saved[projectId] = snapshot; }
    }

    private sealed class FakeRegistration : IProjectRegistrationService
    {
        public ProjectRegistrationResult RegisterProject(ProjectRegistrationRequest r) => new() { Success = true };
        public ProjectRegistrationResult RemoveProject(string id) => new() { Success = true };
    }

    private sealed class FailingLaunch : ILaunchService
    {
        public readonly Exception ToThrow;
        public int Calls;
        public FailingLaunch(Exception ex) { ToThrow = ex; }
        public LaunchResult Launch(ProjectDefinition project, LaunchTargetKind kind)
        {
            Calls++;
            throw ToThrow;
        }
    }

    private static ProjectDefinition NewProject(string id, string root)
    {
        var p = new ProjectDefinition { Id = id, DisplayName = id, ProjectRootPath = root };
        p.Locations.LocalPath = root;
        p.Launch.GitHub = "https://github.com/org/repo";
        return p;
    }

    [Fact]
    public void GitHubLaunch_NetworkUnavailable_PropagatesButDoesNotCorruptLocalMetadata()
    {
        // §7: "Preserve the local experience; external launches may fail but
        // must not affect local metadata."
        var portfolio = new FakePortfolio();
        portfolio.Index.Projects.Add(new ProjectRef { Id = "p1", Path = @"C:\p1" });
        var projects = new FakeProjects();
        projects.Map[@"C:\p1"] = new ProjectLoadResult { Project = NewProject("p1", @"C:\p1") };

        var launch = new FailingLaunch(new HttpRequestException("DNS down"));
        var svc = new ControlTowerService(
            portfolio, projects, new FakeProductMap(), new FakeBoard(),
            new FakeScanner(), new FakeStore(), launch, new FakeRegistration(), null);

        var pr = new ProjectRef { Id = "p1", Path = @"C:\p1" };

        // The launch failure surfaces — UI gets to show the user.
        Assert.Throws<HttpRequestException>(() => svc.Launch(pr, LaunchTargetKind.GitHub));

        // ...but local-metadata reads continue to work after the failure.
        var portfolioAfter = svc.LoadPortfolio();
        Assert.Single(portfolioAfter);
        Assert.Equal("p1", portfolioAfter[0].Id);
        Assert.Equal("p1", portfolioAfter[0].DisplayName);
    }

    [Fact]
    public void AdoLaunch_TimeoutFromExternalSystem_PropagatesButPortfolioStillUsable()
    {
        var portfolio = new FakePortfolio();
        portfolio.Index.Projects.Add(new ProjectRef { Id = "p2", Path = @"C:\p2" });
        var projects = new FakeProjects();
        var proj = NewProject("p2", @"C:\p2");
        proj.Launch.Ado = "https://dev.azure.com/org/proj";
        projects.Map[@"C:\p2"] = new ProjectLoadResult { Project = proj };

        // TaskCanceledException is what HttpClient throws on a timeout; it is
        // the realistic shape of "ADO unavailable".
        var launch = new FailingLaunch(new TaskCanceledException("timeout"));
        var svc = new ControlTowerService(
            portfolio, projects, new FakeProductMap(), new FakeBoard(),
            new FakeScanner(), new FakeStore(), launch, new FakeRegistration(), null);

        var pr = new ProjectRef { Id = "p2", Path = @"C:\p2" };
        Assert.Throws<TaskCanceledException>(() => svc.Launch(pr, LaunchTargetKind.Ado));

        // Local view still composable, including planning authority note.
        var overview = svc.LoadProject(pr, includeRepoScan: false);
        Assert.Equal("p2", overview.Id);
        Assert.NotNull(overview.PlanningAuthorityNote);
    }

    [Fact]
    public void LaunchFailure_DoesNotMutatePortfolioProvider()
    {
        // Belt-and-braces check that a failed external launch does not call
        // back into the portfolio provider — i.e., no hidden write path
        // disguised as "refresh after launch".
        var portfolio = new FakePortfolio();
        portfolio.Index.Projects.Add(new ProjectRef { Id = "p3", Path = @"C:\p3" });
        var projects = new FakeProjects();
        projects.Map[@"C:\p3"] = new ProjectLoadResult { Project = NewProject("p3", @"C:\p3") };

        var launch = new FailingLaunch(new HttpRequestException("503"));
        var svc = new ControlTowerService(
            portfolio, projects, new FakeProductMap(), new FakeBoard(),
            new FakeScanner(), new FakeStore(), launch, new FakeRegistration(), null);

        var portfolioCallsBefore = portfolio.LoadCount;
        try { svc.Launch(new ProjectRef { Id = "p3", Path = @"C:\p3" }, LaunchTargetKind.GitHub); }
        catch (HttpRequestException) { /* expected */ }

        Assert.Equal(portfolioCallsBefore, portfolio.LoadCount);
    }

    // ---- §7: missing repo path (end-to-end via real GitRepoScanner) -------

    [Fact]
    public void MissingRepoPath_LocalDirectoryAbsent_ReturnsUnavailableSnapshot()
    {
        // §7 row "missing repo path": the scanner must surface the absence
        // clearly (IsAvailable=false + a status message) and NEVER throw.
        var bogus = Path.Combine(Path.GetTempPath(), "ct-missing-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(bogus));

        var scanner = new GitRepoScanner(new ToolSettings());
        var snapshot = scanner.Scan(bogus);

        Assert.False(snapshot.IsAvailable);
        Assert.Contains("missing", snapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(bogus, snapshot.RepoPath);
    }

    [Fact]
    public void MissingRepoPath_EmptyPath_ReturnsUnavailableSnapshot()
    {
        var scanner = new GitRepoScanner(new ToolSettings());
        var snapshot = scanner.Scan(string.Empty);

        Assert.False(snapshot.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(snapshot.StatusMessage));
    }

    [Fact]
    public void MissingRepoPath_LoadProjectOverview_StaysComposableWhenRepoGone()
    {
        // End-to-end: a project whose repo folder doesn't exist must still
        // produce a usable ProjectOverview when scanned, with a clear
        // unavailable-repo state — not a crash.
        var bogus = Path.Combine(Path.GetTempPath(), "ct-missing-e2e-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(bogus));

        var portfolio = new FakePortfolio();
        portfolio.Index.Projects.Add(new ProjectRef { Id = "p-missing", Path = bogus });
        var projects = new FakeProjects();
        projects.Map[bogus] = new ProjectLoadResult { Project = NewProject("p-missing", bogus) };

        var fakeStore = new FakeStore();
        var svc = new ControlTowerService(
            portfolio, projects, new FakeProductMap(), new FakeBoard(),
            new GitRepoScanner(new ToolSettings()),
            fakeStore, new FailingLaunch(new HttpRequestException("not used")),
            new FakeRegistration(), null);

        var overview = svc.LoadProject(new ProjectRef { Id = "p-missing", Path = bogus }, includeRepoScan: true);

        Assert.Equal("p-missing", overview.Id);
        // ProjectOverview flattens RepoSnapshot; unavailable repos must not
        // throw and must produce a composable overview (any string value is
        // fine here — composer may surface "Unavailable" or similar).
        Assert.NotNull(overview.Branch);
        // The saved snapshot must record the unavailable state for next time.
        Assert.True(fakeStore.Saved.ContainsKey("p-missing"));
        Assert.False(fakeStore.Saved["p-missing"].IsAvailable);
    }

    // ---- §7: broken SSH target (end-to-end via GitRepoScanner + fake SSH) -

    private sealed class StubStoreProvider : IStoreProvider
    {
        private readonly List<RepoStore> _stores;
        public StubStoreProvider(IEnumerable<RepoStore> stores) { _stores = new List<RepoStore>(stores); }
        public IReadOnlyList<RepoStore> GetStores() => _stores;
        public RepoStore GetStore(string storeId) =>
            _stores.Find(s => string.Equals(s.Id, storeId, StringComparison.OrdinalIgnoreCase))!;
        public string ResolveProjectPath(string storeId, string projectId, string folder) =>
            string.Empty;
    }

    private sealed class StubCredentialStore : ICredentialStore
    {
        public string GetPassword(string target) => string.Empty;
        public void SetPassword(string target, string password) { }
        public void DeletePassword(string target) { }
    }

    private sealed class FailingSshService : ISshService
    {
        private readonly string _error;
        public FailingSshService(string error) { _error = error; }
        public SshResult TestConnection(string h, int p, string u, string pw) => SshResult.Fail(_error);
        public SshResult CreateDirectory(string h, int p, string u, string pw, string r) => SshResult.Fail(_error);
        public SshResult RunCommand(string h, int p, string u, string pw, string c) => SshResult.Fail(_error);
    }

    [Fact]
    public void BrokenSshTarget_HostUnreachable_ReturnsUnavailableSnapshotWithSshPrefix()
    {
        // §7 row "broken SSH target": when the remote scan can't be reached
        // we must show *why* (SSH:<reason>) without throwing.
        var store = new RepoStore
        {
            Id = "remote",
            Type = "ssh",
            Host = "build-box",
            User = "dev",
            Root = "/srv/git"
        };
        var scanner = new GitRepoScanner(
            new ToolSettings(),
            new FailingSshService("connection refused"),
            new StubCredentialStore(),
            new StubStoreProvider(new[] { store }));

        var snapshot = scanner.Scan("dev@build-box:/srv/git/repo");

        Assert.False(snapshot.IsAvailable);
        Assert.StartsWith("SSH:", snapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("connection refused", snapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void BrokenSshTarget_NoMatchingStore_ReturnsUnavailableSnapshot()
    {
        // §7: SSH path resolves but no matching store is configured →
        // unavailable, never reach the network.
        var scanner = new GitRepoScanner(
            new ToolSettings(),
            new FailingSshService("should not be called"),
            new StubCredentialStore(),
            new StubStoreProvider(System.Linq.Enumerable.Empty<RepoStore>()));

        var snapshot = scanner.Scan("dev@unknown-host:/srv/git/repo");

        Assert.False(snapshot.IsAvailable);
        Assert.Contains("No SSH store", snapshot.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }
}
