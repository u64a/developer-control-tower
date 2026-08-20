using ControlTower.Core.Composition;
using ControlTower.Core.Models;

namespace ControlTower.Tests;

/// <summary>
/// Focused pure-function tests for <see cref="SshStoreResolver.TryResolve"/> (ADR-010).
/// All tests are in-memory: no IO, no SSH connections, no file system access.
/// </summary>
public class SshStoreResolverTests
{
    // ---- Helpers ----------------------------------------------------------

    private static RepoStore SshStore(string id, string host, string root, string user = "devuser") =>
        new() { Id = id, Type = "ssh", Host = host, Root = root, User = user };

    // ---- ADR-010 case 1: Exact match, single SSH store, Windows path ------

    [Fact]
    public void TryResolve_ExactMatch_WindowsBackslashPath_ReturnsStoreIdAndFolder()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repos") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@192.168.64.10:d:\repos\myproject",
            stores, out var storeId, out var folder);

        Assert.True(result);
        Assert.Equal("devbox", storeId);
        Assert.Equal("myproject", folder);
    }

    // ---- ADR-010 case 2: Exact match, forward-slash (POSIX) path ----------

    [Fact]
    public void TryResolve_ExactMatch_ForwardSlashPath_ReturnsMatch()
    {
        var stores = new[] { SshStore("lnxbox", "linuxhost", "/srv/repos") };

        var result = SshStoreResolver.TryResolve(
            "devuser@linuxhost:/srv/repos/myproj",
            stores, out var storeId, out var folder);

        Assert.True(result);
        Assert.Equal("lnxbox", storeId);
        Assert.Equal("myproj", folder);
    }

    // ---- ADR-010 case 4: No match - wrong host ----------------------------

    [Fact]
    public void TryResolve_NoMatch_WrongHost_ReturnsFalse()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repos") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@10.0.0.1:d:\repos\proj",
            stores, out var storeId, out var folder);

        Assert.False(result);
        Assert.Equal(string.Empty, storeId);
        Assert.Equal(string.Empty, folder);
    }

    // ---- ADR-010 case 5: No match - wrong root (partial-segment guard) ----

    [Fact]
    public void TryResolve_NoMatch_WrongRoot_PartialSegmentNotTreatedAsPrefix()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repo") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@192.168.64.10:d:\repos\proj",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- ADR-010 case 6: No match - nested path (more than one segment) --

    [Fact]
    public void TryResolve_NoMatch_NestedPath_ReturnsFalse()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repos") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@192.168.64.10:d:\repos\parent\child",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- ADR-010 case 7: No match - empty folder (trailing separator only)

    [Fact]
    public void TryResolve_NoMatch_TrailingSeparatorMakesEmptyFolder_ReturnsFalse()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repos") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@192.168.64.10:d:\repos\",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- ADR-010 case 9: Ambiguous match - two SSH stores, both match -----

    [Fact]
    public void TryResolve_AmbiguousMatch_TwoMatchingStores_ReturnsFalse()
    {
        var stores = new[]
        {
            SshStore("devbox-a", "192.168.64.10", @"d:\repos"),
            SshStore("devbox-b", "192.168.64.10", @"d:\repos")
        };

        var result = SshStoreResolver.TryResolve(
            @"devuser@192.168.64.10:d:\repos\proj",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- ADR-010 case 10: Null or empty SSH target --------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryResolve_NullOrEmptySshTarget_ReturnsFalse(string? target)
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repos") };
        Assert.False(SshStoreResolver.TryResolve(target, stores, out _, out _));
    }

    // ---- ADR-010 case 11: Username mismatch and blank configured user -----

    [Fact]
    public void TryResolve_DifferentUser_Rejected()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repos", user: "devuser") };

        var result = SshStoreResolver.TryResolve(
            @"alice@192.168.64.10:d:\repos\proj",
            stores, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolve_BlankConfiguredUser_RejectedWhenTargetHasUser()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repos", user: "") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@192.168.64.10:d:\repos\proj",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- ADR-010 case 12: Case-insensitive host matching -----------------

    [Fact]
    public void TryResolve_CaseInsensitiveHost_UppercaseHostMatches()
    {
        var stores = new[] { SshStore("devbox", "myserver.local", @"d:\repos") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@MYSERVER.LOCAL:d:\repos\proj",
            stores, out var storeId, out var folder);

        Assert.True(result);
        Assert.Equal("devbox", storeId);
        Assert.Equal("proj", folder);
    }

    // ---- ADR-010 case 13: Windows case-insensitive root; folder casing preserved

    [Fact]
    public void TryResolve_WindowsRoot_CaseInsensitive_FolderCasingPreserved()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"D:\Repos") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@192.168.64.10:d:\repos\MyProject",
            stores, out var storeId, out var folder);

        Assert.True(result);
        Assert.Equal("devbox", storeId);
        Assert.Equal("MyProject", folder);
    }

    // ---- ADR-010 revision: POSIX case-sensitive semantics ----

    [Fact]
    public void TryResolve_PosixRoot_CaseMismatch_ReturnsFalse()
    {
        var stores = new[] { SshStore("lnxbox", "linuxhost", "/srv/repos") };

        var result = SshStoreResolver.TryResolve(
            "devuser@linuxhost:/srv/Repos/myproj",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- ADR-010 revision: mixed-style rejection ----

    [Fact]
    public void TryResolve_MixedPosixRootWindowsTarget_ReturnsFalse()
    {
        var stores = new[] { SshStore("lnxbox", "linuxhost", "/srv/repos") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@linuxhost:d:\repos\proj",
            stores, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolve_MixedWindowsRootPosixTarget_ReturnsFalse()
    {
        var stores = new[] { SshStore("devbox", "192.168.64.10", @"d:\repos") };

        var result = SshStoreResolver.TryResolve(
            "devuser@192.168.64.10:/opt/repos/proj",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- ADR-010 revision: POSIX filesystem root "/" as store root ----

    [Fact]
    public void TryResolve_PosixFilesystemRoot_SingleSegment_ResolvesCorrectly()
    {
        var stores = new[] { SshStore("rootbox", "linuxhost", "/", user: "user") };

        var result = SshStoreResolver.TryResolve(
            "user@linuxhost:/folder",
            stores, out var storeId, out var folder);

        Assert.True(result);
        Assert.Equal("rootbox", storeId);
        Assert.Equal("folder", folder);
    }

    [Fact]
    public void TryResolve_PosixFilesystemRoot_RootOnlyTarget_ReturnsFalse()
    {
        var stores = new[] { SshStore("rootbox", "linuxhost", "/", user: "user") };

        var result = SshStoreResolver.TryResolve(
            "user@linuxhost:/",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- POSIX backslash rejection: backslash in POSIX target is invalid ----

    [Fact]
    public void TryResolve_PosixRoot_BackslashInTarget_ReturnsFalse()
    {
        // Regression: /srv/repos + target with backslash must NOT resolve.
        var stores = new[] { SshStore("lnxbox", "linuxhost", "/srv/repos") };

        var result = SshStoreResolver.TryResolve(
            "devuser@linuxhost:/srv/repos\x5cproject",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- Ambiguous UPN/IPv6: stores with '@' in User or ':' in Host are skipped ----

    [Fact]
    public void TryResolve_UpnUser_StoreSkipped_ReturnsFalse()
    {
        var stores = new[] { SshStore("corp", "192.168.64.10", @"d:\repos", user: "user@domain.com") };

        var result = SshStoreResolver.TryResolve(
            @"user@domain.com@192.168.64.10:d:\repos\proj",
            stores, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolve_Ipv6Host_StoreSkipped_ReturnsFalse()
    {
        var stores = new[] { SshStore("v6box", "::1", "/srv/repos", user: "dev") };

        var result = SshStoreResolver.TryResolve(
            "dev@::1:/srv/repos/proj",
            stores, out _, out _);

        Assert.False(result);
    }

    // ---- Relative roots: style inferred from target separators ----

    [Fact]
    public void TryResolve_RelativeRoot_PosixTarget_CaseSensitive_Resolves()
    {
        var stores = new[] { SshStore("rel", "linuxhost", "repos") };

        var result = SshStoreResolver.TryResolve(
            "devuser@linuxhost:repos/project",
            stores, out var storeId, out var folder);

        Assert.True(result);
        Assert.Equal("rel", storeId);
        Assert.Equal("project", folder);
    }

    [Fact]
    public void TryResolve_RelativeRoot_PosixTarget_CaseSensitiveMismatch_ReturnsFalse()
    {
        // Relative + POSIX-inferred: case-sensitive comparison.
        var stores = new[] { SshStore("rel", "linuxhost", "Repos") };

        var result = SshStoreResolver.TryResolve(
            "devuser@linuxhost:repos/project",
            stores, out _, out _);

        Assert.False(result);
    }

    [Fact]
    public void TryResolve_RelativeRoot_WindowsTarget_CaseInsensitive_Resolves()
    {
        // Backslash in target -> Windows inferred -> case-insensitive.
        var stores = new[] { SshStore("rel", "winhost", "Repos") };

        var result = SshStoreResolver.TryResolve(
            "devuser@winhost:repos\x5cproject",
            stores, out var storeId, out var folder);

        Assert.True(result);
        Assert.Equal("rel", storeId);
        Assert.Equal("project", folder);
    }

    // ---- Short Host: stores with trimmed Host < 2 chars are skipped ----

    [Theory]
    [InlineData("x")]
    [InlineData("1")]
    [InlineData(" ")]
    [InlineData("")]
    public void TryResolve_OneCharHost_StoreSkipped_ReturnsFalse(string host)
    {
        var stores = new[] { SshStore("short", host, @"d:\repos") };

        var result = SshStoreResolver.TryResolve(
            @"devuser@x:d:\repos\proj",
            stores, out _, out _);

        Assert.False(result);
    }
}
