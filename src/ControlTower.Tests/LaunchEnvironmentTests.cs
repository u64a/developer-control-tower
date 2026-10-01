using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Launch;
using ControlTower.Infrastructure.Registration;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

/// <summary>
/// Launch environments: catalog resolution, settings merge/validation,
/// persistence (settings.yml + project.yml) and the launch command lines.
/// </summary>
public class LaunchEnvironmentTests : IDisposable
{
    private readonly string _root;

    public LaunchEnvironmentTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ct-launchenv-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------- catalog

    [Fact]
    public void Catalog_Default_ContainsBuiltInsAndDefaultsToVsCode()
    {
        var catalog = LaunchEnvironmentCatalog.CreateDefault();

        Assert.Equal(
            new[] { "vscode", "copilot-cli", "claude-code" },
            catalog.Environments.Select(e => e.Id).ToArray());
        Assert.Equal("vscode", catalog.DefaultId);
        Assert.Equal(LaunchEnvironmentKind.Terminal, catalog.Find("copilot-cli")!.Kind);
        Assert.Equal("copilot", catalog.Find("copilot-cli")!.Command);
        Assert.Equal("claude", catalog.Find("claude-code")!.Command);
    }

    [Theory]
    [InlineData(null, "vscode")]
    [InlineData("", "vscode")]
    [InlineData("does-not-exist", "vscode")]
    [InlineData("  Copilot-CLI ", "copilot-cli")]
    [InlineData("claude-code", "claude-code")]
    public void Catalog_Resolve_FallsBackToDefault(string? requested, string expected)
    {
        var catalog = LaunchEnvironmentCatalog.CreateDefault();
        Assert.Equal(expected, catalog.Resolve(requested).Id);
    }

    [Fact]
    public void Catalog_UnknownDefault_FallsBackToVsCode_AndVsCodeAlwaysPresent()
    {
        var catalog = new LaunchEnvironmentCatalog(
            new[] { new LaunchEnvironment("tool", "Tool", LaunchEnvironmentKind.Terminal, "tool") },
            "nope");

        Assert.Equal("vscode", catalog.DefaultId);
        Assert.NotNull(catalog.Find("vscode"));
        Assert.True(catalog.IsUnknown("nope"));
        Assert.False(catalog.IsUnknown(""));
        Assert.False(catalog.IsUnknown("TOOL"));
    }

    [Theory]
    [InlineData("vscode", true)]
    [InlineData("my-tool-2", true)]
    [InlineData("-leading", false)]
    [InlineData("Upper", false)]
    [InlineData("has space", false)]
    [InlineData("semi;colon", false)]
    [InlineData("", false)]
    public void Catalog_IsValidId(string id, bool expected)
    {
        Assert.Equal(expected, LaunchEnvironmentCatalog.IsValidId(id));
    }

    [Fact]
    public void Catalog_IsValidId_RejectsOver40Chars()
    {
        Assert.True(LaunchEnvironmentCatalog.IsValidId(new string('a', 40)));
        Assert.False(LaunchEnvironmentCatalog.IsValidId(new string('a', 41)));
    }

    // ------------------------------------------------------- settings merging

    private static ToolSettings SettingsWith(string? defaultId, params (string Id, LaunchEnvironmentOverride Spec)[] overrides)
    {
        var settings = new ToolSettings { VsCodeCommand = "code", DefaultLaunchEnvironment = defaultId! };
        settings.LaunchEnvironmentOverrides = overrides.ToDictionary(o => o.Id, o => o.Spec);
        return settings;
    }

    [Fact]
    public void BuildLaunchCatalog_PartialBuiltInOverride_KeepsOtherFields()
    {
        var settings = SettingsWith("copilot-cli", ("copilot-cli", new LaunchEnvironmentOverride { Args = "--banner" }));

        var catalog = ToolSettingsProvider.BuildLaunchCatalog(settings);

        var copilot = catalog.Find("copilot-cli")!;
        Assert.Equal("GitHub Copilot CLI", copilot.DisplayName);
        Assert.Equal("copilot", copilot.Command);
        Assert.Equal("--banner", copilot.Arguments);
        Assert.True(copilot.IsBuiltIn);
        Assert.Equal("copilot-cli", catalog.DefaultId);
        Assert.Empty(settings.Issues);
    }

    [Fact]
    public void BuildLaunchCatalog_CustomEntry_DefaultsToTerminalAndIsAppended()
    {
        var settings = SettingsWith(null,
            ("Aider", new LaunchEnvironmentOverride { Name = "Aider", Command = "aider", Args = "--dark-mode" }),
            ("cursor", new LaunchEnvironmentOverride { Name = "Cursor", Type = "editor", Command = "cursor" }));

        var catalog = ToolSettingsProvider.BuildLaunchCatalog(settings);

        var aider = catalog.Find("aider")!;
        Assert.Equal(LaunchEnvironmentKind.Terminal, aider.Kind);
        Assert.Equal("--dark-mode", aider.Arguments);
        Assert.False(aider.IsBuiltIn);
        Assert.Equal(LaunchEnvironmentKind.Editor, catalog.Find("cursor")!.Kind);
        Assert.Equal(5, catalog.Environments.Count);
        Assert.Equal("vscode", catalog.DefaultId);
    }

    [Fact]
    public void BuildLaunchCatalog_InvalidEntries_RaiseIssuesAndAreSkipped()
    {
        var settings = SettingsWith("ghost",
            ("Bad Id!", new LaunchEnvironmentOverride { Command = "x" }),
            ("bad-type", new LaunchEnvironmentOverride { Type = "gui", Command = "x" }),
            ("no-command", new LaunchEnvironmentOverride { Name = "Nothing" }),
            ("quote-command", new LaunchEnvironmentOverride { Command = "evil\" & calc" }),
            ("multi-line", new LaunchEnvironmentOverride { Command = "x", Args = "a\nb" }),
            ("bad-remote", new LaunchEnvironmentOverride { Command = "x", RemoteCommand = "a\rb" }),
            ("bad-icon", new LaunchEnvironmentOverride { Command = "x", Icon = @"relative\icon.png" }));

        var catalog = ToolSettingsProvider.BuildLaunchCatalog(settings);
        var codes = settings.Issues.Select(i => i.Code).ToList();

        Assert.Contains("settings/launch/invalid-id", codes);
        Assert.Contains("settings/launch/invalid-type", codes);
        Assert.Equal(2, codes.Count(c => c == "settings/launch/invalid-command"));
        Assert.Equal(2, codes.Count(c => c == "settings/launch/invalid-args"));
        Assert.Contains("settings/launch/invalid-icon", codes);
        Assert.Contains("settings/launch/unknown-default", codes);

        Assert.Null(catalog.Find("bad-type"));
        Assert.Null(catalog.Find("no-command"));
        Assert.Null(catalog.Find("quote-command"));
        Assert.Null(catalog.Find("multi-line"));
        Assert.Null(catalog.Find("bad-remote"));
        // An invalid icon only drops the icon, not the environment.
        Assert.Equal(string.Empty, catalog.Find("bad-icon")!.IconPath);
        Assert.Equal("vscode", catalog.DefaultId);
    }

    [Fact]
    public void BuildLaunchCatalog_ValidIcon_IsKept()
    {
        var icon = Path.Combine(_root, "tool.png");
        File.WriteAllBytes(icon, new byte[] { 1 });
        var settings = SettingsWith(null, ("tool", new LaunchEnvironmentOverride { Command = "tool", Icon = icon }));

        var catalog = ToolSettingsProvider.BuildLaunchCatalog(settings);

        Assert.Equal(icon, catalog.Find("tool")!.IconPath);
        Assert.Empty(settings.Issues);
    }

    [Fact]
    public void Load_LaunchSection_BuildsCatalog()
    {
        var settingsFile = Path.Combine(_root, "settings.yml");
        File.WriteAllText(settingsFile, @"launch:
  default_environment: claude-code
  environments:
    claude-code:
      args: --continue
    my-tool:
      name: My Tool
      type: terminal
      command: mytool
      remote_command: mytool --remote
");

        var settings = new ToolSettingsProvider().Load(settingsFile);

        Assert.NotNull(settings.LaunchEnvironments);
        Assert.Equal("claude-code", settings.LaunchEnvironments.DefaultId);
        Assert.Equal("--continue", settings.LaunchEnvironments.Find("claude-code")!.Arguments);
        var tool = settings.LaunchEnvironments.Find("my-tool")!;
        Assert.Equal("My Tool", tool.DisplayName);
        Assert.Equal("mytool --remote", tool.RemoteCommand);
        Assert.DoesNotContain(settings.Issues, i => i.Code.StartsWith("settings/launch/", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(settings.PowerShellCommand));
    }

    [Fact]
    public void Load_NoLaunchSection_DefaultsToVsCode()
    {
        var settingsFile = Path.Combine(_root, "settings.yml");
        File.WriteAllText(settingsFile, "kind: developer-control-tower/settings\nschema_version: 0\n");

        var settings = new ToolSettingsProvider().Load(settingsFile);

        Assert.Equal("vscode", settings.LaunchEnvironments.DefaultId);
        Assert.Equal(3, settings.LaunchEnvironments.Environments.Count);
    }

    // --------------------------------------------------------- settings write

    [Fact]
    public void SettingsWriter_SetsDefault_AndPreservesCustomEnvironments()
    {
        var settingsFile = Path.Combine(_root, "settings.yml");
        File.WriteAllText(settingsFile, @"launch:
  default_environment: vscode
  environments:
    my-tool:
      name: My Tool
      command: mytool
      args: --flag
    claude-code: {}
");

        new SettingsWriter().Write(settingsFile, Array.Empty<RepoStore>(), null, null, "copilot-cli");
        var reloaded = new ToolSettingsProvider().Load(settingsFile);

        Assert.Equal("copilot-cli", reloaded.LaunchEnvironments.DefaultId);
        var tool = reloaded.LaunchEnvironments.Find("my-tool")!;
        Assert.Equal("My Tool", tool.DisplayName);
        Assert.Equal("--flag", tool.Arguments);
        Assert.NotNull(reloaded.LaunchEnvironments.Find("claude-code"));
        Assert.DoesNotContain(reloaded.Issues, i => i.Code.StartsWith("settings/launch/", StringComparison.Ordinal));
    }

    [Fact]
    public void SettingsWriter_NullDefault_KeepsExistingValue()
    {
        var settingsFile = Path.Combine(_root, "settings.yml");
        File.WriteAllText(settingsFile, "launch:\n  default_environment: claude-code\n");

        new SettingsWriter().Write(settingsFile, Array.Empty<RepoStore>(), null, null);

        Assert.Equal("claude-code", new ToolSettingsProvider().Load(settingsFile).LaunchEnvironments.DefaultId);
    }

    // ----------------------------------------------------- project.yml / reg

    private static string ProjectYaml(string environmentLine) => @"kind: developer-control-tower/project
schema_version: 0

id: p1
display_name: P1

launch:
" + environmentLine + "\n";

    [Theory]
    [InlineData("  environment: Copilot-CLI", "copilot-cli")]
    [InlineData("  environment: not valid!", "")]
    [InlineData("  github: https://github.com/o/r", "")]
    public void ProjectYaml_ParsesEnvironment(string line, string expected)
    {
        var dir = Path.Combine(_root, "proj");
        Directory.CreateDirectory(Path.Combine(dir, ".controltower"));
        File.WriteAllText(Path.Combine(dir, ".controltower", "project.yml"), ProjectYaml(line));

        var result = new ProjectYamlProvider().LoadProject(dir);

        Assert.Equal(expected, result.Project.Launch.Environment ?? string.Empty);
    }

    private (ProjectRegistrationService Service, string ProjectYamlPath, string LocalPath) NewRegistration()
    {
        var localPath = Path.Combine(_root, "p1");
        Directory.CreateDirectory(localPath);
        var svc = new ProjectRegistrationService(Path.Combine(_root, "portfolio.yml"));
        var yamlPath = Path.Combine(_root, "portfolio-projects", "p1", ".controltower", "project.yml");
        return (svc, yamlPath, localPath);
    }

    private static ProjectRegistrationRequest Request(string localPath, string? environment, bool overwrite) => new()
    {
        ProjectId = "p1",
        DisplayName = "Project One",
        LocalPath = localPath,
        LaunchEnvironment = environment!,
        AllowOverwrite = overwrite
    };

    private static string ReadEnvironment(string yamlPath)
    {
        var dir = Path.GetDirectoryName(Path.GetDirectoryName(yamlPath))!;
        return new ProjectYamlProvider().LoadProject(dir).Project.Launch.Environment ?? string.Empty;
    }

    [Fact]
    public void Registration_SetPreserveAndClearEnvironment()
    {
        var (svc, yamlPath, localPath) = NewRegistration();

        Assert.True(svc.RegisterProject(Request(localPath, "Claude-Code", false)).Success);
        Assert.Matches(@"(?m)^  environment: ['""]?claude-code['""]?\r?$", File.ReadAllText(yamlPath));
        Assert.Equal("claude-code", ReadEnvironment(yamlPath));

        // null = an edit that does not touch the environment.
        Assert.True(svc.RegisterProject(Request(localPath, null, true)).Success);
        Assert.Equal("claude-code", ReadEnvironment(yamlPath));

        // "" = back to the global default.
        Assert.True(svc.RegisterProject(Request(localPath, "", true)).Success);
        Assert.DoesNotContain("environment:", File.ReadAllText(yamlPath));
        Assert.Equal(string.Empty, ReadEnvironment(yamlPath));
    }

    [Theory]
    [InlineData("bad id")]
    [InlineData("x\ninjected: true")]
    public void Registration_InvalidEnvironment_Rejected(string environment)
    {
        var (svc, yamlPath, localPath) = NewRegistration();

        var result = svc.RegisterProject(Request(localPath, environment, false));

        Assert.False(result.Success);
        Assert.False(File.Exists(yamlPath));
    }

    // ------------------------------------------------------------- launching

    private ProjectDefinition LocalProject(string? environment = null)
    {
        var path = Path.Combine(_root, "work's dir");
        Directory.CreateDirectory(path);
        var project = new ProjectDefinition { Id = "p1", DisplayName = "My Project", ProjectRootPath = path };
        project.Locations.LocalPath = path;
        project.Launch.Environment = environment!;
        return project;
    }

    private static ProjectDefinition SshProject(string sshTarget, string environment)
    {
        var project = new ProjectDefinition { Id = "p1", DisplayName = "Remote" };
        project.Locations.SshTarget = sshTarget;
        project.Launch.Environment = environment;
        return project;
    }

    private static ToolSettings LaunchSettings(LaunchEnvironmentCatalog? catalog = null, string terminal = @"C:\wt\wt.exe")
    {
        return new ToolSettings
        {
            VsCodeCommand = @"C:\VSCode\bin\code.cmd",
            TerminalCommand = terminal,
            PowerShellCommand = @"C:\pwsh\pwsh.exe",
            SshCommand = "ssh",
            LaunchEnvironments = catalog ?? LaunchEnvironmentCatalog.CreateDefault(@"C:\VSCode\bin\code.cmd")
        };
    }

    private static (LaunchResult Result, ProcessStartInfo? Info) Launch(ToolSettings settings, ProjectDefinition project)
    {
        ProcessStartInfo? captured = null;
        var result = new WindowsLaunchService(settings, info => captured = info).Launch(project, LaunchTargetKind.Code);
        return (result, captured);
    }

    private static string DecodeScript(ProcessStartInfo info)
    {
        const string marker = "-EncodedCommand ";
        var index = info.Arguments.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Missing -EncodedCommand in: " + info.Arguments);
        var encoded = info.Arguments.Substring(index + marker.Length).Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    }

    [Fact]
    public void Launch_DefaultEnvironment_KeepsExistingVsCodeCommandLine()
    {
        var project = LocalProject();

        var (result, info) = Launch(LaunchSettings(), project);

        Assert.True(result.Success);
        Assert.Equal(@"C:\VSCode\bin\code.cmd", info!.FileName);
        Assert.Equal("--new-window \"" + project.Locations.LocalPath + "\"", info.Arguments);
    }

    [Fact]
    public void Launch_UnknownProjectEnvironment_UsesGlobalDefault()
    {
        var project = LocalProject("uninstalled-tool");

        var (result, info) = Launch(LaunchSettings(), project);

        Assert.True(result.Success);
        Assert.Equal(@"C:\VSCode\bin\code.cmd", info!.FileName);
    }

    [Fact]
    public void Launch_CustomEditor_UsesItsCommandAndArgs()
    {
        var catalog = new LaunchEnvironmentCatalog(
            LaunchEnvironmentCatalog.CreateBuiltIns("code")
                .Append(new LaunchEnvironment("cursor", "Cursor", LaunchEnvironmentKind.Editor, @"C:\Cursor\cursor.exe", "--profile work")),
            "cursor");
        var project = LocalProject();

        var (result, info) = Launch(LaunchSettings(catalog), project);

        Assert.True(result.Success);
        Assert.Equal(@"C:\Cursor\cursor.exe", info!.FileName);
        Assert.Equal("--new-window --profile work \"" + project.Locations.LocalPath + "\"", info.Arguments);
    }

    [Fact]
    public void Launch_TerminalEnvironment_OpensWindowsTerminalWithPowerShell()
    {
        var project = LocalProject("copilot-cli");

        var (result, info) = Launch(LaunchSettings(), project);

        Assert.True(result.Success);
        Assert.Equal("Opened GitHub Copilot CLI", result.Message);
        Assert.Equal(@"C:\wt\wt.exe", info!.FileName);
        Assert.StartsWith(
            "-w new new-tab --title \"My Project - GitHub Copilot CLI\" --suppressApplicationTitle \"C:\\pwsh\\pwsh.exe\" -NoLogo -NoExit -EncodedCommand ",
            info.Arguments);
        Assert.Equal(project.Locations.LocalPath, info.WorkingDirectory);

        var expectedPath = project.Locations.LocalPath.Replace("'", "''");
        Assert.Equal("Set-Location -LiteralPath '" + expectedPath + "'; & 'copilot'", DecodeScript(info));
    }

    [Fact]
    public void Launch_TerminalEnvironment_WithoutWindowsTerminal_StartsPowerShellDirectly()
    {
        var project = LocalProject("claude-code");

        var (result, info) = Launch(LaunchSettings(terminal: ""), project);

        Assert.True(result.Success);
        Assert.Equal(@"C:\pwsh\pwsh.exe", info!.FileName);
        Assert.StartsWith("-NoLogo -NoExit -EncodedCommand ", info.Arguments);
        Assert.EndsWith("& 'claude'", DecodeScript(info));
    }

    [Fact]
    public void Launch_TerminalEnvironment_TitleIsSanitized()
    {
        var project = LocalProject("copilot-cli");
        project.DisplayName = "Evil\" & calc \"";

        var (_, info) = Launch(LaunchSettings(), project);

        Assert.Contains("--title \"Evil  calc  - GitHub Copilot CLI\" ", info!.Arguments);
    }

    [Fact]
    public void Launch_TerminalEnvironment_SshPosixTarget()
    {
        var project = SshProject("devbox:/home/me/repo", "copilot-cli");

        var (result, info) = Launch(LaunchSettings(), project);

        Assert.True(result.Success);
        Assert.Equal("Opened GitHub Copilot CLI over SSH", result.Message);
        Assert.Equal("& 'ssh' -t 'devbox' 'cd ''/home/me/repo'' && copilot'", DecodeScript(info!));
    }

    [Fact]
    public void Launch_TerminalEnvironment_SshWindowsTarget_KeepsUserAndSkipsStoreAliases()
    {
        var project = SshProject(@"devuser@winbox:C:\src\repo", "claude-code");

        var (result, info) = Launch(LaunchSettings(), project);

        Assert.True(result.Success);
        const string prefix = "& 'ssh' -t 'devuser@winbox' 'powershell.exe -NoLogo -NoProfile -EncodedCommand ";
        var script = DecodeScript(info!);
        Assert.StartsWith(prefix, script);
        Assert.EndsWith("'", script);
        var remote = Encoding.Unicode.GetString(Convert.FromBase64String(
            script.Substring(prefix.Length, script.Length - prefix.Length - 1)));
        Assert.StartsWith(@"Set-Location -LiteralPath 'C:\src\repo'", remote);
        Assert.Contains("Get-Command -Name 'claude' -CommandType Application -All", remote);
        Assert.Contains(@"-notlike '*\WindowsApps\*'", remote);
        Assert.Contains("& $c.Source", remote);
    }

    [Fact]
    public void Launch_TerminalEnvironment_SshWindowsTarget_RemoteCommandUsedVerbatim()
    {
        var catalog = new LaunchEnvironmentCatalog(
            LaunchEnvironmentCatalog.CreateBuiltIns("code")
                .Append(new LaunchEnvironment("remote-tool", "Tool", LaunchEnvironmentKind.Terminal, "tool", remoteCommand: "tool --x")),
            "vscode");
        var project = SshProject(@"winbox:C:\src\repo", "remote-tool");

        var (result, info) = Launch(LaunchSettings(catalog), project);

        Assert.True(result.Success);
        Assert.Equal(@"& 'ssh' -t 'winbox' 'cd /d C:\src\repo && tool --x'", DecodeScript(info!));
    }

    [Theory]
    [InlineData("devbox:/home/me/it's", "copilot-cli")]
    [InlineData("winbox:/x & powershell -enc AAAA & echo", "copilot-cli")]
    [InlineData("winbox:/x | calc", "claude-code")]
    [InlineData(@"winbox:C:\x;calc", "copilot-cli")]
    [InlineData(@"winbox:C:\x$(calc)", "claude-code")]
    [InlineData("winbox:C:\\x`calc", "copilot-cli")]
    [InlineData(@"winbox:C:\x%PATH%", "copilot-cli")]
    [InlineData("devbox:/srv/$(id)", "copilot-cli")]
    public void Launch_TerminalEnvironment_SshPathWithShellMetacharacters_Rejected(string sshTarget, string environment)
    {
        var project = SshProject(sshTarget, environment);

        var (result, info) = Launch(LaunchSettings(), project);

        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Rejected, result.Status);
        Assert.Null(info);
    }

    [Fact]
    public void Launch_TerminalEnvironment_SshPathWithMetacharacters_RejectedEvenWithRemoteCommand()
    {
        var catalog = new LaunchEnvironmentCatalog(
            LaunchEnvironmentCatalog.CreateBuiltIns("code")
                .Append(new LaunchEnvironment("remote-tool", "Tool", LaunchEnvironmentKind.Terminal, "tool", remoteCommand: "tool --x")),
            "vscode");
        var project = SshProject(@"winbox:C:\x;calc", "remote-tool");

        var (result, info) = Launch(LaunchSettings(catalog), project);

        Assert.Equal(LaunchStatus.Rejected, result.Status);
        Assert.Null(info);
    }

    [Fact]
    public void Launch_TerminalEnvironment_SshPathWithSpaces_Allowed()
    {
        var project = SshProject("devbox:/home/me/my repo", "copilot-cli");

        var (result, info) = Launch(LaunchSettings(), project);

        Assert.True(result.Success);
        Assert.Equal("& 'ssh' -t 'devbox' 'cd ''/home/me/my repo'' && copilot'", DecodeScript(info!));
    }

    [Fact]
    public void Launch_TerminalEnvironment_SshNonBareCommandWithoutRemoteCommand_Unconfigured()
    {
        var catalog = new LaunchEnvironmentCatalog(
            LaunchEnvironmentCatalog.CreateBuiltIns("code")
                .Append(new LaunchEnvironment("local-script", "Script", LaunchEnvironmentKind.Terminal, @"C:\tools\run.ps1")),
            "vscode");
        var project = SshProject("devbox:/home/me/repo", "local-script");

        var (result, info) = Launch(LaunchSettings(catalog), project);

        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Unconfigured, result.Status);
        Assert.Null(info);
    }

    [Fact]
    public void Launch_TerminalEnvironment_SshUsesRemoteCommand()
    {
        var catalog = new LaunchEnvironmentCatalog(
            LaunchEnvironmentCatalog.CreateBuiltIns("code")
                .Append(new LaunchEnvironment("local-script", "Script", LaunchEnvironmentKind.Terminal, @"C:\tools\run.ps1", remoteCommand: "~/bin/run")),
            "vscode");
        var project = SshProject("devbox:/srv/app", "local-script");

        var (result, info) = Launch(LaunchSettings(catalog), project);

        Assert.True(result.Success);
        Assert.Equal("& 'ssh' -t 'devbox' 'cd ''/srv/app'' && ~/bin/run'", DecodeScript(info!));
    }

    // ---------------------------------------------------------- icon lookup

    [Fact]
    public void IconLocator_VsCodeShimOnPath_FindsParentExecutable()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\VSCode\bin\code.cmd",
            @"C:\VSCode\code.exe"
        };
        var env = LaunchEnvironmentCatalog.CreateDefault().Find("vscode");

        var candidates = LaunchEnvironmentIconLocator.GetCandidates(env!, files.Contains, @"C:\Windows;C:\VSCode\bin");

        Assert.Equal(new[] { @"C:\VSCode\code.exe" }, candidates);
    }

    [Fact]
    public void IconLocator_PrefersConfiguredIconThenExeOnPath()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            @"C:\icons\copilot.ico",
            @"C:\tools\copilot.exe"
        };
        var env = new LaunchEnvironment("copilot-cli", "Copilot", LaunchEnvironmentKind.Terminal, "copilot", iconPath: @"C:\icons\copilot.ico");

        var candidates = LaunchEnvironmentIconLocator.GetCandidates(env, files.Contains, @"C:\missing;C:\tools");

        Assert.Equal(new[] { @"C:\icons\copilot.ico", @"C:\tools\copilot.exe" }, candidates);
    }

    [Fact]
    public void IconLocator_RelativeCommandWithSeparator_ReturnsNothing()
    {
        var env = new LaunchEnvironment("x", "X", LaunchEnvironmentKind.Terminal, @"..\tools\x.exe");

        var candidates = LaunchEnvironmentIconLocator.GetCandidates(env, _ => true, @"C:\tools");

        Assert.Empty(candidates);
    }
}
