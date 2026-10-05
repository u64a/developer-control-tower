using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Launch;
using ControlTower.Infrastructure.Registration;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

public class CopilotAutostartTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dct-copilot-" + Guid.NewGuid().ToString("N"));

    public CopilotAutostartTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------ argument building

    [Fact]
    public void Disabled_ProducesNoArguments()
    {
        var autostart = new CopilotAutostart { Yolo = true, AgentName = "squad" };

        Assert.True(autostart.TryBuildArguments(out var tokens, out var error));
        Assert.Empty(tokens);
        Assert.Null(error);
        Assert.Equal(string.Empty, autostart.Describe("copilot"));
    }

    [Fact]
    public void Enabled_DefaultsToResumeOnly()
    {
        var autostart = new CopilotAutostart { Enabled = true };

        Assert.True(autostart.TryBuildArguments(out var tokens, out _));
        Assert.Equal(new[] { "--resume" }, tokens);
    }

    [Fact]
    public void Enabled_YoloAgentAndResume()
    {
        var autostart = new CopilotAutostart { Enabled = true, Yolo = true, AgentName = "squad" };

        Assert.True(autostart.TryBuildArguments(out var tokens, out _));
        Assert.Equal(new[] { "--yolo", "--agent", "squad", "--resume" }, tokens);
        Assert.Equal("copilot --yolo --agent squad --resume", autostart.Describe("copilot"));
    }

    [Fact]
    public void Enabled_NamedSessionReplacesResume()
    {
        var autostart = new CopilotAutostart
        {
            Enabled = true,
            SessionMode = CopilotSessionMode.Name,
            SessionName = "NewSession"
        };

        Assert.True(autostart.TryBuildArguments(out var tokens, out _));
        Assert.Equal(new[] { "--name", "NewSession" }, tokens);
        Assert.DoesNotContain("--resume", tokens);
    }

    [Fact]
    public void NamedSession_WithoutName_IsRefused()
    {
        var autostart = new CopilotAutostart { Enabled = true, SessionMode = CopilotSessionMode.Name };

        Assert.False(autostart.TryBuildArguments(out _, out var error));
        Assert.Contains("Enter a session name", error);
    }

    [Theory]
    [InlineData("my session")]
    [InlineData("name;calc.exe")]
    [InlineData("na'me")]
    [InlineData("$(whoami)")]
    [InlineData("a&b")]
    [InlineData("-leading-hyphen")]
    [InlineData("../escape")]
    public void InjectionShapedNames_AreRefused(string name)
    {
        Assert.False(CopilotAutostart.IsValidName(name));

        var session = new CopilotAutostart
        {
            Enabled = true,
            SessionMode = CopilotSessionMode.Name,
            SessionName = name
        };
        Assert.False(session.TryBuildArguments(out _, out _));

        var agent = new CopilotAutostart { Enabled = true, AgentName = name };
        Assert.False(agent.TryBuildArguments(out _, out _));
    }

    [Theory]
    [InlineData("squad")]
    [InlineData("Session-1")]
    [InlineData("my_session.v2")]
    public void AllowlistedNames_AreAccepted(string name)
    {
        Assert.True(CopilotAutostart.IsValidName(name));
    }

    [Fact]
    public void NameLongerThanSixtyFourCharacters_IsRefused()
    {
        Assert.False(CopilotAutostart.IsValidName(new string('a', 65)));
        Assert.True(CopilotAutostart.IsValidName(new string('a', 64)));
    }

    [Fact]
    public void UnconfiguredOptions_AreDefault()
    {
        Assert.True(new CopilotAutostart().IsDefault);
        Assert.False(new CopilotAutostart { Enabled = true }.IsDefault);
        Assert.False(new CopilotAutostart { Yolo = true }.IsDefault);
    }

    // ----------------------------------------------------------- persistence

    [Fact]
    public void Registration_RoundTripsOptionsAndPreservesThemOnUnrelatedEdits()
    {
        var (svc, yamlPath, localPath) = NewRegistration();

        var request = Request(localPath);
        request.CopilotAutostart = new CopilotAutostart
        {
            Enabled = true,
            SessionMode = CopilotSessionMode.Name,
            SessionName = "NewSession",
            AgentName = "squad",
            Yolo = true
        };
        Assert.True(svc.RegisterProject(request).Success);

        var reloaded = Load(yamlPath).Launch.CopilotAutostart;
        Assert.True(reloaded.Enabled);
        Assert.Equal(CopilotSessionMode.Name, reloaded.SessionMode);
        Assert.Equal("NewSession", reloaded.SessionName);
        Assert.Equal("squad", reloaded.AgentName);
        Assert.True(reloaded.Yolo);

        // null = an edit that does not touch the autostart options.
        var untouched = Request(localPath);
        untouched.AllowOverwrite = true;
        Assert.True(svc.RegisterProject(untouched).Success);
        Assert.True(Load(yamlPath).Launch.CopilotAutostart.Enabled);
    }

    [Fact]
    public void Registration_OmitsBlockWhenNothingConfigured()
    {
        var (svc, yamlPath, localPath) = NewRegistration();

        var request = Request(localPath);
        request.CopilotAutostart = new CopilotAutostart();
        Assert.True(svc.RegisterProject(request).Success);

        Assert.DoesNotContain("copilot_autostart", File.ReadAllText(yamlPath));
        Assert.False(Load(yamlPath).Launch.CopilotAutostart.Enabled);
    }

    [Fact]
    public void NewProject_DefaultsToNoAutostart()
    {
        var (svc, yamlPath, localPath) = NewRegistration();
        Assert.True(svc.RegisterProject(Request(localPath)).Success);

        var autostart = Load(yamlPath).Launch.CopilotAutostart;
        Assert.NotNull(autostart);
        Assert.False(autostart.Enabled);
        Assert.False(autostart.Yolo);
        Assert.Equal(CopilotSessionMode.Resume, autostart.SessionMode);
    }

    [Fact]
    public void Yaml_DropsNamesThatAreNotAllowlisted()
    {
        var dir = Path.Combine(_root, "handedited");
        Directory.CreateDirectory(Path.Combine(dir, ".controltower"));
        File.WriteAllText(Path.Combine(dir, ".controltower", "project.yml"), string.Join("\n", new[]
        {
            "id: p1",
            "display_name: Project One",
            "launch:",
            "  copilot_autostart:",
            "    enabled: true",
            "    session_mode: name",
            "    session_name: \"a; calc.exe\"",
            "    agent: \"$(whoami)\"",
            "    yolo: true"
        }));

        var result = new ProjectYamlProvider().LoadProject(dir);
        var autostart = result.Project.Launch.CopilotAutostart;

        Assert.Equal(string.Empty, autostart.SessionName);
        Assert.Equal(string.Empty, autostart.AgentName);
        Assert.Contains(result.Issues, i => i.Code == "project/copilot-autostart/session-name");
        Assert.Contains(result.Issues, i => i.Code == "project/copilot-autostart/agent");
    }

    [Fact]
    public void Yaml_UnknownSessionModeFallsBackToResume()
    {
        var dir = Path.Combine(_root, "badmode");
        Directory.CreateDirectory(Path.Combine(dir, ".controltower"));
        File.WriteAllText(Path.Combine(dir, ".controltower", "project.yml"), string.Join("\n", new[]
        {
            "id: p1",
            "display_name: Project One",
            "launch:",
            "  copilot_autostart:",
            "    enabled: true",
            "    session_mode: nonsense"
        }));

        var autostart = new ProjectYamlProvider().LoadProject(dir).Project.Launch.CopilotAutostart;
        Assert.Equal(CopilotSessionMode.Resume, autostart.SessionMode);
    }

    // ---------------------------------------------------------------- launch

    [Fact]
    public void Editor_WithAutostart_OpensEditorThenCopilotTerminal()
    {
        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart
        {
            Enabled = true,
            Yolo = true,
            AgentName = "squad"
        };

        var started = Launch(project, out var result);

        Assert.True(result.Success);
        Assert.Equal(2, started.Count);
        Assert.Contains("--new-window", started[0].Arguments);

        var script = DecodeScript(started[1]);
        Assert.Contains("Set-Location -LiteralPath '" + _root + "'", script);
        Assert.Contains("& 'copilot' '--yolo' '--agent' 'squad' '--resume'", script);
        Assert.Contains("copilot --yolo --agent squad --resume", result.Message);
    }

    [Fact]
    public void Editor_WithoutAutostart_OpensEditorOnly()
    {
        var started = Launch(LocalProject(), out var result);

        Assert.True(result.Success);
        Assert.Single(started);
    }

    [Fact]
    public void Editor_WithAutostartButNoLocalFolder_SkipsCopilot()
    {
        // An SSH-only project opens remotely; a local Copilot CLI session
        // would be pointed at the wrong machine.
        var project = new ProjectDefinition { Id = "p1", DisplayName = "Remote" };
        project.Locations.SshTarget = "devbox:/home/me/repo";
        project.Launch.VsCodeSsh = "vscode-remote://ssh-remote+devbox/home/me/repo";
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true };

        var started = Launch(project, out var result);

        Assert.True(result.Success);
        Assert.Single(started);
    }

    [Fact]
    public void CopilotTerminal_WithAutostart_AppendsArgumentsToTheSameSession()
    {
        var project = LocalProject();
        project.Launch.Environment = LaunchEnvironmentCatalog.CopilotCliId;
        project.Launch.CopilotAutostart = new CopilotAutostart
        {
            Enabled = true,
            SessionMode = CopilotSessionMode.Name,
            SessionName = "NewSession"
        };

        var started = Launch(project, out var result);

        Assert.True(result.Success);
        Assert.Single(started);
        Assert.Contains("& 'copilot' '--name' 'NewSession'", DecodeScript(started[0]));
    }

    [Fact]
    public void CopilotTerminal_WithUnusableOptions_IsRejected()
    {
        var project = LocalProject();
        project.Launch.Environment = LaunchEnvironmentCatalog.CopilotCliId;
        project.Launch.CopilotAutostart = new CopilotAutostart
        {
            Enabled = true,
            SessionMode = CopilotSessionMode.Name,
            SessionName = string.Empty
        };

        var started = Launch(project, out var result);

        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Rejected, result.Status);
        Assert.Equal("launch/rejected/copilot-autostart", result.Issue!.Code);
        Assert.Empty(started);
    }

    [Fact]
    public void NonCopilotTerminal_IgnoresAutostart()
    {
        var project = LocalProject();
        project.Launch.Environment = LaunchEnvironmentCatalog.ClaudeCodeId;
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true, Yolo = true };

        var started = Launch(project, out var result);

        Assert.True(result.Success);
        Assert.Single(started);
        Assert.DoesNotContain("--yolo", DecodeScript(started[0]));
    }

    [Fact]
    public void CodeAdmin_WithAutostart_OpensElevatedEditorThenCopilotTerminal()
    {
        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart
        {
            Enabled = true,
            Yolo = true,
            AgentName = "squad"
        };

        var started = Launch(project, LaunchTargetKind.CodeAdmin, out var result);

        Assert.True(result.Success);
        Assert.Equal(2, started.Count);
        Assert.Equal("runas", started[0].Verb);
        Assert.Contains("--new-window", started[0].Arguments);

        // The Copilot session deliberately inherits the caller's token rather
        // than elevating: the CLI never needs Administrator to run.
        Assert.NotEqual("runas", started[1].Verb);
        Assert.Contains("& 'copilot' '--yolo' '--agent' 'squad' '--resume'", DecodeScript(started[1]));
        Assert.Contains("copilot --yolo --agent squad --resume", result.Message);
    }

    [Fact]
    public void CodeAdmin_WithoutAutostart_OpensElevatedEditorOnly()
    {
        var started = Launch(LocalProject(), LaunchTargetKind.CodeAdmin, out var result);

        Assert.True(result.Success);
        Assert.Single(started);
        Assert.Equal("runas", started[0].Verb);
    }

    [Fact]
    public void CodeAdmin_WithAutostartButNoLocalFolder_SkipsCopilot()
    {
        var project = new ProjectDefinition { Id = "p1", DisplayName = "Remote" };
        project.Locations.SshTarget = "devbox:/home/me/repo";
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true };

        var started = Launch(project, LaunchTargetKind.CodeAdmin, out var result);

        Assert.False(result.Success);
        Assert.Empty(started);
    }

    // ------------------------------------------------- integrated terminal

    [Fact]
    public void IntegratedTerminal_WritesTaskAndSkipsSideTerminal()
    {
        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart
        {
            Enabled = true,
            Yolo = true,
            AgentName = "squad",
            UseIntegratedTerminal = true
        };

        var started = Launch(project, out var result);

        Assert.True(result.Success);
        Assert.Single(started);
        Assert.Contains("--new-window", started[0].Arguments);

        var tasks = JsonNode.Parse(File.ReadAllText(TasksPath()))!.AsObject();
        Assert.Equal("2.0.0", tasks["version"]!.GetValue<string>());

        var task = tasks["tasks"]!.AsArray().Single()!.AsObject();
        Assert.Equal(VsCodeTaskFile.TaskLabel, task["label"]!.GetValue<string>());
        Assert.Equal("copilot", task["command"]!.GetValue<string>());
        Assert.Equal("folderOpen", task["runOptions"]!["runOn"]!.GetValue<string>());
        Assert.Equal(
            new[] { "--yolo", "--agent", "squad", "--resume" },
            task["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray());
        Assert.Contains("in its terminal", result.Message);
    }

    [Fact]
    public void IntegratedTerminal_PreservesExistingUserTasks()
    {
        WriteTasksJson("""
        {
          // a comment, which VS Code allows
          "version": "2.0.0",
          "tasks": [ { "label": "build", "type": "shell", "command": "dotnet build" } ]
        }
        """);

        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true, UseIntegratedTerminal = true };

        Launch(project, out var result);

        Assert.True(result.Success);
        var labels = JsonNode.Parse(File.ReadAllText(TasksPath()))!["tasks"]!.AsArray()
            .Select(t => t!["label"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "build", VsCodeTaskFile.TaskLabel }, labels);
    }

    [Fact]
    public void IntegratedTerminal_RewritingDoesNotDuplicateTheTask()
    {
        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true, UseIntegratedTerminal = true };

        Launch(project, out _);
        Launch(project, out _);

        var tasks = JsonNode.Parse(File.ReadAllText(TasksPath()))!["tasks"]!.AsArray();
        Assert.Single(tasks);
    }

    [Fact]
    public void IntegratedTerminal_UnparseableFileIsLeftAlone()
    {
        WriteTasksJson("{ this is not json");

        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true, UseIntegratedTerminal = true };

        var started = Launch(project, out var result);

        Assert.True(result.Success);
        Assert.Single(started);
        Assert.Equal("{ this is not json", File.ReadAllText(TasksPath()));
        Assert.Contains("could not be parsed", result.Message);
    }

    [Fact]
    public void ClearingIntegratedTerminal_RemovesOnlyTheGeneratedTask()
    {
        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true, UseIntegratedTerminal = true };
        Launch(project, out _);

        // The user clears the checkbox: the task must stop firing, but any
        // task they wrote themselves has to survive.
        var tasks = JsonNode.Parse(File.ReadAllText(TasksPath()))!.AsObject();
        tasks["tasks"]!.AsArray().Add(new JsonObject { ["label"] = "build", ["command"] = "dotnet build" });
        File.WriteAllText(TasksPath(), tasks.ToJsonString());

        project.Launch.CopilotAutostart.UseIntegratedTerminal = false;
        Launch(project, out var result);

        Assert.True(result.Success);
        var labels = JsonNode.Parse(File.ReadAllText(TasksPath()))!["tasks"]!.AsArray()
            .Select(t => t!["label"]!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "build" }, labels);
    }

    [Fact]
    public void IntegratedTerminal_AddsGitExcludeEntryOnce()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git", "info"));

        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true, UseIntegratedTerminal = true };

        Launch(project, out _);
        Launch(project, out _);

        var exclude = File.ReadAllLines(Path.Combine(_root, ".git", "info", "exclude"));
        Assert.Single(exclude, l => l.Trim() == "/.vscode/tasks.json");
    }

    [Fact]
    public void IntegratedTerminal_WithoutGitRepository_StillWritesTheTask()
    {
        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart { Enabled = true, UseIntegratedTerminal = true };

        Launch(project, out var result);

        Assert.True(result.Success);
        Assert.True(File.Exists(TasksPath()));
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")));
    }

    [Fact]
    public void IntegratedTerminal_InvalidName_StartsNothingAndExplains()
    {
        var project = LocalProject();
        project.Launch.CopilotAutostart = new CopilotAutostart
        {
            Enabled = true,
            UseIntegratedTerminal = true,
            SessionMode = CopilotSessionMode.Name,
            SessionName = string.Empty
        };

        var started = Launch(project, out var result);

        Assert.True(result.Success);
        Assert.Single(started);
        Assert.False(File.Exists(TasksPath()));
        Assert.Contains("Enter a session name", result.Message);
    }

    [Fact]
    public void IntegratedTerminal_RoundTripsThroughProjectYaml()
    {
        var (service, yamlPath, localPath) = NewRegistration();
        var request = Request(localPath);
        request.CopilotAutostart = new CopilotAutostart
        {
            Enabled = true,
            UseIntegratedTerminal = true,
            AgentName = "squad"
        };

        Assert.True(service.RegisterProject(request).Success);

        var autostart = Load(yamlPath).Launch.CopilotAutostart;
        Assert.True(autostart.UseIntegratedTerminal);
        Assert.Equal("squad", autostart.AgentName);
    }

    // ------------------------------------------- environment compatibility

    [Theory]
    [InlineData(LaunchEnvironmentCatalog.VsCodeId, true)]
    [InlineData(LaunchEnvironmentCatalog.CopilotCliId, true)]
    [InlineData(LaunchEnvironmentCatalog.ClaudeCodeId, false)]
    public void BuiltInEnvironments_ReportCopilotSupport(string id, bool expected)
    {
        var environment = LaunchEnvironmentCatalog.CreateDefault().Find(id)!;
        Assert.Equal(expected, environment.SupportsCopilotAutostart);
    }

    [Fact]
    public void CustomTerminalRunningCopilot_SupportsAutostart()
    {
        var environment = new LaunchEnvironment(
            "my-copilot", "My Copilot", LaunchEnvironmentKind.Terminal, @"C:\tools\copilot.exe");

        Assert.True(environment.IsCopilotCli);
        Assert.True(environment.SupportsCopilotAutostart);
    }

    [Fact]
    public void CustomTerminalRunningAnotherAgent_DoesNotSupportAutostart()
    {
        var environment = new LaunchEnvironment(
            "gemini", "Gemini CLI", LaunchEnvironmentKind.Terminal, "gemini");

        Assert.False(environment.IsCopilotCli);
        Assert.False(environment.SupportsCopilotAutostart);
    }

    [Fact]
    public void CustomEditor_SupportsAutostartRegardlessOfCommand()
    {
        var environment = new LaunchEnvironment(
            "cursor", "Cursor", LaunchEnvironmentKind.Editor, "cursor");

        Assert.False(environment.IsCopilotCli);
        Assert.True(environment.SupportsCopilotAutostart);
    }

    // --------------------------------------------------------------- helpers

    private string TasksPath() => Path.Combine(_root, ".vscode", "tasks.json");

    private void WriteTasksJson(string content)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".vscode"));
        File.WriteAllText(TasksPath(), content);
    }

    private ProjectDefinition LocalProject()
    {
        var project = new ProjectDefinition
        {
            Id = "p1",
            DisplayName = "Project One",
            ProjectRootPath = _root
        };
        project.Locations.LocalPath = _root;
        project.Launch.VsCodeLocal = _root;
        return project;
    }

    private static List<ProcessStartInfo> Launch(ProjectDefinition project, out LaunchResult result)
    {
        return Launch(project, LaunchTargetKind.Code, out result);
    }

    private static List<ProcessStartInfo> Launch(
        ProjectDefinition project, LaunchTargetKind targetKind, out LaunchResult result)
    {
        var started = new List<ProcessStartInfo>();
        var svc = new WindowsLaunchService(new ToolSettings(), started.Add);
        result = svc.Launch(project, targetKind);
        return started;
    }

    private static string DecodeScript(ProcessStartInfo info)
    {
        const string marker = "-EncodedCommand ";
        var index = info.Arguments.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(index >= 0, "Expected an encoded PowerShell command in: " + info.Arguments);
        var encoded = info.Arguments.Substring(index + marker.Length).Trim();
        return Encoding.Unicode.GetString(Convert.FromBase64String(encoded));
    }

    private (ProjectRegistrationService Service, string YamlPath, string LocalPath) NewRegistration()
    {
        var localPath = Path.Combine(_root, "p1");
        Directory.CreateDirectory(localPath);
        var svc = new ProjectRegistrationService(Path.Combine(_root, "portfolio.yml"));
        var yamlPath = Path.Combine(_root, "portfolio-projects", "p1", ".controltower", "project.yml");
        return (svc, yamlPath, localPath);
    }

    private static ProjectDefinition Load(string yamlPath)
    {
        var dir = Path.GetDirectoryName(Path.GetDirectoryName(yamlPath))!;
        return new ProjectYamlProvider().LoadProject(dir).Project;
    }

    private static ProjectRegistrationRequest Request(string localPath) => new()
    {
        ProjectId = "p1",
        DisplayName = "Project One",
        LocalPath = localPath,
        AllowOverwrite = true
    };
}
