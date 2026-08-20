using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace ControlTower.Infrastructure.Yaml.Dto
{
    public sealed class WorkspaceProfilesYamlDto
    {
        [YamlMember(Alias = "schema_version")]
        public int SchemaVersion { get; set; }

        [YamlMember(Alias = "profiles")]
        public List<WorkspaceProfileYamlDto> Profiles { get; set; }
    }

    public sealed class WorkspaceProfileYamlDto
    {
        [YamlMember(Alias = "id")]
        public string Id { get; set; }

        [YamlMember(Alias = "name")]
        public string Name { get; set; }

        [YamlMember(Alias = "members")]
        public List<string> Members { get; set; }
    }
}
