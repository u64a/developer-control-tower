using System.Collections.Generic;
using ControlTower.Core.Validation;

namespace ControlTower.Core.Models
{
    public sealed class ProjectDefinition
    {
        public ProjectDefinition()
        {
            Id = string.Empty;
            DisplayName = string.Empty;
            Summary = string.Empty;
            LifecycleState = "active";
            ProjectRootPath = string.Empty;
            MetadataPath = string.Empty;
            Planning = new PlanningDefinition();
            Locations = new ProjectLocations();
            Launch = new LaunchTargets();
            Docs = new List<DocLink>();
            ExternalRefs = new ExternalReferences();
        }

        public string Id { get; set; }

        public string DisplayName { get; set; }

        public string Summary { get; set; }

        public string LifecycleState { get; set; }

        /// <summary>
        /// Optional organisational folder, e.g. "Customer Projects" / "IPKits".
        /// Empty means ungrouped. One group per project; durable in project.yml.
        /// </summary>
        public string Group { get; set; } = string.Empty;

        public string ProjectRootPath { get; set; }

        public string MetadataPath { get; set; }

        public PlanningDefinition Planning { get; private set; }

        public ProjectLocations Locations { get; private set; }

        public LaunchTargets Launch { get; private set; }

        public IList<DocLink> Docs { get; private set; }

        public ExternalReferences ExternalRefs { get; private set; }
    }

    public sealed class PlanningDefinition
    {
        public PlanningDefinition()
        {
            Authority = "repo";
            SourceRef = string.Empty;
        }

        public string Authority { get; set; }

        public string SourceRef { get; set; }
    }

    public sealed class ProjectLocations
    {
        public ProjectLocations()
        {
            LocalPath = string.Empty;
            SshTarget = string.Empty;
            RemoteUrl = string.Empty;
        }

        public string LocalPath { get; set; }

        public string SshTarget { get; set; }

        public string RemoteUrl { get; set; }
    }

    public sealed class LaunchTargets
    {
        public LaunchTargets()
        {
            VsCodeLocal = string.Empty;
            VsCodeSsh = string.Empty;
            GitHub = string.Empty;
            Ado = string.Empty;
        }

        public string VsCodeLocal { get; set; }

        public string VsCodeSsh { get; set; }

        public string GitHub { get; set; }

        public string Ado { get; set; }
    }

    public sealed class DocLink
    {
        public DocLink()
        {
            Id = string.Empty;
            Title = string.Empty;
            Kind = string.Empty;
            Url = string.Empty;
        }

        public string Id { get; set; }

        public string Title { get; set; }

        public string Kind { get; set; }

        public string Url { get; set; }
    }

    public sealed class ExternalReferences
    {
        public ExternalReferences()
        {
            GitHubRepo = string.Empty;
            GitHubDefaultBranch = string.Empty;
            AdoOrganization = string.Empty;
            AdoProject = string.Empty;
            AdoAreaPath = string.Empty;
            AdoWorkItemRootId = string.Empty;
        }

        public string GitHubRepo { get; set; }

        public string GitHubDefaultBranch { get; set; }

        public string AdoOrganization { get; set; }

        public string AdoProject { get; set; }

        public string AdoAreaPath { get; set; }

        public string AdoWorkItemRootId { get; set; }
    }

    public sealed class ProjectLoadResult
    {
        public ProjectLoadResult()
        {
            Project = new ProjectDefinition();
            Issues = new List<ValidationIssue>();
        }

        public ProjectDefinition Project { get; set; }

        public IList<ValidationIssue> Issues { get; private set; }
    }

    public sealed class ProjectRegistrationRequest
    {
        public ProjectRegistrationRequest()
        {
            ProjectId = string.Empty;
            SourcePath = string.Empty;
            DisplayName = string.Empty;
            Summary = string.Empty;
            LifecycleState = "active";
            LocalPath = string.Empty;
            SshTarget = string.Empty;
            GitHubUrl = string.Empty;
            AdoUrl = string.Empty;
            RemoteUrl = string.Empty;
            AllowOverwrite = false;
        }

        public string ProjectId { get; set; }

        public string SourcePath { get; set; }

        public string DisplayName { get; set; }

        public string Summary { get; set; }

        public string LifecycleState { get; set; }

        public string LocalPath { get; set; }

        public string SshTarget { get; set; }

        public string GitHubUrl { get; set; }

        public string AdoUrl { get; set; }

        /// <summary>Optional organisational folder. Empty = ungrouped.</summary>
        public string Group { get; set; } = string.Empty;

        /// <summary>
        /// Neutral remote URL slot used by the scan-and-register flow.
        /// Takes precedence over <see cref="GitHubUrl"/> and
        /// <see cref="AdoUrl"/> as the source of truth for the
        /// portfolio's <c>remote_url</c> column. May be empty for
        /// callers that only know the host-specific launch URL.
        /// </summary>
        public string RemoteUrl { get; set; }

        /// <summary>
        /// When false (default) re-registering an existing project id
        /// fails the request and leaves the portfolio entry untouched.
        /// When true the existing entry's <c>Path</c> and
        /// <c>RemoteUrl</c> are updated in place. Phase C safety: scan-
        /// and-register defaults to false so the user must explicitly
        /// pick a different id rather than silently overwriting.
        /// </summary>
        public bool AllowOverwrite { get; set; }
    }

    public sealed class ProjectRegistrationResult
    {
        public ProjectRegistrationResult()
        {
            ProjectId = string.Empty;
            Message = string.Empty;
        }

        public bool Success { get; set; }

        public string ProjectId { get; set; }

        public string Message { get; set; }
    }
}
