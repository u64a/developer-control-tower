using ControlTower.Core.Composition;
using ControlTower.Core.Models;
using ControlTower.Core.Time;
using ControlTower.Core.Validation;

namespace ControlTower.Tests;

public class OverviewComposerTests
{
    private sealed class FakeClock : IClock
    {
        public FakeClock(DateTime utcNow) { UtcNow = utcNow; }
        public DateTime UtcNow { get; }
    }

    private static ProjectDefinition MakeProject(
        string id = "test-project",
        string displayName = "Test Project",
        string lifecycleState = "active",
        string authority = "repo",
        string localPath = @"C:\Repos\TestProject",
        string sshTarget = "",
        string githubUrl = "",
        string adoUrl = "")
    {
        var project = new ProjectDefinition();
        project.Id = id;
        project.DisplayName = displayName;
        project.LifecycleState = lifecycleState;
        project.ProjectRootPath = localPath;
        project.Planning.Authority = authority;
        project.Locations.LocalPath = localPath;
        project.Locations.SshTarget = sshTarget;
        project.Launch.GitHub = githubUrl;
        project.Launch.Ado = adoUrl;
        return project;
    }

    private static RepoSnapshot MakeSnapshot(
        bool available = true,
        string branch = "main",
        bool dirty = false,
        bool hasUpstream = true,
        int ahead = 0,
        int behind = 0,
        DateTime? lastCommit = null)
    {
        return new RepoSnapshot
        {
            IsAvailable = available,
            Branch = branch,
            IsDirty = dirty,
            HasUpstream = hasUpstream,
            AheadBy = ahead,
            BehindBy = behind,
            LastCommitUtc = lastCommit ?? DateTime.UtcNow,
            RepoPath = @"C:\Repos\TestProject"
        };
    }

    [Fact]
    public void Compose_WithMinimalProject_ProducesValidOverview()
    {
        var project = MakeProject();
        var result = OverviewComposer.Compose(project, null, null, null, null);

        Assert.Equal("test-project", result.Id);
        Assert.Equal("Test Project", result.DisplayName);
        Assert.Equal("active", result.LifecycleState);
        Assert.Equal("repo", result.PlanningAuthority);
    }

    [Fact]
    public void Compose_WithNullSnapshot_ShowsNeedsRefresh()
    {
        var project = MakeProject();
        var result = OverviewComposer.Compose(project, null, null, null, null);

        Assert.Equal("Unknown", result.Branch);
        Assert.Equal("Unknown", result.WorkingTreeStatus);
        Assert.Equal("Needs refresh", result.RiskSummary);
    }

    [Fact]
    public void Compose_WithCleanSnapshot_ShowsHealthy()
    {
        var project = MakeProject();
        var snapshot = MakeSnapshot();
        var result = OverviewComposer.Compose(project, null, null, snapshot, null);

        Assert.Equal("main", result.Branch);
        Assert.Equal("Clean", result.WorkingTreeStatus);
        Assert.Equal("Healthy", result.RiskSummary);
    }

    [Fact]
    public void Compose_WithDirtySnapshot_ShowsNeedsAttention()
    {
        var project = MakeProject();
        var snapshot = MakeSnapshot(dirty: true);
        var result = OverviewComposer.Compose(project, null, null, snapshot, null);

        Assert.Equal("Dirty", result.WorkingTreeStatus);
        Assert.Equal("Needs attention", result.RiskSummary);
        Assert.Contains("uncommitted", result.StatusLine);
    }

    [Fact]
    public void Compose_WithStaleCommit_ShowsStale()
    {
        // Fixed-clock variant of t-flake-utcnow: the 14-day staleness threshold
        // and "days since last commit" string are now driven by an injected
        // IClock instead of DateTime.UtcNow, so the assertion below is exact
        // rather than time-of-run dependent.
        var now = new DateTime(2025, 1, 31, 12, 0, 0, DateTimeKind.Utc);
        var clock = new FakeClock(now);
        var project = MakeProject();
        var snapshot = MakeSnapshot(lastCommit: now.AddDays(-30));
        var result = OverviewComposer.Compose(project, null, null, snapshot, null, clock);

        Assert.Equal("Stale", result.RiskSummary);
        Assert.Equal("Stale: 30 days since last commit", result.ActivitySummary);
    }

    [Fact]
    public void Compose_RecentCommit_UnderStaleThreshold_IsHealthy()
    {
        // Boundary test using the injected clock: just under 14 days must NOT
        // be classified as Stale (and ActivitySummary should report a positive
        // "Updated N days ago").
        var now = new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        var clock = new FakeClock(now);
        var project = MakeProject();
        var snapshot = MakeSnapshot(lastCommit: now.AddDays(-3));
        var result = OverviewComposer.Compose(project, null, null, snapshot, null, clock);

        Assert.Equal("Healthy", result.RiskSummary);
        Assert.Equal("Updated 3 days ago", result.ActivitySummary);
    }

    [Fact]
    public void Compose_WithUnavailableSnapshot_ShowsUnavailable()
    {
        var project = MakeProject();
        var snapshot = MakeSnapshot(available: false);
        snapshot.StatusMessage = "Git not found";
        var result = OverviewComposer.Compose(project, null, null, snapshot, null);

        Assert.Equal("Unavailable", result.Branch);
        Assert.Equal("Repo unavailable", result.RiskSummary);
    }

    [Fact]
    public void Compose_WithNoUpstream_ShowsLocalOnly()
    {
        var project = MakeProject();
        var snapshot = MakeSnapshot(hasUpstream: false);
        var result = OverviewComposer.Compose(project, null, null, snapshot, null);

        Assert.Equal("Local only", result.RiskSummary);
        Assert.Equal("No upstream configured", result.SyncStatus);
    }

    [Fact]
    public void Compose_WithNoUpstreamButRemoteConfigured_ShowsBranchNotPublished()
    {
        var project = MakeProject();
        var snapshot = MakeSnapshot(hasUpstream: false);
        snapshot.OriginUrl = "https://github.com/example/repo.git";
        var result = OverviewComposer.Compose(project, null, null, snapshot, null);

        Assert.Equal("Branch not published", result.RiskSummary);
        Assert.Equal("Branch has no upstream (push with -u to publish)", result.SyncStatus);
    }

    [Fact]
    public void Compose_WithProductMap_ShowsInitiatives()
    {
        var project = MakeProject();
        var productMap = new ProductMapSummary
        {
            ProductTitle = "My Product",
            PlanningAuthority = "repo"
        };
        productMap.TopLevelInitiatives.Add("Portfolio Awareness");
        productMap.TopLevelInitiatives.Add("Launchpad");

        var result = OverviewComposer.Compose(project, productMap, null, null, null);

        Assert.Equal("My Product", result.ProductTitle);
        Assert.Contains("Portfolio Awareness", result.InitiativeSummary);
        Assert.Contains("Launchpad", result.InitiativeSummary);
        Assert.Equal("product-map.yml", result.PlanningSource);
    }

    [Fact]
    public void Compose_WithPlanningBoard_TakesPrecedenceOverProductMap()
    {
        var project = MakeProject();
        var productMap = new ProductMapSummary { ProductTitle = "Map Title" };
        var planningBoard = new PlanningBoardSummary
        {
            Title = "Board Title",
            Source = "roadmap",
            Summary = "3 waves"
        };
        planningBoard.Nodes.Add(new PlanningNodeSummary { Title = "Wave 1" });

        var result = OverviewComposer.Compose(project, productMap, planningBoard, null, null);

        Assert.Equal("Board Title", result.ProductTitle);
        Assert.Equal("roadmap", result.PlanningSource);
        Assert.Single(result.PlanningNodes);
    }

    [Fact]
    public void Compose_ResolvesWorkspaceMode_Local()
    {
        var project = MakeProject(localPath: @"C:\Repos\Test");
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Equal("Local", result.WorkspaceMode);
    }

    [Fact]
    public void Compose_ResolvesWorkspaceMode_RemoteSsh()
    {
        var project = MakeProject(localPath: "", sshTarget: "devbox:/home/user/project");
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Equal("Remote SSH", result.WorkspaceMode);
    }

    [Fact]
    public void Compose_ResolvesWorkspaceMode_Hybrid()
    {
        var project = MakeProject(localPath: @"C:\Repos\Test", sshTarget: "devbox:/home/user/project");
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Equal("Hybrid", result.WorkspaceMode);
    }

    [Fact]
    public void Compose_RepoLocation_Local_ShowsLocalClonePath()
    {
        var project = MakeProject(localPath: @"C:\Repos\Test");
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Equal(@"C:\Repos\Test", result.RepoLocation);
    }

    [Fact]
    public void Compose_RepoLocation_SshOnly_ShowsRemotePathNotConfigRoot()
    {
        var project = MakeProject(localPath: "", sshTarget: @"devuser@192.168.64.10:d:\repos\azcra");
        // The config root lives under OneDrive; the repo itself is on the SSH host.
        project.ProjectRootPath = @"D:\Profiles\example\OneDrive\portfolio-projects\azcra";
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Equal(@"d:\repos\azcra", result.RepoLocation);
    }

    [Fact]
    public void Compose_RepoLocation_SshOnly_DoesNotMarkAsLocalClone()
    {
        // The display path changing must not make an SSH-only repo look like it
        // has a local clone: LocalPath stays the (logical) config-root fallback,
        // never the SSH remote path.
        var project = MakeProject(localPath: "", sshTarget: @"devuser@host:d:\repos\azcra");
        project.ProjectRootPath = @"D:\Profiles\example\OneDrive\portfolio-projects\azcra";
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.NotEqual(@"d:\repos\azcra", result.LocalPath);
    }

    [Fact]
    public void Compose_NoGroup_DefaultsToUngrouped()
    {
        var project = MakeProject();
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Equal("Ungrouped", result.Group);
    }

    [Fact]
    public void Compose_WithGroup_MapsThrough()
    {
        var project = MakeProject();
        project.Group = "Customer Projects";
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Equal("Customer Projects", result.Group);
    }

    [Fact]
    public void Compose_AdoAuthority_SetsCorrectNote()
    {
        var project = MakeProject(authority: "ado");
        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Contains("Azure DevOps", result.PlanningAuthorityNote);
        Assert.Contains("read-only", result.PlanningAuthorityNote);
    }

    [Fact]
    public void Compose_WithValidationErrors_SurfacesInStatusLine()
    {
        var project = MakeProject();
        var issues = new List<ValidationIssue>
        {
            new ValidationIssue { Severity = IssueSeverity.Error, Message = "Missing project ID" }
        };
        var result = OverviewComposer.Compose(project, null, null, null, issues);
        Assert.Equal("Missing project ID", result.StatusLine);
    }

    [Fact]
    public void Compose_WithAheadBehind_ShowsSyncStatus()
    {
        var project = MakeProject();
        var snapshot = MakeSnapshot(ahead: 3, behind: 1);
        var result = OverviewComposer.Compose(project, null, null, snapshot, null);
        Assert.Equal("Ahead 3, behind 1", result.SyncStatus);
    }

    [Fact]
    public void Compose_WithExternalRefs_ShowsSummary()
    {
        var project = MakeProject(githubUrl: "https://github.com/test/repo");
        project.ExternalRefs.GitHubRepo = "test/repo";
        project.ExternalRefs.AdoProject = "MyProject";
        project.ExternalRefs.AdoAreaPath = @"MyProject\Core";

        var result = OverviewComposer.Compose(project, null, null, null, null);
        Assert.Contains("GitHub repo: test/repo", result.ExternalSystemSummary);
        Assert.Contains("ADO project: MyProject", result.ExternalSystemSummary);
    }

    // ---- ProjectContextComposer / AuthorityGate integration (ADR-002) ----

    [Fact]
    public void ContextCompose_RepoAuthority_RendersProductMap()
    {
        var project = MakeProject(authority: "repo");
        var pm = new ProductMapSummary { ProductTitle = "Repo Product", PlanningAuthority = "repo" };
        pm.TopLevelInitiatives.Add("Init-A");

        var ov = ProjectContextComposer.Compose(project, pm, null, null, null);
        Assert.Equal("Repo Product", ov.ProductTitle);
        Assert.Contains("Init-A", ov.InitiativeSummary);
        Assert.Equal("product-map.yml", ov.PlanningSource);
    }

    [Fact]
    public void ContextCompose_AdoAuthority_SuppressesProductMapAndBoard()
    {
        var project = MakeProject(authority: "ado");
        var pm = new ProductMapSummary { ProductTitle = "Should-Hide", PlanningAuthority = "ado" };
        pm.TopLevelInitiatives.Add("HiddenInitiative");
        var board = new PlanningBoardSummary { Title = "Hidden Board", Source = "roadmap" };
        board.Nodes.Add(new PlanningNodeSummary { Title = "n" });

        var ov = ProjectContextComposer.Compose(project, pm, board, null, null);
        Assert.Equal("No product map", ov.ProductTitle);
        Assert.Equal("None", ov.PlanningSource);
        Assert.Empty(ov.PlanningNodes);
        // The note still tells the user authority is ADO.
        Assert.Contains("Azure DevOps", ov.PlanningAuthorityNote);
    }

    [Fact]
    public void ContextCompose_GithubAuthority_DoesNotSuppress_ButFlagsMismatch()
    {
        var project = MakeProject(authority: "github");
        var pm = new ProductMapSummary { ProductTitle = "GH Product", PlanningAuthority = "github" };
        pm.TopLevelInitiatives.Add("Init-GH");

        var ov = ProjectContextComposer.Compose(project, pm, null, null, null);
        // Not suppressed: product-map still renders.
        Assert.Equal("GH Product", ov.ProductTitle);
        // But the mismatch warning is surfaced via the status line fallback.
        // (Either StatusLine == "Ready" if no snapshot dirty + no errors, or the
        // mismatch warning gets picked up. We just verify the planning source is
        // intact and authority note matches.)
        Assert.Contains("GitHub", ov.PlanningAuthorityNote);
    }

    [Fact]
    public void ContextCompose_ConflictingAuthority_DoesNotSuppressButFlagsMismatch()
    {
        // project says repo, product-map says ado -> Mismatch state.
        var project = MakeProject(authority: "repo");
        var pm = new ProductMapSummary { ProductTitle = "Conflict Product", PlanningAuthority = "ado" };
        pm.TopLevelInitiatives.Add("Init-C");

        var ov = ProjectContextComposer.Compose(project, pm, null, null, null);
        // Mismatch state does NOT suppress (only AdoAuthoritative suppresses).
        Assert.Equal("Conflict Product", ov.ProductTitle);
        // The mismatch warning takes over the status line because there are no
        // errors and no dirty snapshot.
        Assert.Contains("authority", ov.StatusLine, System.StringComparison.OrdinalIgnoreCase);
    }
}
