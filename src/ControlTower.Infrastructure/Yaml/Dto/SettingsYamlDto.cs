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

        [YamlMember(Alias = "launch")]
        public LaunchSettingsDto Launch { get; set; }
    }

    public sealed class LaunchSettingsDto
    {
        [YamlMember(Alias = "default_environment")]
        public string DefaultEnvironment { get; set; }

        [YamlMember(Alias = "environments")]
        public Dictionary<string, LaunchEnvironmentDto> Environments { get; set; }
    }

    public sealed class LaunchEnvironmentDto
    {
        [YamlMember(Alias = "name")]
        public string Name { get; set; }

        /// <summary><c>editor</c> or <c>terminal</c>.</summary>
        [YamlMember(Alias = "type")]
        public string Type { get; set; }

        [YamlMember(Alias = "command")]
        public string Command { get; set; }

        [YamlMember(Alias = "args")]
        public string Args { get; set; }

        [YamlMember(Alias = "remote_command")]
        public string RemoteCommand { get; set; }

        [YamlMember(Alias = "icon")]
        public string Icon { get; set; }
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

        /// <summary>Windows Terminal executable used to host terminal launch environments.</summary>
        [YamlMember(Alias = "terminal_command")]
        public string TerminalCommand { get; set; }

        /// <summary>PowerShell executable used inside the terminal (default pwsh, else Windows PowerShell).</summary>
        [YamlMember(Alias = "powershell_command")]
        public string PowerShellCommand { get; set; }
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
