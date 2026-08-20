using System;
using System.IO;
using System.Text;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;
using ControlTower.Infrastructure.Registration;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

/// <summary>
/// Phase C registration semantics: RemoteUrl persistence, AllowOverwrite
/// gating of duplicate ids, and the credential-bearing URL pre-flight
/// reject.
/// </summary>
public class ProjectRegistrationServiceTests : IDisposable
{
    private readonly string _root;
    private readonly string _portfolioPath;

    public ProjectRegistrationServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-reg-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _portfolioPath = Path.Combine(_root, "portfolio.yml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void RegisterProject_RemoteUrlSet_PortfolioAndProjectYamlPopulated()
    {
        var localPath = Path.Combine(_root, "p1");
        Directory.CreateDirectory(localPath);

        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "p1",
            DisplayName = "Project One",
            LocalPath = localPath,
            RemoteUrl = "https://github.com/org/p1.git"
        });

        Assert.True(result.Success);

        var portfolio = new PortfolioYamlProvider(_portfolioPath).LoadPortfolio();
        var entry = Assert.Single(portfolio.Projects);
        Assert.Equal("p1", entry.Id);
        Assert.Equal("https://github.com/org/p1.git", entry.RemoteUrl);

        var projectYaml = File.ReadAllText(
            Path.Combine(_root, "portfolio-projects", "p1", ".controltower", "project.yml"));
        Assert.Contains("remote_url:", projectYaml);
        Assert.Contains("github.com/org/p1.git", projectYaml);

        // Metadata must NOT be written into the target repo any more.
        Assert.False(Directory.Exists(Path.Combine(localPath, ".controltower")));
    }

    [Fact]
    public void RegisterProject_DuplicateIdWithoutOverwrite_Rejected()
    {
        var path1 = Path.Combine(_root, "p1");
        var path2 = Path.Combine(_root, "p2");
        Directory.CreateDirectory(path1);
        Directory.CreateDirectory(path2);

        var svc = new ProjectRegistrationService(_portfolioPath);
        var first = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "p1",
            DisplayName = "First",
            LocalPath = path1,
            RemoteUrl = "https://github.com/org/first.git"
        });
        Assert.True(first.Success);

        var second = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "p1",
            DisplayName = "Second",
            LocalPath = path2,
            RemoteUrl = "https://github.com/org/second.git",
            AllowOverwrite = false
        });

        Assert.False(second.Success);
        Assert.Contains("already registered", second.Message);

        var portfolio = new PortfolioYamlProvider(_portfolioPath).LoadPortfolio();
        var entry = Assert.Single(portfolio.Projects);
        // The first entry must be intact — second was rejected before any mutation.
        Assert.Equal(path1, entry.Path);
        Assert.Equal("https://github.com/org/first.git", entry.RemoteUrl);
    }

    [Fact]
    public void RegisterProject_DuplicateIdWithOverwrite_UpdatesEntry()
    {
        var path1 = Path.Combine(_root, "p1");
        var path2 = Path.Combine(_root, "p1-moved");
        Directory.CreateDirectory(path1);
        Directory.CreateDirectory(path2);

        var svc = new ProjectRegistrationService(_portfolioPath);
        Assert.True(svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "p1",
            DisplayName = "First",
            LocalPath = path1,
            RemoteUrl = "https://github.com/org/first.git"
        }).Success);

        var second = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "p1",
            DisplayName = "First Renamed",
            LocalPath = path2,
            RemoteUrl = "https://github.com/org/renamed.git",
            AllowOverwrite = true
        });

        Assert.True(second.Success);
        var portfolio = new PortfolioYamlProvider(_portfolioPath).LoadPortfolio();
        var entry = Assert.Single(portfolio.Projects);
        Assert.Equal(path2, entry.Path);
        Assert.Equal("https://github.com/org/renamed.git", entry.RemoteUrl);
    }

    [Fact]
    public void RegisterProject_CredentialBearingUrl_RejectedBeforeIO()
    {
        var localPath = Path.Combine(_root, "p1");
        Directory.CreateDirectory(localPath);

        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "p1",
            DisplayName = "Credential Repo",
            LocalPath = localPath,
            RemoteUrl = "https:/" + "/user:token@github.com/org/p1.git"
        });

        Assert.False(result.Success);
        Assert.Contains("credentials", result.Message);
        // Pre-flight: nothing on disk should have changed.
        Assert.False(File.Exists(_portfolioPath));
        Assert.False(Directory.Exists(Path.Combine(localPath, ".controltower")));
    }

    [Fact]
    public void RegisterProject_MalformedPortfolio_PreservesPortfolioAndProjectFilesByteForByte()
    {
        var localPath = Path.Combine(_root, "protected");
        Directory.CreateDirectory(localPath);

        var portfolioBytes = Encoding.UTF8.GetBytes(
            "schema_version: 1\nprojects:\n  - id: \"unterminated\n");
        File.WriteAllBytes(_portfolioPath, portfolioBytes);

        var metadataFolder = Path.Combine(
            _root, "portfolio-projects", "protected", ".controltower");
        Directory.CreateDirectory(metadataFolder);
        var projectPath = Path.Combine(metadataFolder, "project.yml");
        var productMapPath = Path.Combine(metadataFolder, "product-map.yml");
        var projectBytes = Encoding.UTF8.GetBytes(
            "kind: preserved/project\r\nid: protected\r\n");
        var productMapBytes = Encoding.UTF8.GetBytes(
            "kind: preserved/product-map\r\nproject_id: protected\r\n");
        File.WriteAllBytes(projectPath, projectBytes);
        File.WriteAllBytes(productMapPath, productMapBytes);

        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "protected",
            DisplayName = "Protected",
            LocalPath = localPath,
            AllowOverwrite = true
        });

        Assert.False(result.Success);
        Assert.Contains("Registration failed", result.Message);
        Assert.Contains("portfolio", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(portfolioBytes, File.ReadAllBytes(_portfolioPath));
        Assert.Equal(projectBytes, File.ReadAllBytes(projectPath));
        Assert.Equal(productMapBytes, File.ReadAllBytes(productMapPath));
    }

    [Fact]
    public void RegisterProject_TruncatedPortfolio_DoesNotCreateProjectMetadata()
    {
        var localPath = Path.Combine(_root, "new-project");
        Directory.CreateDirectory(localPath);

        var portfolioBytes = Encoding.UTF8.GetBytes(
            "schema_version: 1\nprojects:\n  - id: 'existing'\n    path:\n");
        File.WriteAllBytes(_portfolioPath, portfolioBytes);

        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "new-project",
            DisplayName = "New Project",
            LocalPath = localPath
        });

        Assert.False(result.Success);
        Assert.Contains("safely validated", result.Message);
        Assert.Equal(portfolioBytes, File.ReadAllBytes(_portfolioPath));
        Assert.False(Directory.Exists(
            Path.Combine(_root, "portfolio-projects", "new-project")));
    }

    [Fact]
    public void RegisterProject_PortfolioErrorIssue_AbortsWithoutWrites()
    {
        var existingPath = Path.Combine(_root, "existing");
        var localPath = Path.Combine(_root, "new-project");
        Directory.CreateDirectory(existingPath);
        Directory.CreateDirectory(localPath);

        var yaml =
            "schema_version: 99\n" +
            "projects:\n" +
            "  - id: 'existing'\n" +
            "    path: '" + existingPath.Replace("'", "''") + "'\n";
        var portfolioBytes = Encoding.UTF8.GetBytes(yaml);
        File.WriteAllBytes(_portfolioPath, portfolioBytes);

        var loaded = new PortfolioYamlProvider(_portfolioPath).LoadPortfolio();
        Assert.Contains(loaded.Issues, issue =>
            issue.Severity == IssueSeverity.Error &&
            issue.Code == "portfolio/schema/unsupported");

        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "new-project",
            DisplayName = "New Project",
            LocalPath = localPath
        });

        Assert.False(result.Success);
        Assert.Contains("unsupported schema_version", result.Message);
        Assert.Equal(portfolioBytes, File.ReadAllBytes(_portfolioPath));
        Assert.False(Directory.Exists(
            Path.Combine(_root, "portfolio-projects", "new-project")));
    }

    [Fact]
    public void RegisterProject_AbsentPortfolio_InitializesSuccessfully()
    {
        var localPath = Path.Combine(_root, "new-project");
        Directory.CreateDirectory(localPath);
        Assert.False(File.Exists(_portfolioPath));

        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "new-project",
            DisplayName = "New Project",
            LocalPath = localPath
        });

        Assert.True(result.Success);
        Assert.True(File.Exists(_portfolioPath));
        Assert.True(File.Exists(Path.Combine(
            _root,
            "portfolio-projects",
            "new-project",
            ".controltower",
            "project.yml")));
    }

    [Fact]
    public void RemoveProject_MalformedPortfolio_PreservesPortfolioByteForByte()
    {
        var portfolioBytes = Encoding.UTF8.GetBytes(
            "schema_version: 1\nprojects:\n  - id: \"unterminated\n");
        File.WriteAllBytes(_portfolioPath, portfolioBytes);

        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RemoveProject("existing");

        Assert.False(result.Success);
        Assert.Contains("Removal failed", result.Message);
        Assert.Equal(portfolioBytes, File.ReadAllBytes(_portfolioPath));
    }

    [Fact]
    public void RegisterProject_OverwriteWithEmptyRemote_KeepsExistingRemote()
    {
        var localPath = Path.Combine(_root, "p1");
        Directory.CreateDirectory(localPath);

        var svc = new ProjectRegistrationService(_portfolioPath);
        Assert.True(svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "p1",
            DisplayName = "First",
            LocalPath = localPath,
            RemoteUrl = "https://github.com/org/preserved.git"
        }).Success);

        Assert.True(svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "p1",
            DisplayName = "First Renamed",
            LocalPath = localPath,
            RemoteUrl = string.Empty,
            AllowOverwrite = true
        }).Success);

        var portfolio = new PortfolioYamlProvider(_portfolioPath).LoadPortfolio();
        var entry = Assert.Single(portfolio.Projects);
        // Empty RemoteUrl on update must NOT blank out the cached value.
        Assert.Equal("https://github.com/org/preserved.git", entry.RemoteUrl);
    }

    [Fact]
    public void RegisterProject_EditPreservesCustomPlanningAndRemoteUrl()
    {
        var localPath = Path.Combine(_root, "custom-plan");
        Directory.CreateDirectory(localPath);

        var svc = new ProjectRegistrationService(_portfolioPath);

        // Initial registration
        Assert.True(svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "custom-plan",
            DisplayName = "Custom Planning",
            Summary = "Original summary",
            LocalPath = localPath,
            RemoteUrl = "https://git.internal.example.com/team/repo.git"
        }).Success);

        // Patch the generated project.yml with custom planning values
        // that a user could set outside the registration UI.
        var portfolioDir = Path.GetDirectoryName(_portfolioPath);
        if (portfolioDir == null)
            throw new InvalidOperationException($"Portfolio path '{_portfolioPath}' has no directory component.");

        var metadataRoot = Path.Combine(
            portfolioDir,
            "portfolio-projects", "custom-plan");
        var projectYaml = Path.Combine(metadataRoot, ".controltower", "project.yml");
        var yaml = File.ReadAllText(projectYaml);
        yaml = yaml.Replace("authority: 'repo'", "authority: 'ado-board'");
        yaml = yaml.Replace(@"source_ref: '.controltower\product-map.yml'",
                             "source_ref: 'https://dev.azure.com/org/proj/_boards'");
        File.WriteAllText(projectYaml, yaml);

        // Edit: change only display name and summary, supply no remote URLs
        var editResult = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "custom-plan",
            DisplayName = "Custom Planning Renamed",
            Summary = "Updated summary",
            LocalPath = localPath,
            AllowOverwrite = true
        });
        Assert.True(editResult.Success);

        // Re-read the written project.yml
        var output = File.ReadAllText(projectYaml);

        // Custom planning values must survive the edit
        Assert.Contains("authority: 'ado-board'", output);
        Assert.Contains("source_ref: 'https://dev.azure.com/org/proj/_boards'", output);

        // Generic remote_url must be preserved, not blanked
        Assert.Contains("remote_url: 'https://git.internal.example.com/team/repo.git'", output);

        // Edited fields must reflect the update
        Assert.Contains("Custom Planning Renamed", output);
        Assert.Contains("Updated summary", output);
    }

    // ---- Finding 3: SSH launch metadata (vscode_ssh) correctness ----

    [Fact]
    public void RegisterProject_SshTarget_AbsolutePosix_VsCodeSshIsCorrect()
    {
        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "ssh-posix",
            DisplayName = "SSH Posix Project",
            SshTarget = "devuser@192.168.64.10:/srv/repos/ssh-posix"
        });
        Assert.True(result.Success);

        var projectYaml = ReadCentralProjectYaml("ssh-posix");
        // vscode_ssh must be the correct Remote SSH URI for a POSIX path.
        Assert.Contains("ssh-remote+devuser@192.168.64.10/srv/repos/ssh-posix", projectYaml);
    }

    [Fact]
    public void RegisterProject_SshTarget_WindowsAbsolute_VsCodeSshIsCorrect()
    {
        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "ssh-win",
            DisplayName = "SSH Windows Project",
            SshTarget = @"devuser@192.168.64.10:D:\repos\ssh-win"
        });
        Assert.True(result.Success);

        var projectYaml = ReadCentralProjectYaml("ssh-win");
        // Windows path "D:\repos\ssh-win" → "/D:/repos/ssh-win" in the VSCode Remote SSH URI.
        Assert.Contains("ssh-remote+devuser@192.168.64.10/D:/repos/ssh-win", projectYaml);
    }

    [Fact]
    public void RegisterProject_SshTarget_RelativePath_VsCodeSshIsEmpty()
    {
        // A relative SSH path (e.g. "repos/ssh-rel") cannot be resolved to an
        // absolute remote path here. BuildVsCodeSsh must return empty rather than
        // prepending '/' and producing the incorrect "/repos/ssh-rel" URI.
        var svc = new ProjectRegistrationService(_portfolioPath);
        var result = svc.RegisterProject(new ProjectRegistrationRequest
        {
            ProjectId = "ssh-rel",
            DisplayName = "SSH Relative Project",
            SshTarget = "devuser@192.168.64.10:repos/ssh-rel"
        });
        Assert.True(result.Success);

        var projectYaml = ReadCentralProjectYaml("ssh-rel");
        // vscode_ssh must be empty (''), not "/repos/ssh-rel".
        Assert.Contains("vscode_ssh: ''", projectYaml);
        Assert.DoesNotContain("/repos/ssh-rel", projectYaml);
    }

    private string ReadCentralProjectYaml(string projectId)
    {
        var portfolioDir = Path.GetDirectoryName(_portfolioPath)
            ?? throw new InvalidOperationException("Portfolio path has no directory component.");
        var stub = Path.Combine(portfolioDir, "portfolio-projects", projectId);
        return File.ReadAllText(Path.Combine(stub, ".controltower", "project.yml"));
    }
}
