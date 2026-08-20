using System;
using System.Collections.Generic;
using System.Linq;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Core.UseCases
{
    public sealed class WorkspaceProfileManager
    {
        private readonly IWorkspaceProfileProvider _profileProvider;
        private readonly IActiveProfileSelectionStore _selectionStore;

        public WorkspaceProfileManager(
            IWorkspaceProfileProvider profileProvider,
            IActiveProfileSelectionStore selectionStore)
        {
            _profileProvider = profileProvider ?? throw new ArgumentNullException(nameof(profileProvider));
            _selectionStore = selectionStore ?? throw new ArgumentNullException(nameof(selectionStore));
        }

        public WorkspaceProfileState LoadState()
        {
            return WorkspaceProfilePolicy.Resolve(
                _profileProvider.LoadProfiles(),
                _selectionStore.Load());
        }

        /// <summary>
        /// Changes only the machine-local selection. Synced profile definitions
        /// are never rewritten during a switch.
        /// </summary>
        public void SwitchProfile(Guid profileId)
        {
            if (profileId == WorkspaceProfilePolicy.AllProjectsProfileId)
            {
                _selectionStore.Save(profileId);
                return;
            }

            var catalog = LoadUsableCatalog();
            if (!catalog.Profiles.Any(profile => profile.Id == profileId))
            {
                throw new InvalidOperationException("The selected workspace profile no longer exists.");
            }

            _selectionStore.Save(profileId);
        }

        public Guid SaveProfiles(
            IReadOnlyList<WorkspaceProfile> profiles,
            Guid? preferredActiveProfileId)
        {
            _profileProvider.SaveProfiles(profiles);

            if (!preferredActiveProfileId.HasValue ||
                preferredActiveProfileId.Value == WorkspaceProfilePolicy.AllProjectsProfileId)
            {
                _selectionStore.Save(WorkspaceProfilePolicy.AllProjectsProfileId);
                return WorkspaceProfilePolicy.AllProjectsProfileId;
            }

            var active = profiles.FirstOrDefault(
                profile => profile.Id == preferredActiveProfileId.Value);
            active ??= WorkspaceProfilePolicy.SelectDeterministic(profiles);

            if (active == null)
            {
                throw new InvalidOperationException("At least one persisted workspace profile is required.");
            }

            _selectionStore.Save(active.Id);
            return active.Id;
        }

        public void RenameProfile(Guid profileId, string newName)
        {
            var catalog = LoadUsableCatalog();
            var profiles = CloneProfiles(catalog.Profiles);
            var profile = profiles.FirstOrDefault(item => item.Id == profileId);
            if (profile == null)
            {
                throw new InvalidOperationException("The workspace profile no longer exists.");
            }

            profile.Name = (newName ?? string.Empty).Trim();
            _profileProvider.SaveProfiles(profiles);
        }

        public Guid DeleteProfile(Guid profileId)
        {
            var catalog = LoadUsableCatalog();
            if (catalog.Profiles.Count <= 1)
            {
                throw new InvalidOperationException("The last persisted workspace profile cannot be deleted.");
            }

            var profiles = CloneProfiles(catalog.Profiles);
            var removed = profiles.RemoveAll(profile => profile.Id == profileId);
            if (removed == 0)
            {
                throw new InvalidOperationException("The workspace profile no longer exists.");
            }

            _profileProvider.SaveProfiles(profiles);

            var selection = _selectionStore.Load();
            var selected = selection.ProfileId.HasValue
                ? profiles.FirstOrDefault(profile => profile.Id == selection.ProfileId.Value)
                : null;
            if (selected != null)
            {
                return selected.Id;
            }

            selected = WorkspaceProfilePolicy.SelectDeterministic(profiles);
            _selectionStore.Save(selected.Id);
            return selected.Id;
        }

        /// <summary>
        /// Adds a canonical portfolio member id to a persisted active profile.
        /// Synthetic All projects requires no write and returns false.
        /// </summary>
        public bool AppendProjectToActive(
            WorkspaceProfile activeProfile,
            string projectRefId)
        {
            if (activeProfile == null)
            {
                throw new ArgumentNullException(nameof(activeProfile));
            }

            if (activeProfile.IsSynthetic)
            {
                return false;
            }

            var memberId = (projectRefId ?? string.Empty).Trim();
            if (ProjectIdentity.IsUnstable(memberId))
            {
                throw new ArgumentException(
                    "A canonical portfolio ProjectRef.Id is required.",
                    nameof(projectRefId));
            }

            var catalog = LoadUsableCatalog();
            var profiles = CloneProfiles(catalog.Profiles);
            var current = profiles.FirstOrDefault(profile => profile.Id == activeProfile.Id);
            if (current == null)
            {
                throw new InvalidOperationException("The active workspace profile no longer exists.");
            }

            if (current.IncludesProject(memberId))
            {
                return false;
            }

            current.Members.Add(memberId);
            _profileProvider.SaveProfiles(profiles);
            return true;
        }

        public void SelectSyntheticFallback()
        {
            _selectionStore.Save(WorkspaceProfilePolicy.AllProjectsProfileId);
        }

        private WorkspaceProfileCatalog LoadUsableCatalog()
        {
            var catalog = _profileProvider.LoadProfiles();
            if (catalog.HasErrors)
            {
                throw new WorkspaceProfileValidationException(
                    catalog.Issues
                        .Where(issue => issue.Severity == IssueSeverity.Error)
                        .ToList());
            }

            if (catalog.Profiles.Count == 0)
            {
                throw new InvalidOperationException("No persisted workspace profiles exist.");
            }

            return catalog;
        }

        private static List<WorkspaceProfile> CloneProfiles(
            IEnumerable<WorkspaceProfile> profiles)
        {
            return profiles.Select(profile => profile.Clone()).ToList();
        }
    }
}
