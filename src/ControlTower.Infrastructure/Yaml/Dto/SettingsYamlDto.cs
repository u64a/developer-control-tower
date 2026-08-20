using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace ControlTower.Infrastructure.Yaml.Dto
{
    public sealed class SettingsYamlDto
    {
        [YamlMember(Alias = "kind")]
        public string Kind { get; set; }

        [YamlMember(Alias = "schema_version")]
        public int SchemaVersion { get; set; }

        [YamlMember(Alias = "tooling")]
        public ToolingDto Tooling { get; set; }

        [YamlMember(Alias = "security")]
        public SecurityDto Security { get; set; }

        [YamlMember(Alias = "stores")]
        public Dictionary<string, StoreDto> Stores { get; set; }

        [YamlMember(Alias = "library")]
        public LibraryDto Library { get; set; }

        [YamlMember(Alias = "updates")]
        public UpdatesDto Updates { get; set; }
    }

    public sealed class ToolingDto
    {
        [YamlMember(Alias = "vscode_command")]
        public string VsCodeCommand { get; set; }

        [YamlMember(Alias = "git_command")]
        public string GitCommand { get; set; }

        [YamlMember(Alias = "ssh_command")]
        public string SshCommand { get; set; }

        [YamlMember(Alias = "ssh_config_path")]
        public string SshConfigPath { get; set; }
    }

    public sealed class SecurityDto
    {
        [YamlMember(Alias = "allow_http_links")]
        public bool? AllowHttpLinks { get; set; }

        [YamlMember(Alias = "github_credential_target")]
        public string GitHubCredentialTarget { get; set; }

        [YamlMember(Alias = "ado_credential_target")]
        public string AdoCredentialTarget { get; set; }
    }

    public sealed class StoreDto
    {
        [YamlMember(Alias = "type")]
        public string Type { get; set; }

        [YamlMember(Alias = "root")]
        public string Root { get; set; }

        [YamlMember(Alias = "host")]
        public string Host { get; set; }

        [YamlMember(Alias = "user")]
        public string User { get; set; }

        [YamlMember(Alias = "credential_target")]
        public string CredentialTarget { get; set; }

        [YamlMember(Alias = "port")]
        public int Port { get; set; }
    }

    public sealed class LibraryDto
    {
        [YamlMember(Alias = "path")]
        public string Path { get; set; }
    }

    public sealed class UpdatesDto
    {
        [YamlMember(Alias = "branch")]
        public string Branch { get; set; }

        [YamlMember(Alias = "auto_check_on_launch")]
        public bool? AutoCheckOnLaunch { get; set; }

        [YamlMember(Alias = "repo_root_override")]
        public string RepoRootOverride { get; set; }
    }
}
