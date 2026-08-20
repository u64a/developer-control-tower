using System;
using System.Diagnostics;
using System.IO;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Launch;

namespace ControlTower.Tests;

public class WindowsLaunchServiceTests
{
    private static ProjectDefinition NewProjectAt(string root)
    {
        var project = new ProjectDefinition
        {
            Id = "test",
            DisplayName = "Test",
            ProjectRootPath = root
        };
        project.Locations.LocalPath = root;
        return project;
    }

    [Fact]
    public void Launch_NullProject_ReturnsUnconfigured()
    {
        var svc = new WindowsLaunchService();
        var result = svc.Launch(null, LaunchTargetKind.GitHub);
        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Unconfigured, result.Status);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("mailto:user@example.com")]
    [InlineData("ftp://example.com/")]
    [InlineData("ms-settings:network")]
    public void Launch_GitHub_BlocksUnsupportedSchemes(string url)
    {
        var project = new ProjectDefinition();
        project.Launch.GitHub = url;

        var svc = new WindowsLaunchService();
        var result = svc.Launch(project, LaunchTargetKind.GitHub);

        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Rejected, result.Status);
        Assert.NotNull(result.Issue);
        Assert.StartsWith("launch/rejected/", result.Issue.Code);
    }

    [Fact]
    public void Launch_GitHub_AllowsHttps()
    {
        // Verify the allowlist accepts https without ever invoking the real
        // browser by injecting a no-op process starter. The test captures the
        // ProcessStartInfo that *would* have been launched and asserts on it.
        var project = new ProjectDefinition();
        project.Launch.GitHub = "https://github.com/example/repo";

        ProcessStartInfo? captured = null;
        var svc = new WindowsLaunchService(new ToolSettings(), info => captured = info);
        var result = svc.Launch(project, LaunchTargetKind.GitHub);

        Assert.True(result.Success);
        Assert.NotEqual(LaunchStatus.Rejected, result.Status);
        Assert.NotNull(captured);
        Assert.Equal("https://github.com/example/repo", captured.FileName);
    }

    [Fact]
    public void Launch_GitHub_BlocksEmbeddedCredentials()
    {
        var project = new ProjectDefinition();
        project.Launch.GitHub = "https:/" + "/user:pass@github.com/example";

        var svc = new WindowsLaunchService();
        var result = svc.Launch(project, LaunchTargetKind.GitHub);

        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Rejected, result.Status);
        Assert.Equal("launch/rejected/embedded-credentials", result.Issue.Code);
    }

    [Fact]
    public void Launch_Doc_RejectsUncPath()
    {
        var dir = CreateProjectDir();
        try
        {
            var project = NewProjectAt(dir);
            project.Docs.Add(new DocLink { Url = @"\\evilhost\share\payload.md" });

            var svc = new WindowsLaunchService();
            var result = svc.Launch(project, LaunchTargetKind.PrimaryDoc);

            Assert.False(result.Success);
            Assert.Equal(LaunchStatus.Rejected, result.Status);
            // Could be unc rejection, or scheme rejection if the URI parser
            // sees the \\host\share form as a file:// URL. Either way, the
            // launch must be rejected — never followed.
            Assert.True(
                result.Issue.Code == "launch/rejected/unc" ||
                result.Issue.Code == "launch/rejected/scheme" ||
                result.Issue.Code == "launch/rejected/path",
                $"unexpected code {result.Issue.Code}");
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Launch_Doc_RejectsParentTraversal()
    {
        var dir = CreateProjectDir();
        try
        {
            var project = NewProjectAt(dir);
            project.Docs.Add(new DocLink { Url = @"..\..\Windows\System32\drivers\etc\hosts" });

            var svc = new WindowsLaunchService();
            var result = svc.Launch(project, LaunchTargetKind.PrimaryDoc);

            Assert.False(result.Success);
            Assert.Equal(LaunchStatus.Rejected, result.Status);
            Assert.Equal("launch/rejected/traversal", result.Issue.Code);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Launch_Doc_RejectsPathOutsideRoot()
    {
        var dir = CreateProjectDir();
        try
        {
            var project = NewProjectAt(dir);
            project.Docs.Add(new DocLink { Url = @"C:\Windows\notepad.exe" });

            var svc = new WindowsLaunchService();
            var result = svc.Launch(project, LaunchTargetKind.PrimaryDoc);

            Assert.False(result.Success);
            Assert.Equal(LaunchStatus.Rejected, result.Status);
            // Could be outside-root or extension; both are valid rejections.
            Assert.StartsWith("launch/rejected/", result.Issue.Code);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Launch_Doc_RejectsExecutableExtension()
    {
        var dir = CreateProjectDir();
        try
        {
            var fake = Path.Combine(dir, "evil.exe");
            File.WriteAllText(fake, "");

            var project = NewProjectAt(dir);
            project.Docs.Add(new DocLink { Url = "evil.exe" });

            var svc = new WindowsLaunchService();
            var result = svc.Launch(project, LaunchTargetKind.PrimaryDoc);

            Assert.False(result.Success);
            Assert.Equal(LaunchStatus.Rejected, result.Status);
            Assert.Equal("launch/rejected/extension", result.Issue.Code);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Launch_Doc_RejectsUnknownExtension()
    {
        var dir = CreateProjectDir();
        try
        {
            var fake = Path.Combine(dir, "notes.weird");
            File.WriteAllText(fake, "");

            var project = NewProjectAt(dir);
            project.Docs.Add(new DocLink { Url = "notes.weird" });

            var svc = new WindowsLaunchService();
            var result = svc.Launch(project, LaunchTargetKind.PrimaryDoc);

            Assert.False(result.Success);
            Assert.Equal(LaunchStatus.Rejected, result.Status);
            Assert.Equal("launch/rejected/extension", result.Issue.Code);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Launch_Doc_UnconfiguredWhenNoDocs()
    {
        var dir = CreateProjectDir();
        try
        {
            var project = NewProjectAt(dir);

            var svc = new WindowsLaunchService();
            var result = svc.Launch(project, LaunchTargetKind.PrimaryDoc);

            Assert.False(result.Success);
            Assert.Equal(LaunchStatus.Unconfigured, result.Status);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Launch_GitHub_UnconfiguredWhenEmpty()
    {
        var project = new ProjectDefinition();
        var svc = new WindowsLaunchService();
        var result = svc.Launch(project, LaunchTargetKind.GitHub);
        Assert.Equal(LaunchStatus.Unconfigured, result.Status);
    }

    [Fact]
    public void Launch_GitHub_HttpRequiresOptIn()
    {
        var project = new ProjectDefinition();
        project.Launch.GitHub = "http://example.com/";

        var svc = new WindowsLaunchService(new ToolSettings { AllowHttpLinks = false });
        var result = svc.Launch(project, LaunchTargetKind.GitHub);

        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Rejected, result.Status);
        Assert.Equal("launch/rejected/scheme", result.Issue.Code);
    }

    [Fact]
    public void Launch_Plan_RejectsLocalSourceRefWhenRootless()
    {
        // SSH-only / root-less project: an absolute-or-relative local
        // Planning.SourceRef must not be resolved against the process working
        // directory. With no local root there is nothing to confine it to, so
        // the launch is rejected rather than opening an arbitrary local file.
        var project = new ProjectDefinition { Id = "ssh-only", DisplayName = "SSH Only" };
        project.Planning.SourceRef = @"roadmap-notes.md";

        var svc = new WindowsLaunchService();
        var result = svc.Launch(project, LaunchTargetKind.Plan);

        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Rejected, result.Status);
        Assert.Equal("launch/rejected/no-root", result.Issue.Code);
    }

    [Fact]
    public void Launch_Doc_RejectsLocalPathWhenRootless()
    {
        var project = new ProjectDefinition { Id = "ssh-only", DisplayName = "SSH Only" };
        project.Docs.Add(new DocLink { Url = "notes.md" });

        var svc = new WindowsLaunchService();
        var result = svc.Launch(project, LaunchTargetKind.PrimaryDoc);

        Assert.False(result.Success);
        Assert.Equal(LaunchStatus.Rejected, result.Status);
        Assert.Equal("launch/rejected/no-root", result.Issue.Code);
    }

    private static string CreateProjectDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-launch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, true); } catch { }
    }
}
