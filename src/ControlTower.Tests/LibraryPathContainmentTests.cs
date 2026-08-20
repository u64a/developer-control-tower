using ControlTower.Core.Models;
using ControlTower.Infrastructure.Library;
using ControlTower.Infrastructure.Ssh;

namespace ControlTower.Tests;

public class LibraryPathContainmentTests
{
    [Fact]
    public void ResolveRemoteTarget_PosixParentEscapeIsRejected()
    {
        var success = LibraryPathContainment.TryResolveRemoteTarget(
            "/srv/projects/demo",
            "../../.ssh",
            remoteIsWindows: false,
            out _,
            out var issue);

        Assert.False(success);
        Assert.Contains("escapes", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ResolveRemoteTarget_WindowsParentEscapeIsRejected()
    {
        var success = LibraryPathContainment.TryResolveRemoteTarget(
            @"D:\Projects\Demo",
            @"..\..",
            remoteIsWindows: true,
            out _,
            out var issue);

        Assert.False(success);
        Assert.Contains("escapes", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(false, "/etc")]
    [InlineData(false, @"\etc")]
    [InlineData(false, @"C:\Temp")]
    [InlineData(false, @"\\server\share")]
    [InlineData(true, "/Windows")]
    [InlineData(true, @"\Windows")]
    [InlineData(true, @"C:Temp")]
    [InlineData(true, @"C:\Temp")]
    [InlineData(true, @"\\server\share")]
    public void ResolveRemoteTarget_RootedDriveAndUncTargetsAreRejected(
        bool remoteIsWindows,
        string target)
    {
        var projectRoot = remoteIsWindows
            ? @"D:\Projects\Demo"
            : "/srv/projects/demo";

        var success = LibraryPathContainment.TryResolveRemoteTarget(
            projectRoot,
            target,
            remoteIsWindows,
            out _,
            out var issue);

        Assert.False(success);
        Assert.Contains("relative", issue, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(
        false,
        "/srv/work items/demo",
        "docs with spaces/./guides/../api",
        "/srv/work items/demo/docs with spaces/api")]
    [InlineData(
        true,
        @"D:\Work Items\Demo",
        @"docs with spaces\.\guides\..\api",
        @"D:\Work Items\Demo\docs with spaces\api")]
    public void ResolveRemoteTarget_ValidNestedTargetsAreNormalized(
        bool remoteIsWindows,
        string projectRoot,
        string target,
        string expected)
    {
        var success = LibraryPathContainment.TryResolveRemoteTarget(
            projectRoot,
            target,
            remoteIsWindows,
            out var resolved,
            out var issue);

        Assert.True(success, issue);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void ValidateRemotePath_UsesDetectedOperatingSystemCaseRules()
    {
        Assert.True(LibraryPathContainment.TryValidateRemotePathWithinRoot(
            @"D:\Projects\Demo",
            @"d:\projects\demo\docs",
            remoteIsWindows: true,
            allowEqual: false,
            out _,
            out _));

        Assert.False(LibraryPathContainment.TryValidateRemotePathWithinRoot(
            "/srv/Projects/Demo",
            "/srv/projects/demo/docs",
            remoteIsWindows: false,
            allowEqual: false,
            out _,
            out _));
    }

    [Fact]
    public void ValidateRemotePath_RelativeRootDoesNotContainParentEscape()
    {
        Assert.False(LibraryPathContainment.TryValidateRemotePathWithinRoot(
            ".",
            "../.ssh",
            remoteIsWindows: false,
            allowEqual: false,
            out _,
            out _));
    }

    [Theory]
    [InlineData(@"docs\.. \outside")]
    [InlineData(@"docs\. \outside")]
    [InlineData(@"docs\folder.\file.txt")]
    [InlineData(@"docs\folder \file.txt")]
    [InlineData(@"docs\file.txt:stream")]
    [InlineData(@"docs\NUL.txt")]
    public void ResolveRemoteTarget_WindowsWin32AmbiguousSegmentsAreRejected(
        string target)
    {
        var success = LibraryPathContainment.TryResolveRemoteTarget(
            @"D:\Projects\Demo",
            target,
            remoteIsWindows: true,
            out _,
            out _);

        Assert.False(success);
    }

    [Fact]
    public void ResolveRemoteTarget_PosixDoesNotApplyWin32SegmentRules()
    {
        var success = LibraryPathContainment.TryResolveRemoteTarget(
            "/srv/projects/demo",
            "docs./file:stream",
            remoteIsWindows: false,
            out var resolved,
            out var issue);

        Assert.True(success, issue);
        Assert.Equal("/srv/projects/demo/docs./file:stream", resolved);
    }

    [Theory]
    [InlineData(@"D:\Projects\Demo\docs\.. \outside")]
    [InlineData(@"D:\Projects\Demo\docs\folder.\file.txt")]
    [InlineData(@"D:\Projects\Demo\docs\file.txt:stream")]
    [InlineData(@"\\?\D:\Projects\Demo\docs\file.txt")]
    [InlineData(@"\\.\D:\Projects\Demo\docs\file.txt")]
    [InlineData(@"\??\D:\Projects\Demo\docs\file.txt")]
    [InlineData(@"\Device\HarddiskVolume1\Projects\Demo\docs\file.txt")]
    public void ValidateRemotePath_WindowsUnsafeCanonicalFormsAreRejected(
        string candidate)
    {
        var success = LibraryPathContainment.TryValidateRemotePathWithinRoot(
            @"D:\Projects\Demo",
            candidate,
            remoteIsWindows: true,
            allowEqual: false,
            out _,
            out _);

        Assert.False(success);
    }

    [Fact]
    public void ApplyPush_RevalidatesRemoteContainmentBeforeConnecting()
    {
        var service = new SshAssetTransferService(
            storeProvider: null,
            credentialStore: null,
            sshClientFactory: new SshNetClientFactory(hostKeyPolicy: null));
        var plan = new AssetPushPlan
        {
            Asset = new LibraryAsset { AbsoluteRoot = @"C:\library\skills\demo" },
            TargetRoot = "host.example:/srv/projects/demo",
            ResolvedTargetPath = "host.example:/srv/projects/demo/assets",
            RemoteIsWindows = false,
        };
        plan.Changes.Add(new FileChange
        {
            RelativePath = "README.md",
            SourceAbsolutePath = @"C:\library\skills\demo\README.md",
            TargetAbsolutePath =
                "/srv/projects/demo/assets/../../.ssh/authorized_keys",
            Kind = FileChangeKind.New,
            Apply = true,
        });

        var result = service.ApplyPush(plan);

        Assert.False(result.Success);
        Assert.Contains("containment", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("outside", result.Message, StringComparison.OrdinalIgnoreCase);
    }
}
