using System;
using System.Collections.Generic;
using System.Linq;
using ControlTower.Core.Validation;

namespace ControlTower.Core.Models
{
    public sealed class WorkspaceProfile
    {
        public WorkspaceProfile()
        {
            Name = string.Empty;
            Members = new List<string>();
        }

        public Guid Id { get; set; }

        public string Name { get; set; }

        /// <summary>
        /// Canonical <see cref="ProjectRef.Id"/> values included in this profile.
        /// Stale values are retained so temporarily unavailable portfolio entries
        /// reappear if they return.
        /// </summary>
        public IList<string> Members { get; private set; }

        public bool IsSynthetic { get; set; }

        public bool IncludesProject(string projectRefId)
        {
            if (IsSynthetic)
            {
                return true;
            }

            return Members.Any(member =>
                string.Equals(member, projectRefId, StringComparison.OrdinalIgnoreCase));
        }

        public WorkspaceProfile Clone()
        {
            var clone = new WorkspaceProfile
            {
                Id = Id,
                Name = Name,
                IsSynthetic = IsSynthetic
            };

            foreach (var member in Members)
            {
                clone.Members.Add(member);
            }

            return clone;
        }
    }

    public sealed class WorkspaceProfileCatalog
    {
        public WorkspaceProfileCatalog()
        {
            Profiles = new List<WorkspaceProfile>();
            Issues = new List<ValidationIssue>();
        }

        public IList<WorkspaceProfile> Profiles { get; private set; }

        public IList<ValidationIssue> Issues { get; private set; }

        public bool HasErrors =>
            Issues.Any(issue => issue.Severity == IssueSeverity.Error);
    }

    public sealed class ActiveProfileSelection
    {
        public Guid? ProfileId { get; set; }

        public ValidationIssue Issue { get; set; }
    }

    public sealed class WorkspaceProfileState
    {
        public WorkspaceProfileState(
            IReadOnlyList<WorkspaceProfile> persistedProfiles,
            WorkspaceProfile activeProfile,
            IReadOnlyList<ValidationIssue> issues)
        {
            PersistedProfiles = persistedProfiles ?? Array.Empty<WorkspaceProfile>();
            ActiveProfile = activeProfile ?? throw new ArgumentNullException(nameof(activeProfile));
            Issues = issues ?? Array.Empty<ValidationIssue>();
        }

        public IReadOnlyList<WorkspaceProfile> PersistedProfiles { get; }

        public WorkspaceProfile ActiveProfile { get; }

        public IReadOnlyList<ValidationIssue> Issues { get; }

        public bool UsesSyntheticFallback => ActiveProfile.IsSynthetic;
    }

    public sealed class WorkspaceProfileValidationException : Exception
    {
        public WorkspaceProfileValidationException(IReadOnlyList<ValidationIssue> issues)
            : base(BuildMessage(issues))
        {
            Issues = issues ?? Array.Empty<ValidationIssue>();
        }

        public IReadOnlyList<ValidationIssue> Issues { get; }

        private static string BuildMessage(IReadOnlyList<ValidationIssue> issues)
        {
            if (issues == null || issues.Count == 0)
            {
                return "Workspace profile definitions are invalid.";
            }

            return string.Join(
                Environment.NewLine,
                issues.Select(issue => issue.Message));
        }
    }
}
