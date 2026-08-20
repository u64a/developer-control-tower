using System.IO;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

public class ProjectYamlProviderTests
{
    private string CreateTempProjectDir(string projectYaml)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-test-" + Guid.NewGuid().ToString("N"));
        var metaDir = Path.Combine(dir, ".controltower");
        Directory.CreateDirectory(metaDir);
        File.WriteAllText(Path.Combine(metaDir, "project.yml"), projectYaml);
        return dir;
    }

    [Fact]
    public void LoadProject_ValidFile_ReturnsProjectDefinition()
    {
        var yaml = @"kind: developer-control-tower/project
schema_version: 0

id: my-project
display_name: My Project
summary: A test project
lifecycle_state: active

planning:
  authority: repo
  source_ref: .controltower\product-map.yml

locations:
  local_path: C:\Repos\MyProject
  ssh_target:
  remote_url: https://github.com/test/repo
";
        var dir = CreateTempProjectDir(yaml);
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            Assert.Equal("my-project", result.Project.Id);
            Assert.Equal("My Project", result.Project.DisplayName);
            Assert.Equal("A test project", result.Project.Summary);
            Assert.Equal("active", result.Project.LifecycleState);
            Assert.Equal("repo", result.Project.Planning.Authority);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadProject_SeparateWorkingAndMetadataRoots_ReadsMetadataFromStub_KeepsWorkingRoot()
    {
        // Metadata lives in the central stub; the working tree is a different repo.
        var yaml = @"kind: developer-control-tower/project
schema_version: 0

id: split-project
display_name: Split Project
lifecycle_state: active

locations:
  local_path: C:\Repos\SplitProject
";
        var metadataRoot = CreateTempProjectDir(yaml);
        var workingRoot = Path.Combine(Path.GetTempPath(), "ct-working-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workingRoot); // deliberately has NO .controltower
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(workingRoot, metadataRoot);

            // Identity/metadata came from the stub, not the (empty) working tree.
            Assert.Equal("split-project", result.Project.Id);
            Assert.Equal("Split Project", result.Project.DisplayName);
            // ProjectRootPath tracks the working tree (drives launch / roadmap).
            Assert.Equal(workingRoot, result.Project.ProjectRootPath);
            // Explicit yaml local_path still wins for the repo location.
            Assert.Equal(@"C:\Repos\SplitProject", result.Project.Locations.LocalPath);
        }
        finally
        {
            Directory.Delete(metadataRoot, true);
            Directory.Delete(workingRoot, true);
        }
    }

    [Fact]
    public void LoadProject_StubEmpty_FallsBackToLegacyInRepoMetadata()
    {
        // Not-yet-migrated project: metadata still lives in the repo, stub is empty.
        var yaml = @"kind: developer-control-tower/project
schema_version: 0

id: legacy-project
display_name: Legacy Project
lifecycle_state: active
";
        var workingRoot = CreateTempProjectDir(yaml); // in-repo .controltower
        var emptyStub = Path.Combine(Path.GetTempPath(), "ct-stub-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(emptyStub); // no .controltower
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(workingRoot, emptyStub);

            Assert.Equal("legacy-project", result.Project.Id);
            Assert.Equal("Legacy Project", result.Project.DisplayName);
        }
        finally
        {
            Directory.Delete(workingRoot, true);
            Directory.Delete(emptyStub, true);
        }
    }

    [Fact]
    public void LoadProject_MissingFile_ReturnsValidationError()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-test-missing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            Assert.True(result.Issues.Count > 0);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadProject_WithExternalRefs_ParsesCorrectly()
    {
        var yaml = @"kind: developer-control-tower/project
schema_version: 0

id: ref-project
display_name: Ref Project

external_refs:
  github:
    repo: owner/repo
    default_branch: main
  ado:
    organization: myorg
    project: myproj
    area_path: myproj\core
";
        var dir = CreateTempProjectDir(yaml);
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            Assert.Equal("owner/repo", result.Project.ExternalRefs.GitHubRepo);
            Assert.Equal("main", result.Project.ExternalRefs.GitHubDefaultBranch);
            Assert.Equal("myorg", result.Project.ExternalRefs.AdoOrganization);
            Assert.Equal("myproj", result.Project.ExternalRefs.AdoProject);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadProject_MalformedYaml_AddsValidationIssue()
    {
        var bad = "kind: developer-control-tower/project\nschema_version: 0\nid: \"unterminated\n";
        var dir = CreateTempProjectDir(bad);
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            Assert.NotEmpty(result.Issues);
            Assert.Contains(result.Issues, i => i.Code == "project/yaml/malformed");
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadProject_WithDocs_ParsesDocLinks()
    {
        var yaml = @"kind: developer-control-tower/project
schema_version: 0

id: doc-project
display_name: Doc Project

docs:
  - id: spec
    title: V0 Spec
    kind: spec
    url: spec\v0-spec.md
";
        var dir = CreateTempProjectDir(yaml);
        try
        {
            var provider = new ProjectYamlProvider();
            var result = provider.LoadProject(dir);

            Assert.Single(result.Project.Docs);
            Assert.Equal("spec", result.Project.Docs[0].Id);
            Assert.Equal("V0 Spec", result.Project.Docs[0].Title);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
