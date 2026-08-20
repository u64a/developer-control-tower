using System;
using System.Collections.Generic;
using System.Linq;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Core.Validation;

namespace ControlTower.Tests;

public sealed class WorkspaceProfilePolicyTests
{
    [Fact]
    public void Resolve_MissingDefinitionsOrSelection_UsesStableSyntheticAll()
    {
        var state = WorkspaceProfilePolicy.Resolve(
            new WorkspaceProfileCatalog(),
            new ActiveProfileSelection());

        Assert.True(state.ActiveProfile.IsSynthetic);
        Assert.Equal(WorkspaceProfilePolicy.AllProjectsProfileId, state.ActiveProfile.Id);
        Assert.Equal("All projects", state.ActiveProfile.Name);
    }

    [Fact]
    public void Resolve_MalformedDefinitions_UsesSyntheticAllAndPreservesIssues()
    {
        var catalog = new WorkspaceProfileCatalog();
        catalog.Profiles.Add(Profile(Guid.NewGuid(), "Alpha", "project-one"));
        catalog.Issues.Add(new ValidationIssue(
            IssueSeverity.Error,
            "profiles/yaml/malformed",
            "Broken YAML"));

        var state = WorkspaceProfilePolicy.Resolve(
            catalog,
            new ActiveProfileSelection
            {
                ProfileId = catalog.Profiles[0].Id
            });

        Assert.True(state.ActiveProfile.IsSynthetic);
        Assert.Contains(state.Issues, issue => issue.Code == "profiles/yaml/malformed");
    }

    [Fact]
    public void Resolve_ValidDefinitionsWithMissingSelection_UsesSyntheticAll()
    {
        var catalog = Catalog(Profile(Guid.NewGuid(), "Alpha", "project-one"));

        var state = WorkspaceProfilePolicy.Resolve(
            catalog,
            new ActiveProfileSelection());

        Assert.True(state.ActiveProfile.IsSynthetic);
        Assert.Empty(state.Issues);
    }

    [Fact]
    public void Resolve_DanglingSelection_UsesSyntheticAllWithVisibleWarning()
    {
        var catalog = Catalog(Profile(Guid.NewGuid(), "Alpha", "project-one"));

        var state = WorkspaceProfilePolicy.Resolve(
            catalog,
            new ActiveProfileSelection { ProfileId = Guid.NewGuid() });

        Assert.True(state.ActiveProfile.IsSynthetic);
        Assert.Contains(state.Issues, issue => issue.Code == "profiles/selection/dangling");
    }

    [Fact]
    public void FilterProjects_IsCaseInsensitiveAndDoesNotMutateCanonicalPortfolio()
    {
        var first = new ProjectRef
        {
            Id = "Project-One",
            Path = @"C:\repos\one",
            StoreId = "local",
            Folder = "one"
        };
        var second = new ProjectRef
        {
            Id = "project-two",
            Path = @"C:\repos\two",
            StoreId = "ssh",
            Folder = "two"
        };
        var canonical = new List<ProjectRef> { first, second };
        var active = Profile(Guid.NewGuid(), "Alpha", "project-one");

        var filtered = WorkspaceProfilePolicy.FilterProjects(canonical, active);

        var included = Assert.Single(filtered);
        Assert.Same(first, included);
        Assert.Equal(2, canonical.Count);
        Assert.Same(first, canonical[0]);
        Assert.Same(second, canonical[1]);
        Assert.Equal("local", first.StoreId);
        Assert.Equal("one", first.Folder);
        Assert.Equal(@"C:\repos\one", first.Path);
    }

    [Fact]
    public void SwitchProfile_WritesOnlyMachineLocalSelection()
    {
        var profile = Profile(Guid.NewGuid(), "Alpha", "project-one");
        var provider = new FakeProfileProvider(Catalog(profile));
        var selection = new FakeSelectionStore();
        var manager = new WorkspaceProfileManager(provider, selection);

        manager.SwitchProfile(profile.Id);

        Assert.Equal(profile.Id, selection.Current.ProfileId);
        Assert.Equal(1, selection.SaveCount);
        Assert.Equal(0, provider.SaveCount);
    }

    [Fact]
    public void SwitchProfile_AllProjects_WritesSyntheticSelectionOnly()
    {
        var profile = Profile(Guid.NewGuid(), "Alpha", "project-one");
        var provider = new FakeProfileProvider(Catalog(profile));
        var selection = new FakeSelectionStore(profile.Id);
        var manager = new WorkspaceProfileManager(provider, selection);

        manager.SwitchProfile(WorkspaceProfilePolicy.AllProjectsProfileId);

        Assert.Equal(
            WorkspaceProfilePolicy.AllProjectsProfileId,
            selection.Current.ProfileId);
        Assert.Equal(1, selection.SaveCount);
        Assert.Equal(0, provider.SaveCount);
    }

    [Fact]
    public void RenameProfile_ChangesOnlyNameAndKeepsStableIdAndSelection()
    {
        var profile = Profile(Guid.NewGuid(), "Alpha", "project-one");
        var provider = new FakeProfileProvider(Catalog(profile));
        var selection = new FakeSelectionStore(profile.Id);
        var manager = new WorkspaceProfileManager(provider, selection);

        manager.RenameProfile(profile.Id, "Renamed");

        var renamed = Assert.Single(provider.Catalog.Profiles);
        Assert.Equal(profile.Id, renamed.Id);
        Assert.Equal("Renamed", renamed.Name);
        Assert.Equal(new[] { "project-one" }, renamed.Members);
        Assert.Equal(0, selection.SaveCount);
    }

    [Fact]
    public void DeleteActiveProfile_SelectsDeterministicRemainingProfile()
    {
        var deleted = Profile(Guid.NewGuid(), "Current", "project-one");
        var zulu = Profile(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            "Zulu",
            "project-two");
        var alpha = Profile(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "alpha",
            "project-three");
        var provider = new FakeProfileProvider(Catalog(deleted, zulu, alpha));
        var selection = new FakeSelectionStore(deleted.Id);
        var manager = new WorkspaceProfileManager(provider, selection);

        var selectedId = manager.DeleteProfile(deleted.Id);

        Assert.Equal(alpha.Id, selectedId);
        Assert.Equal(alpha.Id, selection.Current.ProfileId);
        Assert.DoesNotContain(provider.Catalog.Profiles, profile => profile.Id == deleted.Id);
    }

    [Fact]
    public void DeleteProfile_LastPersistedProfile_IsBlocked()
    {
        var only = Profile(Guid.NewGuid(), "Only", "project-one");
        var manager = new WorkspaceProfileManager(
            new FakeProfileProvider(Catalog(only)),
            new FakeSelectionStore(only.Id));

        Assert.Throws<InvalidOperationException>(() => manager.DeleteProfile(only.Id));
    }

    [Fact]
    public void AppendProjectToActive_PersistsCanonicalMemberAndPreservesStaleIds()
    {
        var active = Profile(Guid.NewGuid(), "Alpha", "stale-project");
        var other = Profile(Guid.NewGuid(), "Other", "other-project");
        var provider = new FakeProfileProvider(Catalog(active, other));
        var selection = new FakeSelectionStore(active.Id);
        var manager = new WorkspaceProfileManager(provider, selection);

        var changed = manager.AppendProjectToActive(active, "New-Project");

        Assert.True(changed);
        Assert.Equal(1, provider.SaveCount);
        Assert.Equal(0, selection.SaveCount);
        var saved = provider.Catalog.Profiles.Single(profile => profile.Id == active.Id);
        Assert.Contains("stale-project", saved.Members);
        Assert.Contains("New-Project", saved.Members);
        Assert.Equal(new[] { "other-project" }, other.Members);
    }

    [Fact]
    public void AppendProjectToActive_IsCaseInsensitiveAndSyntheticAllDoesNotWrite()
    {
        var active = Profile(Guid.NewGuid(), "Alpha", "Project-One");
        var provider = new FakeProfileProvider(Catalog(active));
        var selection = new FakeSelectionStore(active.Id);
        var manager = new WorkspaceProfileManager(provider, selection);

        Assert.False(manager.AppendProjectToActive(active, "project-one"));
        Assert.Equal(0, provider.SaveCount);

        Assert.False(manager.AppendProjectToActive(
            WorkspaceProfilePolicy.CreateAllProjectsProfile(),
            "project-two"));
        Assert.Equal(0, provider.SaveCount);
    }

    [Fact]
    public void SaveProfiles_DeletedPreferredSelectionFallsBackDeterministically()
    {
        var zulu = Profile(
            Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff"),
            "Zulu",
            "z");
        var alpha = Profile(
            Guid.Parse("00000000-0000-0000-0000-000000000001"),
            "Alpha",
            "a");
        var provider = new FakeProfileProvider(new WorkspaceProfileCatalog());
        var selection = new FakeSelectionStore();
        var manager = new WorkspaceProfileManager(provider, selection);

        var selected = manager.SaveProfiles(
            new[] { zulu, alpha },
            preferredActiveProfileId: Guid.NewGuid());

        Assert.Equal(alpha.Id, selected);
        Assert.Equal(alpha.Id, selection.Current.ProfileId);
    }

    [Fact]
    public void SaveProfiles_SyntheticSelectionRemainsAllProjects()
    {
        var alpha = Profile(Guid.NewGuid(), "Alpha", "project-one");
        var provider = new FakeProfileProvider(new WorkspaceProfileCatalog());
        var selection = new FakeSelectionStore(
            WorkspaceProfilePolicy.AllProjectsProfileId);
        var manager = new WorkspaceProfileManager(provider, selection);

        var selected = manager.SaveProfiles(
            new[] { alpha },
            WorkspaceProfilePolicy.AllProjectsProfileId);

        Assert.Equal(WorkspaceProfilePolicy.AllProjectsProfileId, selected);
        Assert.Equal(
            WorkspaceProfilePolicy.AllProjectsProfileId,
            selection.Current.ProfileId);
        Assert.Equal(1, selection.SaveCount);
        Assert.Equal(1, provider.SaveCount);
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

    private static WorkspaceProfileCatalog Catalog(
        params WorkspaceProfile[] profiles)
    {
        var catalog = new WorkspaceProfileCatalog();
        foreach (var profile in profiles)
        {
            catalog.Profiles.Add(profile);
        }
        return catalog;
    }

    private sealed class FakeProfileProvider : IWorkspaceProfileProvider
    {
        public FakeProfileProvider(WorkspaceProfileCatalog catalog)
        {
            Catalog = catalog;
        }

        public WorkspaceProfileCatalog Catalog { get; private set; }

        public int SaveCount { get; private set; }

        public WorkspaceProfileCatalog LoadProfiles() => Catalog;

        public void SaveProfiles(IReadOnlyList<WorkspaceProfile> profiles)
        {
            var validation = WorkspaceProfilePolicy.ValidateDefinitions(
                profiles,
                requireAtLeastOne: true);
            if (validation.Any(issue => issue.Severity == IssueSeverity.Error))
            {
                throw new WorkspaceProfileValidationException(validation);
            }

            SaveCount++;
            Catalog = WorkspaceProfilePolicyTests.Catalog(
                profiles.Select(profile => profile.Clone()).ToArray());
        }
    }

    private sealed class FakeSelectionStore : IActiveProfileSelectionStore
    {
        public FakeSelectionStore(Guid? profileId = null)
        {
            Current = new ActiveProfileSelection { ProfileId = profileId };
        }

        public ActiveProfileSelection Current { get; private set; }

        public int SaveCount { get; private set; }

        public ActiveProfileSelection Load() => Current;

        public void Save(Guid profileId)
        {
            SaveCount++;
            Current = new ActiveProfileSelection { ProfileId = profileId };
        }
    }
}
