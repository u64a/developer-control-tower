using System.Collections.Generic;
using System.IO;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Registration;

namespace ControlTower.Tests;

public class RepoBootstrapServiceTests
{
    [Fact]
    public void DetectMissing_EmptyPortfolio_ReturnsEmpty()
    {
        var svc = BuildService(new List<ProjectRef>());
        var missing = svc.DetectMissing();
        Assert.Empty(missing);
    }

    [Fact]
    public void DetectMissing_ExistingPath_NotMissing()
    {
        var existing = Path.GetTempPath(); // guaranteed to exist
        var refs = new List<ProjectRef>
        {
            new ProjectRef { Id = "exists", Path = existing }
        };
        var svc = BuildService(refs);
        var missing = svc.DetectMissing();
        Assert.Empty(missing);
    }

    [Fact]
    public void DetectMissing_NonexistentPath_ReturnsMissing()
    {
        var refs = new List<ProjectRef>
        {
            new ProjectRef { Id = "gone", Path = @"C:\nonexistent_test_path_1234567890" }
        };
        var svc = BuildService(refs);
        var missing = svc.DetectMissing();
        Assert.Single(missing);
        Assert.Equal("gone", missing[0].ProjectId);
    }

    [Fact]
    public void DetectMissing_SshStore_Skipped()
    {
        var stores = new List<RepoStore>
        {
            new RepoStore { Id = "remote", Type = "ssh", Host = "1.1.1.1", User = "u", Root = "/data" }
        };
        var refs = new List<ProjectRef>
        {
            new ProjectRef { Id = "remote-proj", Path = "1.1.1.1:/data/remote-proj", StoreId = "remote" }
        };
        var svc = BuildService(refs, stores);
        var missing = svc.DetectMissing();
        Assert.Empty(missing);
    }

    [Fact]
    public void Clone_NullProject_Fails()
    {
        var svc = BuildService(new List<ProjectRef>());
        var result = svc.Clone(null);
        Assert.False(result.Success);
    }

    [Fact]
    public void Clone_NoCloneUrl_Fails()
    {
        var svc = BuildService(new List<ProjectRef>());
        var result = svc.Clone(new MissingProject
        {
            ProjectId = "test",
            ExpectedPath = @"C:\temp\test"
        });
        Assert.False(result.Success);
        Assert.Contains("No clone URL", result.Message);
    }

    private static RepoBootstrapService BuildService(
        List<ProjectRef> projects,
        List<RepoStore>? stores = null)
    {
        stores ??= new List<RepoStore> { new RepoStore { Id = "local", Type = "local", Root = @"C:\Repos" } };

        return new RepoBootstrapService(
            new FakePortfolioProvider(projects),
            new FakeProjectProvider(),
            new StoreProvider(stores),
            new FakeSshService(),
            new FakeCredentialStore());
    }

    private sealed class FakePortfolioProvider : IPortfolioProvider
    {
        private readonly PortfolioIndex _portfolio;
        public FakePortfolioProvider(List<ProjectRef> projects)
        {
            _portfolio = new PortfolioIndex();
            foreach (var p in projects) _portfolio.Projects.Add(p);
        }
        public PortfolioIndex LoadPortfolio() => _portfolio;
        public void SavePortfolio(PortfolioIndex portfolio) { /* no-op for tests */ }
    }

    private sealed class FakeProjectProvider : IProjectProvider
    {
        public ProjectLoadResult LoadProject(string projectRootPath) => new ProjectLoadResult();
    }

    private sealed class FakeSshService : ISshService
    {
        public SshResult TestConnection(string host, int port, string user, string password) => SshResult.Ok();
        public SshResult CreateDirectory(string host, int port, string user, string password, string remotePath) => SshResult.Ok();
        public SshResult RunCommand(string host, int port, string user, string password, string command) => SshResult.Ok();
    }

    private sealed class FakeCredentialStore : ICredentialStore
    {
        public string GetPassword(string target) => string.Empty;
        public void SetPassword(string target, string password) { }
        public void DeletePassword(string target) { }
    }
}
