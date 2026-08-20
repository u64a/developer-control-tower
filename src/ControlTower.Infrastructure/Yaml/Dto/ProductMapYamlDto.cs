using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace ControlTower.Infrastructure.Yaml.Dto
{
    public sealed class ProductMapYamlDto
    {
        [YamlMember(Alias = "kind")]
        public string Kind { get; set; }

        [YamlMember(Alias = "schema_version")]
        public int SchemaVersion { get; set; }

        [YamlMember(Alias = "project_id")]
        public string ProjectId { get; set; }

        [YamlMember(Alias = "planning_authority")]
        public string PlanningAuthority { get; set; }

        [YamlMember(Alias = "nodes")]
        public List<ProductNodeDto> Nodes { get; set; }
    }

    public sealed class ProductNodeDto
    {
        [YamlMember(Alias = "id")]
        public string Id { get; set; }

        [YamlMember(Alias = "type")]
        public string Type { get; set; }

        [YamlMember(Alias = "title")]
        public string Title { get; set; }

        [YamlMember(Alias = "parent_id")]
        public string ParentId { get; set; }

        [YamlMember(Alias = "status")]
        public string Status { get; set; }

        [YamlMember(Alias = "description")]
        public string Description { get; set; }

        [YamlMember(Alias = "external_ref")]
        public ExternalRefDto ExternalRef { get; set; }
    }

    public sealed class ExternalRefDto
    {
        [YamlMember(Alias = "system")]
        public string System { get; set; }

        [YamlMember(Alias = "id")]
        public string Id { get; set; }

        [YamlMember(Alias = "url")]
        public string Url { get; set; }
    }
}
