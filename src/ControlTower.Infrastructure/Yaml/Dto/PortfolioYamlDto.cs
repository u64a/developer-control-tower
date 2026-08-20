using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace ControlTower.Infrastructure.Yaml.Dto
{
    public sealed class PortfolioYamlDto
    {
        [YamlMember(Alias = "schema_version")]
        public int? SchemaVersion { get; set; }

        [YamlMember(Alias = "projects")]
        public List<PortfolioProjectDto> Projects { get; set; }
    }

    public sealed class PortfolioProjectDto
    {
        [YamlMember(Alias = "id")]
        public string Id { get; set; }

        [YamlMember(Alias = "path")]
        public string Path { get; set; }

        [YamlMember(Alias = "store")]
        public string Store { get; set; }

        [YamlMember(Alias = "folder")]
        public string Folder { get; set; }

        [YamlMember(Alias = "remote_url")]
        public string RemoteUrl { get; set; }
    }
}
