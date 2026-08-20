using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace ControlTower.Infrastructure.Yaml.Dto
{
    public sealed class ProjectYamlDto
    {
        [YamlMember(Alias = "kind")]
        public string Kind { get; set; }

        [YamlMember(Alias = "schema_version")]
        public int SchemaVersion { get; set; }

        [YamlMember(Alias = "id")]
        public string Id { get; set; }

        [YamlMember(Alias = "display_name")]
        public string DisplayName { get; set; }

        [YamlMember(Alias = "summary")]
        public string Summary { get; set; }

        [YamlMember(Alias = "lifecycle_state")]
        public string LifecycleState { get; set; }

        [YamlMember(Alias = "group")]
        public string Group { get; set; }

        [YamlMember(Alias = "planning")]
        public PlanningDto Planning { get; set; }

        [YamlMember(Alias = "locations")]
        public LocationsDto Locations { get; set; }

        [YamlMember(Alias = "launch")]
        public LaunchDto Launch { get; set; }

        [YamlMember(Alias = "docs")]
        public List<DocLinkDto> Docs { get; set; }

        [YamlMember(Alias = "external_refs")]
        public ExternalRefsDto ExternalRefs { get; set; }
    }

    public sealed class PlanningDto
    {
        [YamlMember(Alias = "authority")]
        public string Authority { get; set; }

        [YamlMember(Alias = "source_ref")]
        public string SourceRef { get; set; }
    }

    public sealed class LocationsDto
    {
        [YamlMember(Alias = "local_path")]
        public string LocalPath { get; set; }

        [YamlMember(Alias = "ssh_target")]
        public string SshTarget { get; set; }

        [YamlMember(Alias = "remote_url")]
        public string RemoteUrl { get; set; }
    }

    public sealed class LaunchDto
    {
        [YamlMember(Alias = "vscode_local")]
        public string VsCodeLocal { get; set; }

        [YamlMember(Alias = "vscode_ssh")]
        public string VsCodeSsh { get; set; }

        [YamlMember(Alias = "github")]
        public string GitHub { get; set; }

        [YamlMember(Alias = "ado")]
        public string Ado { get; set; }
    }

    public sealed class DocLinkDto
    {
        [YamlMember(Alias = "id")]
        public string Id { get; set; }

        [YamlMember(Alias = "title")]
        public string Title { get; set; }

        [YamlMember(Alias = "kind")]
        public string Kind { get; set; }

        [YamlMember(Alias = "url")]
        public string Url { get; set; }
    }

    public sealed class ExternalRefsDto
    {
        [YamlMember(Alias = "github")]
        public GitHubRefsDto GitHub { get; set; }

        [YamlMember(Alias = "ado")]
        public AdoRefsDto Ado { get; set; }
    }

    public sealed class GitHubRefsDto
    {
        [YamlMember(Alias = "repo")]
        public string Repo { get; set; }

        [YamlMember(Alias = "default_branch")]
        public string DefaultBranch { get; set; }
    }

    public sealed class AdoRefsDto
    {
        [YamlMember(Alias = "organization")]
        public string Organization { get; set; }

        [YamlMember(Alias = "project")]
        public string Project { get; set; }

        [YamlMember(Alias = "area_path")]
        public string AreaPath { get; set; }

        [YamlMember(Alias = "work_item_root_id")]
        public string WorkItemRootId { get; set; }
    }
}
