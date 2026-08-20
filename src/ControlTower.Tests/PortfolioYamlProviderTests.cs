using System.IO;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Yaml;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Tests;

public class PortfolioYamlProviderTests
{
    private string WriteTempFile(string content)
    {
        var path = Path.Combine(Path.GetTempPath(), "ct-test-" + Guid.NewGuid().ToString("N") + ".yml");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void LoadPortfolio_ValidFile_ReturnsProjects()
    {
        var yaml = @"schema_version: 0
projects:
  - id: project-one
    path: 'C:\Repos\ProjectOne'
  - id: project-two
    path: 'C:\Repos\ProjectTwo'
";
        var path = WriteTempFile(yaml);
        try
        {
            var provider = new PortfolioYamlProvider(path);
            var result = provider.LoadPortfolio();

            Assert.Equal(2, result.Projects.Count);
            Assert.Equal("project-one", result.Projects[0].Id);
            Assert.Equal("project-two", result.Projects[1].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPortfolio_MissingFile_ReturnsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), "nonexistent-" + Guid.NewGuid().ToString("N") + ".yml");
        var provider = new PortfolioYamlProvider(path);
        var result = provider.LoadPortfolio();

        Assert.NotNull(result);
        Assert.Empty(result.Projects);
    }

    [Fact]
    public void LoadPortfolio_EmptyFile_RaisesValidationIssue()
    {
        var path = WriteTempFile("");
        try
        {
            var provider = new PortfolioYamlProvider(path);
            var result = provider.LoadPortfolio();
            Assert.NotNull(result);
            Assert.Contains(result.Issues, issue =>
                issue.Severity == IssueSeverity.Error &&
                issue.Code == "portfolio/yaml/empty");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPortfolio_DuplicateIds_Deduplicates()
    {
        var yaml = @"schema_version: 0
projects:
  - id: project-one
    path: 'C:\Repos\Same'
  - id: project-one
    path: 'C:\Repos\Different'
";
        var path = WriteTempFile(yaml);
        try
        {
            var provider = new PortfolioYamlProvider(path);
            var result = provider.LoadPortfolio();

            Assert.Single(result.Projects);
            Assert.Equal("project-one", result.Projects[0].Id);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPortfolio_StoreReference_ResolvesPath()
    {
        var yaml = @"schema_version: 1
projects:
  - id: my-project
    store: local
";
        var path = WriteTempFile(yaml);
        try
        {
            var stores = new[] { new RepoStore { Id = "local", Type = "local", Root = @"C:\Repos" } };
            var storeProvider = new StoreProvider(stores);
            var provider = new PortfolioYamlProvider(path, storeProvider);
            var result = provider.LoadPortfolio();

            Assert.Single(result.Projects);
            Assert.Equal("my-project", result.Projects[0].Id);
            Assert.Equal("local", result.Projects[0].StoreId);
            Assert.True(result.Projects[0].UsesStore);
            Assert.Contains("my-project", result.Projects[0].Path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPortfolio_MixedV0AndV1_BothWork()
    {
        var yaml = @"schema_version: 1
projects:
  - id: store-project
    store: local
  - id: legacy-project
    path: 'C:\Legacy\Thing'
";
        var path = WriteTempFile(yaml);
        try
        {
            var stores = new[] { new RepoStore { Id = "local", Type = "local", Root = @"C:\Repos" } };
            var storeProvider = new StoreProvider(stores);
            var provider = new PortfolioYamlProvider(path, storeProvider);
            var result = provider.LoadPortfolio();

            Assert.Equal(2, result.Projects.Count);
            Assert.True(result.Projects[0].UsesStore);
            Assert.False(result.Projects[1].UsesStore);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPortfolio_MalformedYaml_RaisesValidationIssue()
    {
        var bad = "schema_version: 0\nprojects:\n  - id: \"unterminated\n";
        var path = WriteTempFile(bad);
        try
        {
            var provider = new PortfolioYamlProvider(path);
            var result = provider.LoadPortfolio();

            Assert.NotEmpty(result.Issues);
            Assert.Contains(result.Issues, i => i.Code == "portfolio/yaml/malformed");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void LoadPortfolio_TruncatedProject_RaisesValidationIssue()
    {
        var truncated = @"schema_version: 1
projects:
  - id: existing-project
    path:
";
        var path = WriteTempFile(truncated);
        try
        {
            var provider = new PortfolioYamlProvider(path);
            var result = provider.LoadPortfolio();

            Assert.Contains(result.Issues, issue =>
                issue.Severity == IssueSeverity.Error &&
                issue.Code == "portfolio/project/location-missing");
        }
        finally
        {
            File.Delete(path);
        }
    }
}
