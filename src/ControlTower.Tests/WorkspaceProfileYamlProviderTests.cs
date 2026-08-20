using System;
using System.IO;
using System.Linq;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

public sealed class WorkspaceProfileYamlProviderTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public WorkspaceProfileYamlProviderTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "ct-profiles-yaml-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "profiles.yml");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public void Load_MissingOrEmptyFile_ReturnsEmptyCatalog()
    {
        var provider = new WorkspaceProfileYamlProvider(_path);

        var missing = provider.LoadProfiles();
        Assert.Empty(missing.Profiles);
        Assert.Empty(missing.Issues);

        File.WriteAllText(_path, string.Empty);
        var empty = provider.LoadProfiles();
        Assert.Empty(empty.Profiles);
        Assert.Empty(empty.Issues);
    }

    [Fact]
    public void SaveAndLoad_RoundTripsProfilesAndPreservesStaleMembers()
    {
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var first = Profile(firstId, "Client work", "project-b", "stale-project");
        var second = Profile(secondId, "Internal", "project-a");
        var provider = new WorkspaceProfileYamlProvider(_path);

        provider.SaveProfiles(new[] { first, second });
        var loaded = provider.LoadProfiles();

        Assert.Empty(loaded.Issues);
        Assert.Equal(2, loaded.Profiles.Count);
        Assert.Equal(firstId, loaded.Profiles[0].Id);
        Assert.Equal("Client work", loaded.Profiles[0].Name);
        Assert.Contains("stale-project", loaded.Profiles[0].Members);
        Assert.Equal(secondId, loaded.Profiles[1].Id);
    }

    [Fact]
    public void Load_MalformedYaml_ReturnsVisibleValidationIssue()
    {
        File.WriteAllText(
            _path,
            "schema_version: 1\nprofiles:\n  - id: \"unterminated\n");

        var loaded = new WorkspaceProfileYamlProvider(_path).LoadProfiles();

        Assert.Empty(loaded.Profiles);
        Assert.Contains(
            loaded.Issues,
            issue => issue.Code == "profiles/yaml/malformed");
    }

    [Fact]
    public void Load_DuplicatesAreReportedCaseInsensitively()
    {
        var id = Guid.NewGuid();
        File.WriteAllText(
            _path,
            $"""
            schema_version: 1
            profiles:
              - id: '{id:D}'
                name: 'Alpha'
                members:
                  - 'Project-One'
                  - 'project-one'
              - id: '{id:D}'
                name: 'alpha'
                members: []
            """);

        var loaded = new WorkspaceProfileYamlProvider(_path).LoadProfiles();

        Assert.Contains(loaded.Issues, issue => issue.Code == "profiles/id/duplicate");
        Assert.Contains(loaded.Issues, issue => issue.Code == "profiles/name/duplicate");
        Assert.Contains(loaded.Issues, issue => issue.Code == "profiles/member/duplicate");
    }

    [Fact]
    public void Load_InvalidGuidNameSchemaAndSentinelMember_AreReported()
    {
        File.WriteAllText(
            _path,
            """
            schema_version: 2
            profiles:
              - id: 'not-a-guid'
                name: ' '
                members:
                  - 'missing.project'
            """);

        var loaded = new WorkspaceProfileYamlProvider(_path).LoadProfiles();

        Assert.Contains(loaded.Issues, issue => issue.Code == "profiles/schema/unsupported");
        Assert.Contains(loaded.Issues, issue => issue.Code == "profiles/id/invalid");
        Assert.Contains(loaded.Issues, issue => issue.Code == "profiles/name/required");
        Assert.Contains(loaded.Issues, issue => issue.Code == "profiles/member/unstable");
    }

    [Fact]
    public void Save_InvalidDefinitions_ThrowsBeforeWriting()
    {
        var invalid = Profile(Guid.Empty, string.Empty, "invalid.project");
        var provider = new WorkspaceProfileYamlProvider(_path);

        var exception = Assert.Throws<WorkspaceProfileValidationException>(
            () => provider.SaveProfiles(new[] { invalid }));

        Assert.Contains(exception.Issues, issue => issue.Code == "profiles/id/invalid");
        Assert.False(File.Exists(_path));
    }

    [Fact]
    public void Save_ReplacesAtomicallyAndSerializesNoCredentialFields()
    {
        var id = Guid.NewGuid();
        var provider = new WorkspaceProfileYamlProvider(_path);
        provider.SaveProfiles(new[] { Profile(id, "First", "project-one") });
        provider.SaveProfiles(new[] { Profile(id, "Renamed", "project-two") });

        var text = File.ReadAllText(_path);
        Assert.Contains("name: 'Renamed'", text);
        Assert.DoesNotContain("name: 'First'", text);
        Assert.DoesNotContain("credential_target:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("password:", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret:", text, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    private static WorkspaceProfile Profile(
        Guid id,
        string name,
        params string[] members)
    {
        var profile = new WorkspaceProfile { Id = id, Name = name };
        foreach (var member in members)
        {
            profile.Members.Add(member);
        }
        return profile;
    }
}
