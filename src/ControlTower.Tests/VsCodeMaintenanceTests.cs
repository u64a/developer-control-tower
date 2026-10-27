using System.Diagnostics;
using ControlTower.Core.Composition;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Launch;
using ControlTower.Infrastructure.Registration;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Tests;

public sealed class VsCodeMaintenanceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "dct-maintenance-" + Guid.NewGuid().ToString("N"));

    public VsCodeMaintenanceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RunningEditor_SkipsAllUpdatesAndStillLaunches(bool updateCode, bool updateExtensions)
    {
        var updates = new List<string>();
        var maintenance = new VsCodePreLaunchMaintenance(
            () => "VS Code is already running.",
            () => { updates.Add("editor"); return new(true, "Updated editor."); },
            _ => { updates.Add("extensions"); return new(true, "Updated extensions."); });
        var started = new List<ProcessStartInfo>();
        var project = LocalProject();
        project.Launch.VsCodeMaintenance = new() { UpdateVsCode = updateCode, UpdateExtensions = updateExtensions };

        var result = new WindowsLaunchService(new ToolSettings(), started.Add, maintenance)
            .Launch(project, LaunchTargetKind.Code);

        Assert.True(result.Success);
        Assert.Contains("skipped", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("already running", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(updates);
        Assert.Single(started);
        Assert.False(project.Launch.CopilotAutostart.Enabled);
    }

    [Fact]
    public void DetectionFailure_FailsClosedButAllowsNormalLaunch()
    {
        var maintenance = new VsCodePreLaunchMaintenance(
            () => throw new InvalidOperationException("Cannot inspect processes"),
            () => throw new Xunit.Sdk.XunitException("Updater must not run"),
            _ => throw new Xunit.Sdk.XunitException("Extension updater must not run"));

        var result = maintenance.Run(new() { UpdateVsCode = true, UpdateExtensions = true }, "code");

        Assert.True(result.CanLaunch);
        Assert.Contains("skipped", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OptionsOff_DoNotEvenInspectProcesses()
    {
        var maintenance = new VsCodePreLaunchMaintenance(
            () => throw new Xunit.Sdk.XunitException("Default launch must not inspect processes"),
            () => throw new Xunit.Sdk.XunitException("Default launch must not update"),
            _ => throw new Xunit.Sdk.XunitException("Default launch must not update extensions"));

        var result = maintenance.Run(new(), "code");

        Assert.True(result.CanLaunch);
        Assert.Empty(result.Message);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingMaintenance_BlocksLaunchEvenWhenOptionsAreOff(bool enabled)
    {
        var pending = true;
        var maintenance = new VsCodePreLaunchMaintenance(
            () => "VS Code is already running.",
            () => throw new Xunit.Sdk.XunitException("Cannot start another updater"),
            _ => throw new Xunit.Sdk.XunitException("Cannot start another extension updater"),
            () => pending);
        var options = new VsCodeMaintenance { UpdateVsCode = enabled, UpdateExtensions = enabled };

        var result = maintenance.Run(options, "code");

        Assert.False(result.CanLaunch);
        Assert.Contains("still running", result.Message);
        pending = false;
        Assert.True(maintenance.Run(options, "code").CanLaunch);
    }

    [Theory]
    [InlineData(true, false, "editor,launch")]
    [InlineData(false, true, "extensions,launch")]
    [InlineData(true, true, "editor,extensions,launch")]
    public void Updates_RunInOrderBeforeEditorWithoutCopilot(bool updateCode, bool updateExtensions, string expected)
    {
        var events = new List<string>();
        var maintenance = new VsCodePreLaunchMaintenance(
            () => string.Empty,
            () => { events.Add("editor"); return new(true, "Editor ready."); },
            command => { Assert.Equal("code", command); events.Add("extensions"); return new(true, "Extensions ready."); });
        var project = LocalProject();
        project.Launch.VsCodeMaintenance = new() { UpdateVsCode = updateCode, UpdateExtensions = updateExtensions };

        var result = new WindowsLaunchService(new ToolSettings(), _ => events.Add("launch"), maintenance)
            .Launch(project, LaunchTargetKind.Code);

        Assert.True(result.Success);
        Assert.Equal(expected, string.Join(",", events));
    }

    [Fact]
    public void EditorAppearingBetweenSteps_SkipsExtensions()
    {
        var running = false;
        var extensionCalls = 0;
        var maintenance = new VsCodePreLaunchMaintenance(
            () => running ? "VS Code is already running." : string.Empty,
            () => { running = true; return new(true, "Editor ready."); },
            _ => { extensionCalls++; return new(true, string.Empty); });

        var result = maintenance.Run(new() { UpdateVsCode = true, UpdateExtensions = true }, "code");

        Assert.True(result.CanLaunch);
        Assert.Equal(0, extensionCalls);
        Assert.Contains("skipped", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StillRunningUpdater_PreventsEditorAndExtensionLaunch()
    {
        var started = new List<ProcessStartInfo>();
        var maintenance = new VsCodePreLaunchMaintenance(
            () => string.Empty,
            () => new(false, "VS Code updater is still running."),
            _ => throw new Xunit.Sdk.XunitException("Extensions must wait for installer completion"));

        var result = new WindowsLaunchService(new ToolSettings(), started.Add, maintenance)
            .Launch(LocalProject(), LaunchTargetKind.Code);

        Assert.False(result.Success);
        Assert.Contains("still running", result.Message);
        Assert.Empty(started);
    }

    [Theory]
    [InlineData("copilot-cli")]
    [InlineData("claude-code")]
    [InlineData("custom-editor")]
    public void NonVsCodeEnvironment_DoesNotRunMaintenance(string environment)
    {
        var catalog = new LaunchEnvironmentCatalog(
            LaunchEnvironmentCatalog.CreateBuiltIns("code").Append(
                new LaunchEnvironment("custom-editor", "Other editor", LaunchEnvironmentKind.Editor, "other-editor")),
            "vscode");
        var started = new List<ProcessStartInfo>();
        var project = LocalProject();
        project.Launch.Environment = environment;
        var settings = new ToolSettings { LaunchEnvironments = catalog };

        var result = new WindowsLaunchService(settings, started.Add, new UnexpectedMaintenance())
            .Launch(project, LaunchTargetKind.Code);

        Assert.True(result.Success);
        Assert.Single(started);
    }

    [Fact]
    public void RemoteOnlyProject_DoesNotMaintainLocalEditor()
    {
        var project = LocalProject();
        project.Locations.LocalPath = string.Empty;
        project.Launch.VsCodeLocal = string.Empty;
        project.Locations.SshTarget = "devbox:/repo";
        var started = new List<ProcessStartInfo>();

        var result = new WindowsLaunchService(new ToolSettings(), started.Add, new UnexpectedMaintenance())
            .Launch(project, LaunchTargetKind.Code);

        Assert.True(result.Success);
        Assert.Contains("--folder-uri", Assert.Single(started).Arguments);
    }

    [Fact]
    public void AdministratorLaunch_WithDefaultOptionsKeepsExistingBehavior()
    {
        var project = LocalProject();
        project.Launch.VsCodeMaintenance = new();
        var started = new List<ProcessStartInfo>();

        var result = new WindowsLaunchService(new ToolSettings(), started.Add, new UnexpectedMaintenance())
            .Launch(project, LaunchTargetKind.CodeAdmin);

        Assert.True(result.Success);
        Assert.DoesNotContain("updates", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("runas", Assert.Single(started).Verb);
    }

    [Fact]
    public void Registration_PreservesIndependentOptionsOnUnrelatedEditAndCanClearThem()
    {
        var service = new ProjectRegistrationService(Path.Combine(_root, "portfolio.yml"));
        var request = new ProjectRegistrationRequest
        {
            ProjectId = "test-project",
            DisplayName = "Test project",
            LocalPath = _root,
            AllowOverwrite = true,
            VsCodeMaintenance = new() { UpdateVsCode = true, UpdateExtensions = true }
        };
        Assert.True(service.RegisterProject(request).Success);
        request.VsCodeMaintenance = null;
        request.CopilotAutostart = new() { Enabled = true };
        Assert.True(service.RegisterProject(request).Success);
        var metadataRoot = Path.Combine(_root, "portfolio-projects", request.ProjectId);
        var loaded = new ProjectYamlProvider().LoadProject(_root, metadataRoot).Project;
        Assert.True(loaded.Launch.VsCodeMaintenance.UpdateVsCode);
        Assert.True(loaded.Launch.VsCodeMaintenance.UpdateExtensions);
        Assert.True(loaded.Launch.CopilotAutostart.Enabled);

        var overview = OverviewComposer.Compose(loaded, null, null, null, null);
        overview.VsCodeMaintenance.UpdateExtensions = false;
        Assert.True(loaded.Launch.VsCodeMaintenance.UpdateExtensions);

        request.VsCodeMaintenance = new();
        Assert.True(service.RegisterProject(request).Success);
        Assert.DoesNotContain("vscode_maintenance",
            File.ReadAllText(Path.Combine(metadataRoot, ".controltower", "project.yml")));
    }

    [Fact]
    public void RepositoryMetadata_CannotOptMachineIntoUpdates()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
        Directory.CreateDirectory(Path.Combine(_root, ".controltower"));
        File.WriteAllText(Path.Combine(_root, ".controltower", "project.yml"), """
            id: test-project
            display_name: Test
            launch:
              vscode_maintenance:
                update_vscode: true
                update_extensions: true
            """);

        var result = new ProjectYamlProvider().LoadProject(_root);

        Assert.True(result.Project.Launch.VsCodeMaintenance.IsDefault);
        Assert.Contains(result.Issues, issue => issue.Code == "project/vscode-maintenance/untrusted");
    }

    private ProjectDefinition LocalProject()
    {
        var project = new ProjectDefinition { Id = "test-project", ProjectRootPath = _root };
        project.Locations.LocalPath = _root;
        project.Launch.VsCodeLocal = _root;
        project.Launch.VsCodeMaintenance = new() { UpdateVsCode = true, UpdateExtensions = true };
        return project;
    }

    private sealed class UnexpectedMaintenance : IVsCodePreLaunchMaintenance
    {
        public VsCodeMaintenanceResult Run(VsCodeMaintenance options, string codeCommand) =>
            throw new Xunit.Sdk.XunitException("This launch must not perform VS Code maintenance");
    }
}
