using System.IO;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

public class ProductMapYamlProviderTests
{
    private string CreateTempProjectDir(string productMapYaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-test-" + Guid.NewGuid().ToString("N"));
        var metaDir = Path.Combine(dir, ".controltower");
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, "product-map.yml"), productMapYaml);
        return dir;
    }

    [Fact]
    public void LoadProductMap_ValidFile_ReturnsProductAndInitiatives()
    {
        var yaml = @"kind: developer-control-tower/product-map
schema_version: 0

project_id: test-project
planning_authority: repo

nodes:
  - id: product.my-tool
    type: product
    title: My Tool
    parent_id: null
    status: active
    description: A great tool.

  - id: initiative.feature-a
    type: initiative
    title: Feature A
    parent_id: product.my-tool
    status: active
    description: First initiative.

  - id: initiative.feature-b
    type: initiative
    title: Feature B
    parent_id: product.my-tool
    status: proposed
    description: Second initiative.
";
        var dir = CreateTempProjectDir(yaml);
        try
        {
            var provider = new ProductMapYamlProvider();
            var result = provider.LoadProductMap(dir, @".controltower\product-map.yml");

            Assert.Equal("My Tool", result.Summary.ProductTitle);
            Assert.Equal("repo", result.Summary.PlanningAuthority);
            Assert.Equal(2, result.Summary.TopLevelInitiatives.Count);
            Assert.Contains("Feature A", result.Summary.TopLevelInitiatives);
            Assert.Contains("Feature B", result.Summary.TopLevelInitiatives);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadProductMap_MissingFile_ReturnsEmptySummary()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-test-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var provider = new ProductMapYamlProvider();
            var result = provider.LoadProductMap(dir, @".controltower\product-map.yml");

            Assert.NotNull(result.Summary);
            Assert.Empty(result.Summary.TopLevelInitiatives);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadProductMap_NoProductNode_ReportsIssue()
    {
        var yaml = @"kind: developer-control-tower/product-map
schema_version: 0

project_id: test-project
planning_authority: repo

nodes:
  - id: initiative.orphan
    type: initiative
    title: Orphan Initiative
    parent_id: null
    status: active
";
        var dir = CreateTempProjectDir(yaml);
        try
        {
            var provider = new ProductMapYamlProvider();
            var result = provider.LoadProductMap(dir, @".controltower\product-map.yml");

            Assert.True(result.Issues.Count > 0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
