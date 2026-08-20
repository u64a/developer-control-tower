using System;
using System.Collections.Generic;
using System.Linq;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Core.Composition
{
    public static class WorkspaceProfilePolicy
    {
        public static readonly Guid AllProjectsProfileId =
            Guid.Parse("8e40f1f1-822c-48ef-b9ed-93d42f7d58fb");

        public const string AllProjectsProfileName = "All projects";

        public static WorkspaceProfile CreateAllProjectsProfile()
        {
            return new WorkspaceProfile
            {
                Id = AllProjectsProfileId,
                Name = AllProjectsProfileName,
                IsSynthetic = true
            };
        }

        public static WorkspaceProfileState Resolve(
            WorkspaceProfileCatalog catalog,
            ActiveProfileSelection selection)
        {
            catalog ??= new WorkspaceProfileCatalog();
            selection ??= new ActiveProfileSelection();

            var issues = new List<ValidationIssue>(catalog.Issues);
            if (selection.Issue != null)
            {
                issues.Add(selection.Issue);
            }

            var persisted = catalog.Profiles
                .Select(profile => profile.Clone())
                .ToList();

            if (!catalog.HasErrors && persisted.Count > 0 && selection.ProfileId.HasValue)
            {
                var selected = persisted.FirstOrDefault(profile =>
                    profile.Id == selection.ProfileId.Value);
                if (selected != null)
                {
                    return new WorkspaceProfileState(persisted, selected, issues);
                }

                if (selection.ProfileId.Value != AllProjectsProfileId)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Warning,
                        "profiles/selection/dangling",
                        "The selected workspace profile no longer exists. Showing all projects."));
                }
            }

            return new WorkspaceProfileState(
                persisted,
                CreateAllProjectsProfile(),
                issues);
        }

        public static IReadOnlyList<ProjectRef> FilterProjects(
            IEnumerable<ProjectRef> canonicalProjects,
            WorkspaceProfile activeProfile)
        {
            if (canonicalProjects == null)
            {
                return Array.Empty<ProjectRef>();
            }

            if (activeProfile == null || activeProfile.IsSynthetic)
            {
                return canonicalProjects.ToList();
            }

            return canonicalProjects
                .Where(project => project != null && activeProfile.IncludesProject(project.Id))
                .ToList();
        }

        public static IReadOnlyList<ValidationIssue> ValidateDefinitions(
            IEnumerable<WorkspaceProfile> profiles,
            bool requireAtLeastOne)
        {
            var issues = new List<ValidationIssue>();
            var definitions = profiles?.ToList() ?? new List<WorkspaceProfile>();

            if (requireAtLeastOne && definitions.Count == 0)
            {
                issues.Add(new ValidationIssue(
                    IssueSeverity.Error,
                    "profiles/empty",
                    "At least one persisted workspace profile is required."));
                return issues;
            }

            var ids = new HashSet<Guid>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            for (var index = 0; index < definitions.Count; index++)
            {
                var profile = definitions[index];
                var label = "Profile " + (index + 1);

                if (profile == null)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "profiles/profile/null",
                        label + " is empty."));
                    continue;
                }

                if (profile.IsSynthetic)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "profiles/profile/synthetic",
                        label + " is synthetic and cannot be persisted."));
                }

                if (profile.Id == Guid.Empty)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "profiles/id/invalid",
                        label + " must have a non-empty GUID id."));
                }
                else if (profile.Id == AllProjectsProfileId)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "profiles/id/reserved",
                        label + " uses the reserved synthetic All projects id."));
                }
                else if (!ids.Add(profile.Id))
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "profiles/id/duplicate",
                        label + " duplicates another profile id."));
                }

                var name = (profile.Name ?? string.Empty).Trim();
                if (name.Length == 0)
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "profiles/name/required",
                        label + " must have a name."));
                }
                else if (!names.Add(name))
                {
                    issues.Add(new ValidationIssue(
                        IssueSeverity.Error,
                        "profiles/name/duplicate",
                        "Workspace profile names must be unique (case-insensitive): '" + name + "'."));
                }

                var members = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var rawMember in profile.Members ?? Array.Empty<string>())
                {
                    var member = (rawMember ?? string.Empty).Trim();
                    if (member.Length == 0)
                    {
                        issues.Add(new ValidationIssue(
                            IssueSeverity.Error,
                            "profiles/member/invalid",
                            label + " contains an empty project member id."));
                    }
                    else if (ProjectIdentity.IsUnstable(member))
                    {
                        issues.Add(new ValidationIssue(
                            IssueSeverity.Error,
                            "profiles/member/unstable",
                            label + " contains a non-canonical project id: '" + member + "'."));
                    }
                    else if (!members.Add(member))
                    {
                        issues.Add(new ValidationIssue(
                            IssueSeverity.Error,
                            "profiles/member/duplicate",
                            label + " contains duplicate project member id '" + member + "'."));
                    }
                }
            }

            return issues;
        }

        public static WorkspaceProfile SelectDeterministic(
            IEnumerable<WorkspaceProfile> profiles)
        {
            return (profiles ?? Enumerable.Empty<WorkspaceProfile>())
                .Where(profile => profile != null)
                .OrderBy(profile => profile.Id)
                .FirstOrDefault();
        }
    }
}
