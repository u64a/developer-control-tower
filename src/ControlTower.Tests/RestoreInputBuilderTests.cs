using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

/// <summary>
/// Exercises <see cref="RestoreInputBuilder"/> against the real project
/// YAML provider and metadata layout: metadata under
/// <c>{configRoot}\portfolio-projects\{id}</c>, working trees elsewhere
/// and (as after a machine rebuild) absent.
/// </summary>
public class RestoreInputBuilderTests : IDisposable
{
    private readonly string _root;
    private readonly string _configRoot;
    private readonly string _repos;
    private readonly PortfolioIndex _portfolio = new();
    private readonly FakeSnapshotStore _snapshots = new();

    public RestoreInputBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-restore-inputs-" + Guid.NewGuid().ToString("N"));
        _configRoot = Path.Combine(_root, "config");
        _repos = Path.Combine(_root, "repos");
        Directory.CreateDirectory(_configRoot);
        Directory.CreateDirectory(_repos);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Build_RepoFolderGone_ReadsRemoteUrlFromCentralMetadata()
    {
        var path = AddLocalProject("alpha", cachedRemoteUrl: "");
        WriteMetadata("alpha", localPath: path, remoteUrl: "https://github.com/org/alpha.git");

        var input = Assert.Single(NewBuilder().Build());

        Assert.Equal(path, input.ExpectedPath);
        Assert.Equal("https://github.com/org/alpha.git", input.RemoteUrl);
        Assert.Equal(RestoreUrlSource.ProjectMetadata, input.RemoteUrlSource);
        Assert.Equal("Display alpha", input.ProjectName);
        Assert.Equal(string.Empty, input.CachedRemoteUrl);
    }

    [Fact]
    public void Build_PortfolioCacheWinsOverMetadata()
    {
        var path = AddLocalProject("alpha", cachedRemoteUrl: "git@github.com:org/alpha.git");
        WriteMetadata("alpha", localPath: path, remoteUrl: "https://github.com/org/alpha");

        var input = Assert.Single(NewBuilder().Build());

        Assert.Equal("git@github.com:org/alpha.git", input.RemoteUrl);
        Assert.Equal(RestoreUrlSource.PortfolioCache, input.RemoteUrlSource);
    }

    [Fact]
    public void Build_FallsBackToLastScanThenLaunchLink_SkippingUnusableValues()
    {
        var scanned = AddLocalProject("scanned", cachedRemoteUrl: "https://user:secret@github.com/org/scanned.git");
        WriteMetadata("scanned", localPath: scanned, remoteUrl: "", gitHub: "https://github.com/org/scanned");
        _snapshots.Snapshots["scanned"] = new RepoSnapshot { OriginUrl = "https://github.com/org/scanned.git" };

        var linked = AddLocalProject("linked", cachedRemoteUrl: "");
        WriteMetadata("linked", localPath: linked, remoteUrl: "", gitHub: "https://github.com/org/linked");
        _snapshots.Snapshots["linked"] = new RepoSnapshot { OriginUrl = "No remote configured" };

        var unknown = AddLocalProject("unknown", cachedRemoteUrl: "");
        WriteMetadata("unknown", localPath: unknown, remoteUrl: "");

        var inputs = NewBuilder().Build().ToDictionary(i => i.ProjectId);

        Assert.Equal("https://github.com/org/scanned.git", inputs["scanned"].RemoteUrl);
        Assert.Equal(RestoreUrlSource.LastScan, inputs["scanned"].RemoteUrlSource);
        Assert.Equal("https://github.com/org/linked", inputs["linked"].RemoteUrl);
        Assert.Equal(RestoreUrlSource.LaunchLink, inputs["linked"].RemoteUrlSource);
        Assert.Equal(string.Empty, inputs["unknown"].RemoteUrl);
        Assert.Equal(RestoreUrlSource.None, inputs["unknown"].RemoteUrlSource);
    }

    [Fact]
    public void Build_EntryPointingAtMetadataStub_IsNotARestoreCandidate()
    {
        // SSH-only projects are registered with the stub as their path.
        var stub = new ProjectMetadataLocator(_configRoot).ResolveMetadataRoot("remote-only");
        _portfolio.Projects.Add(new ProjectRef { Id = "remote-only", Path = stub });
        WriteMetadata("remote-only", localPath: "", remoteUrl: "https://github.com/org/remote-only.git",
            sshTarget: "devbox:/srv/remote-only");

        Assert.Empty(NewBuilder().Build());
    }

    [Fact]
    public void SelectOriginsToCache_CachesNewTrustedOriginsOnly()
    {
        var inputs = new[]
        {
            Input("from-metadata", cached: ""),
            Input("unchanged", cached: "https://github.com/org/unchanged.git"),
            Input("guess", cached: ""),
            Input("secret", cached: ""),
            Input("live-changed", cached: "https://github.com/org/old.git"),
        };
        var candidates = new[]
        {
            Candidate("from-metadata", "https://github.com/org/m.git", RestoreUrlSource.ProjectMetadata),
            Candidate("unchanged", "https://github.com/org/unchanged.git", RestoreUrlSource.PortfolioCache),
            Candidate("guess", "https://github.com/org/guess", RestoreUrlSource.LaunchLink),
            Candidate("secret", "https://user:pw@github.com/org/s.git", RestoreUrlSource.LiveRepository),
            Candidate("live-changed", "https://github.com/org/new.git", RestoreUrlSource.LiveRepository),
        };

        var toCache = RestoreInputBuilder.SelectOriginsToCache(inputs, candidates);

        Assert.Equal(
            new[] { "from-metadata", "live-changed" },
            toCache.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal("https://github.com/org/new.git", toCache["live-changed"]);
    }

    private RestoreInputBuilder NewBuilder() => new(
        new FakePortfolioProvider(_portfolio),
        new ProjectYamlProvider(),
        new NoStores(),
        new ProjectMetadataLocator(_configRoot),
        _snapshots);

    private string AddLocalProject(string id, string cachedRemoteUrl)
    {
        var path = Path.Combine(_repos, id);
        _portfolio.Projects.Add(new ProjectRef { Id = id, Path = path, RemoteUrl = cachedRemoteUrl });
        return path;
    }

    private void WriteMetadata(
        string id, string localPath, string remoteUrl, string gitHub = "", string sshTarget = "")
    {
        var folder = Path.Combine(
            new ProjectMetadataLocator(_configRoot).ResolveMetadataRoot(id), ".controltower");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "project.yml"), new[]
        {
            "kind: developer-control-tower/project",
            "schema_version: 0",
            "id: " + id,
            "display_name: 'Display " + id + "'",
            "locations:",
            "  local_path: '" + localPath + "'",
            "  ssh_target: '" + sshTarget + "'",
            "  remote_url: '" + remoteUrl + "'",
            "launch:",
            "  github: '" + gitHub + "'",
        });
    }

    private static ProjectRestoreInput Input(string id, string cached) => new(
        ProjectId: id, ProjectName: id, Slug: id, ExpectedPath: @"C:\repos\" + id,
        RemoteUrl: cached, IsLocalStore: true, CachedRemoteUrl: cached);

    private static RestoreCandidate Candidate(string id, string url, RestoreUrlSource source) => new(
        ProjectId: id, ProjectName: id, Slug: id, ExpectedPath: @"C:\repos\" + id,
        RemoteUrl: url, CanonicalRemoteUrl: url,
        Classification: RestoreClassification.Missing, Detail: string.Empty,
        RemoteUrlSource: source);

    private sealed class FakePortfolioProvider : IPortfolioProvider
    {
        private readonly PortfolioIndex _portfolio;
        public FakePortfolioProvider(PortfolioIndex portfolio) { _portfolio = portfolio; }
        public PortfolioIndex LoadPortfolio() => _portfolio;
        public void SavePortfolio(PortfolioIndex portfolio) { }
    }

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public Dictionary<string, RepoSnapshot> Snapshots { get; } = new();
        public RepoSnapshot Load(string projectId) =>
            Snapshots.TryGetValue(projectId, out var snapshot) ? snapshot : null!;
        public void Save(string projectId, RepoSnapshot snapshot) => Snapshots[projectId] = snapshot;
    }

    private sealed class NoStores : IStoreProvider
    {
        public IReadOnlyList<RepoStore> GetStores() => Array.Empty<RepoStore>();
        public RepoStore GetStore(string storeId) => null!;
        public string ResolveProjectPath(string storeId, string projectId, string folder) => string.Empty;
    }
}
