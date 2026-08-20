#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Core.Validation;

namespace ControlTower.Tests;

/// <summary>
/// Filesystem-backed tests for <see cref="RepoScanService"/>. Uses a fake
/// <see cref="IGitWorkspaceInspector"/> so the scanner is exercised
/// without any real <c>git.exe</c> dependency.
/// </summary>
public class RepoScanServiceTests : IDisposable
{
    private readonly string _root;

    public RepoScanServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-scan-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // --- Helpers ------------------------------------------------------------

    private string MakeWorkingTreeMarker(string subdir, string? originUrl = null, string? branch = null)
    {
        var p = Path.Combine(_root, subdir);
        Directory.CreateDirectory(p);
        // Just the marker — the fake inspector decides what to return.
        Directory.CreateDirectory(Path.Combine(p, ".git"));
        if (originUrl != null || branch != null)
        {
            // Stash sidecar info we can pick up from the fake inspector
            // by reading these files. Real git would store this in config.
            if (originUrl != null) File.WriteAllText(Path.Combine(p, ".git", "_origin"), originUrl);
            if (branch != null) File.WriteAllText(Path.Combine(p, ".git", "_branch"), branch);
        }
        return p;
    }

    private string MakeBareMarker(string subdir)
    {
        var p = Path.Combine(_root, subdir);
        Directory.CreateDirectory(p);
        File.WriteAllText(Path.Combine(p, "HEAD"), "ref: refs/heads/main\n");
        Directory.CreateDirectory(Path.Combine(p, "objects"));
        Directory.CreateDirectory(Path.Combine(p, "refs"));
        return p;
    }

    private static RepoScanService NewScanner(
        IGitWorkspaceInspector inspector,
        PortfolioIndex? portfolio = null,
        Dictionary<string, ProjectDefinition>? projectsByPath = null)
    {
        return new RepoScanService(
            inspector,
            new FakePortfolioProvider(portfolio ?? new PortfolioIndex()),
            new FakeProjectProvider(projectsByPath ?? new Dictionary<string, ProjectDefinition>(StringComparer.OrdinalIgnoreCase)));
    }

    // --- Tests --------------------------------------------------------------

    [Fact]
    public async Task ScanAsync_DepthBound_DeepRepoNotWalked()
    {
        // depth 4 from root; default MaxDepth=3 -> should not be discovered.
        var deep = Path.Combine(_root, "a", "b", "c", "d", "deep-repo");
        Directory.CreateDirectory(deep);
        Directory.CreateDirectory(Path.Combine(deep, ".git"));
        var inspector = new FakeInspector(_ => new WorkingTreeRepo(deep, deep, "main", false, false, false, false, false, false, null, Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector).ScanAsync(
            new[] { _root }, new ScanOptions(MaxDepth: 3), progress: null, ct: CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ScanAsync_SkipListHonoured()
    {
        // Place a "repo" inside each blocked folder name, all under a wrap
        // folder so that no folder at or above the skip-listed name carries
        // a .git marker (which would short-circuit the walker before it ever
        // reached the skip-list check at the child-enqueue step).
        // Note: .git itself is omitted from this matrix because its skip
        // semantics only matter inside a discovered repo's working tree —
        // see RepoScanService line 276 where we never descend into a repo.
        var wrap = Path.Combine(_root, "wrap");
        Directory.CreateDirectory(wrap);
        foreach (var name in new[] { "node_modules", "bin", "obj" })
        {
            var p = Path.Combine(wrap, name, "fake-repo");
            Directory.CreateDirectory(p);
            Directory.CreateDirectory(Path.Combine(p, ".git"));
        }

        var inspector = new FakeInspector(path =>
            new WorkingTreeRepo(path, path, "main", false, false, false, false, false, false, null, Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task ScanAsync_BareRepoDetected()
    {
        var bare = MakeBareMarker("my-bare");
        var inspector = new FakeInspector(_ => new BareRepo(bare, bare, Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal(RepoKind.BareRepo, c.Kind);
    }

    [Fact]
    public async Task ScanAsync_WorkingTreeNoRemote_ClassifiedNoRemote()
    {
        var path = MakeWorkingTreeMarker("no-remote");
        var inspector = new FakeInspector(_ =>
            new WorkingTreeRepo(path, path, "main", false, false, false, false, false, false, null, Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal(RepoKind.WorkingTree, c.Kind);
        Assert.Equal(RemoteState.NoRemote, c.RemoteState);
        Assert.Equal(string.Empty, c.DisplayOriginUrl);
    }

    [Fact]
    public async Task ScanAsync_ExcludedCanonicalProject_IsSkippedBeforeGitInspection()
    {
        var includedPath = MakeWorkingTreeMarker("included");
        var excludedPath = MakeWorkingTreeMarker("excluded");
        var inspector = new FakeInspector(path =>
            new WorkingTreeRepo(
                path,
                path,
                "main",
                false,
                false,
                false,
                false,
                false,
                false,
                null,
                Array.Empty<GitRemote>()));
        var portfolio = new PortfolioIndex();
        portfolio.Projects.Add(new ProjectRef { Id = "included", Path = includedPath });
        portfolio.Projects.Add(new ProjectRef { Id = "excluded", Path = excludedPath });
        var active = new WorkspaceProfile { Id = Guid.NewGuid(), Name = "Focused" };
        active.Members.Add("INCLUDED");
        var projectProvider = new FakeProjectProvider(
            new Dictionary<string, ProjectDefinition>(StringComparer.OrdinalIgnoreCase));
        var scanner = new RepoScanService(
            inspector,
            new FakePortfolioProvider(portfolio),
            projectProvider,
            metadataLocator: null,
            activeProfile: active);

        var result = await scanner.ScanAsync(
            new[] { _root },
            new ScanOptions(),
            progress: null,
            ct: CancellationToken.None);

        var candidate = Assert.Single(result.Candidates);
        Assert.Equal(includedPath, candidate.FolderPath);
        Assert.Equal(1, inspector.ClassifyCount);
        Assert.Equal(new[] { includedPath }, projectProvider.LoadedPaths);
    }

    [Fact]
    public async Task ScanAsync_WorkingTreeWithOrigin_HasIdentity()
    {
        var path = MakeWorkingTreeMarker("with-origin");
        var inspector = new FakeInspector(_ =>
            new WorkingTreeRepo(
                path, path, "main", false, false, false, false, false, false,
                "https://github.com/org/repo.git", Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal(RemoteState.HasOrigin, c.RemoteState);
        Assert.Equal("https://github.com/org/repo.git", c.DisplayOriginUrl);
        Assert.Equal("github.com/org/repo", c.DedupeIdentity);
    }

    [Fact]
    public async Task ScanAsync_WorkingTreeWithCredentialUrl_StripsAndFlags()
    {
        var path = MakeWorkingTreeMarker("creds");
        var inspector = new FakeInspector(_ =>
            new WorkingTreeRepo(
                path, path, "main", false, false, false, false, false, false,
                "https:/" + "/user:token@github.com/org/repo.git", Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal(RemoteState.OriginHasCredentials, c.RemoteState);
        Assert.Equal("https://github.com/org/repo.git", c.DisplayOriginUrl);
        Assert.Equal("https:/" + "/user:token@github.com/org/repo.git", c.RawOriginUrl);
    }

    [Fact]
    public async Task ScanAsync_DedupeByPath_CaseInsensitive()
    {
        var path = MakeWorkingTreeMarker("dup-path");
        var inspector = new FakeInspector(_ =>
            new WorkingTreeRepo(path, path, "main", false, false, false, false, false, false,
                null, Array.Empty<GitRemote>()));

        // Existing portfolio carries the same path with different case.
        var portfolio = new PortfolioIndex();
        portfolio.Projects.Add(new ProjectRef { Id = "existing", Path = path.ToUpperInvariant() });

        var result = await NewScanner(inspector, portfolio).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal(DuplicateKind.Path, c.DuplicateKind);
        Assert.Equal("existing", c.DuplicateOfProjectId);
    }

    [Fact]
    public async Task ScanAsync_DedupeByOriginIdentity_AcrossSchemeForms()
    {
        var path = MakeWorkingTreeMarker("dup-origin");
        var inspector = new FakeInspector(_ =>
            new WorkingTreeRepo(path, path, "main", false, false, false, false, false, false,
                "git@github.com:org/repo.git", Array.Empty<GitRemote>()));

        var portfolio = new PortfolioIndex();
        // Existing portfolio remote is in https form — must still match.
        portfolio.Projects.Add(new ProjectRef
        {
            Id = "existing-https",
            Path = Path.Combine(_root, "somewhere-else"),
            RemoteUrl = "https://github.com/org/repo.git"
        });

        var result = await NewScanner(inspector, portfolio).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal(DuplicateKind.Origin, c.DuplicateKind);
        Assert.Equal("existing-https", c.DuplicateOfProjectId);
    }

    [Fact]
    public async Task ScanAsync_InScanDuplicate_SecondOccurrenceMarked()
    {
        // Two repos with the same origin but different paths under one root.
        var a = MakeWorkingTreeMarker("a-clone");
        var b = MakeWorkingTreeMarker("b-clone");

        var inspector = new FakeInspector(p =>
            new WorkingTreeRepo(p, p, "main", false, false, false, false, false, false,
                "https://github.com/org/same.git", Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        Assert.Equal(2, result.Candidates.Count);
        // BFS order is filesystem-dependent — assert one is original, one is duplicate.
        int firstCount = result.Candidates.Count(c => c.DuplicateKind == DuplicateKind.None);
        int dupCount = result.Candidates.Count(c => c.DuplicateKind == DuplicateKind.Origin);
        Assert.Equal(1, firstCount);
        Assert.Equal(1, dupCount);
    }

    [Fact]
    public async Task ScanAsync_SlugCollision_AppendsHyphenOne()
    {
        var path = MakeWorkingTreeMarker("projectmgr");
        var inspector = new FakeInspector(_ =>
            new WorkingTreeRepo(path, path, "main", false, false, false, false, false, false,
                null, Array.Empty<GitRemote>()));

        var portfolio = new PortfolioIndex();
        portfolio.Projects.Add(new ProjectRef
        {
            Id = "projectmgr",
            Path = Path.Combine(_root, "elsewhere-projectmgr")
        });

        var result = await NewScanner(inspector, portfolio).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal("projectmgr-1", c.SuggestedSlug);
    }

    [Fact]
    public async Task ScanAsync_AccessDenied_SurfacesAsIssueAndContinues()
    {
        // Construct an inspector that wouldn't matter — we'll cause the
        // enumeration to fail by passing a non-existent root which falls
        // into the IOError bucket.
        var inspector = new FakeInspector(_ => new NotARepo(""));
        var missing = Path.Combine(_root, "does", "not", "exist");

        var path = MakeWorkingTreeMarker("present");
        var inspector2 = new FakeInspector(p =>
            new WorkingTreeRepo(p, p, "main", false, false, false, false, false, false,
                null, Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector2).ScanAsync(
            new[] { missing, _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        Assert.Contains(result.Issues, i => i.Kind == ScanIssueKind.IOError);
        // Scan must continue past the bad root and still find the present one.
        Assert.Single(result.Candidates);
    }

    [Fact]
    public async Task ScanAsync_Cancellation_ThrowsOperationCanceled()
    {
        // Plant several non-repo folders so the walker has work to chew on.
        for (int i = 0; i < 20; i++)
        {
            Directory.CreateDirectory(Path.Combine(_root, "dir-" + i));
        }
        var inspector = new FakeInspector(_ => new NotARepo(""));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            NewScanner(inspector).ScanAsync(
                new[] { _root }, new ScanOptions(), progress: null, ct: cts.Token));
    }

    [Fact]
    public async Task ScanAsync_NestedRepoInsideRepo_NotSurfaced()
    {
        var outer = MakeWorkingTreeMarker("outer");
        var innerRel = Path.Combine(outer, "inner");
        Directory.CreateDirectory(innerRel);
        Directory.CreateDirectory(Path.Combine(innerRel, ".git"));

        var inspector = new FakeInspector(p =>
            new WorkingTreeRepo(p, p, "main", false, false, false, false, false, false,
                null, Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal(outer, c.FolderPath);
    }

    [Fact]
    public async Task ScanAsync_LegacyProjectWithoutPortfolioRemoteUrl_FallsBackToProjectYaml()
    {
        // Portfolio entry has no RemoteUrl on the row, but its on-disk
        // project.yml carries an origin — scanner must read that for dedupe.
        var existingPath = Path.Combine(_root, "legacy-existing");
        Directory.CreateDirectory(existingPath);

        var portfolio = new PortfolioIndex();
        portfolio.Projects.Add(new ProjectRef
        {
            Id = "legacy",
            Path = existingPath,
            RemoteUrl = string.Empty
        });

        var projectsByPath = new Dictionary<string, ProjectDefinition>(StringComparer.OrdinalIgnoreCase);
        var legacyProject = new ProjectDefinition { Id = "legacy" };
        legacyProject.Locations.RemoteUrl = "https://github.com/org/legacy.git";
        projectsByPath[existingPath] = legacyProject;

        // Now scan another folder with the same origin.
        var newPath = MakeWorkingTreeMarker("freshly-cloned");
        var inspector = new FakeInspector(p =>
            new WorkingTreeRepo(p, p, "main", false, false, false, false, false, false,
                "git@github.com:org/legacy.git", Array.Empty<GitRemote>()));

        var result = await NewScanner(inspector, portfolio, projectsByPath).ScanAsync(
            new[] { _root }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        var c = Assert.Single(result.Candidates);
        Assert.Equal(DuplicateKind.Origin, c.DuplicateKind);
        Assert.Equal("legacy", c.DuplicateOfProjectId);
    }

    [Fact]
    public async Task ScanAsync_ReparsePoint_SkippedByDefault()
    {
        // Skip cleanly if junctions aren't supported in this environment.
        var target = MakeWorkingTreeMarker("real-target");
        var junctionParent = Path.Combine(_root, "junction-parent");
        Directory.CreateDirectory(junctionParent);
        var junction = Path.Combine(junctionParent, "junction");

        if (!TryCreateJunction(junction, target))
        {
            // Couldn't create a junction (e.g. permission denied) — bail out.
            return;
        }

        // Inspector returns a repo for any path it's asked about.
        var inspector = new FakeInspector(p =>
            new WorkingTreeRepo(p, p, "main", false, false, false, false, false, false,
                null, Array.Empty<GitRemote>()));

        // Scan only the junction-parent. The real target is outside it, so
        // if we *follow* the junction we'd surface a repo; if we skip
        // reparse points we get none.
        var result = await NewScanner(inspector).ScanAsync(
            new[] { junctionParent }, new ScanOptions(), progress: null, ct: CancellationToken.None);

        Assert.Empty(result.Candidates);
    }

    // --- Fakes --------------------------------------------------------------

    private sealed class FakeInspector : IGitWorkspaceInspector
    {
        private readonly Func<string, GitWorkspaceClassification> _classify;
        public FakeInspector(Func<string, GitWorkspaceClassification> classify)
        {
            _classify = classify;
        }

        public int ClassifyCount { get; private set; }

        public Task<GitWorkspaceClassification> ClassifyAsync(string path, CancellationToken ct)
        {
            ClassifyCount++;
            return Task.FromResult(_classify(path));
        }

        public Task<GitStatusBuckets> ReadStatusAsync(string workingTreePath, CancellationToken ct)
            => Task.FromResult(new GitStatusBuckets(
                Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(),
                Array.Empty<string>(), null, null));

        public string CanonicalizeRemote(string remote)
        {
            // Use the real implementation's algorithm faithfully enough for
            // dedupe to work in tests.
            var inspector = new ControlTower.Infrastructure.Git.GitWorkspaceInspector(
                new ControlTower.Infrastructure.Git.GitProcessAdapter(
                    new ControlTower.Infrastructure.Configuration.ToolSettings()));
            return inspector.CanonicalizeRemote(remote);
        }

        public string GetRemoteIdentity(string remote)
        {
            var inspector = new ControlTower.Infrastructure.Git.GitWorkspaceInspector(
                new ControlTower.Infrastructure.Git.GitProcessAdapter(
                    new ControlTower.Infrastructure.Configuration.ToolSettings()));
            return inspector.GetRemoteIdentity(remote);
        }
    }

    private sealed class FakePortfolioProvider : IPortfolioProvider
    {
        private readonly PortfolioIndex _portfolio;
        public FakePortfolioProvider(PortfolioIndex portfolio) { _portfolio = portfolio; }
        public PortfolioIndex LoadPortfolio() => _portfolio;
        public void SavePortfolio(PortfolioIndex portfolio) { /* no-op for scan tests */ }
    }

    private sealed class FakeProjectProvider : IProjectProvider
    {
        private readonly Dictionary<string, ProjectDefinition> _byPath;
        public FakeProjectProvider(Dictionary<string, ProjectDefinition> byPath) { _byPath = byPath; }
        public int LoadCount { get; private set; }
        public List<string> LoadedPaths { get; } = new();
        public ProjectLoadResult LoadProject(string projectRootPath)
        {
            LoadCount++;
            LoadedPaths.Add(projectRootPath);
            if (_byPath.TryGetValue(projectRootPath, out var def))
            {
                return new ProjectLoadResult { Project = def };
            }
            return new ProjectLoadResult();
        }
    }

    private static bool TryCreateJunction(string junction, string target)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{junction}\" \"{target}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return false;
            proc.WaitForExit(5_000);
            return proc.ExitCode == 0 && Directory.Exists(junction);
        }
        catch
        {
            return false;
        }
    }
}
