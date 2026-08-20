using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Infrastructure.FileSystem;

namespace ControlTower.Tests;

/// <summary>
/// Tests for the local→local leg of <see cref="RelocateFileTransfer"/>
/// (the only leg that runs without a real SSH server). They exercise
/// recursive copy, path-traversal rejection, large-file skipping, and
/// the SSH→SSH guard.
/// </summary>
public class RelocateFileTransferTests : IDisposable
{
    private readonly string _root;

    public RelocateFileTransferTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ctrelocate-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task CopyDirectory_LocalToLocal_CopiesAllFilesRecursively()
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(Path.Combine(src, ".controltower", "nested"));
        File.WriteAllText(Path.Combine(src, ".controltower", "project.yml"), "id: sample\n");
        File.WriteAllText(Path.Combine(src, ".controltower", "nested", "child.txt"), "x");

        var sut = new RelocateFileTransfer();
        var result = await sut.CopyDirectoryAsync(
            new RelocateEndpoint { IsSsh = false, LocalPath = src },
            new RelocateEndpoint { IsSsh = false, LocalPath = dst },
            ".controltower",
            long.MaxValue,
            CancellationToken.None);

        Assert.True(result.Success, "Expected copy to succeed: " + result.ErrorMessage);
        Assert.Equal(2, result.FilesCopied);
        Assert.True(File.Exists(Path.Combine(dst, ".controltower", "project.yml")));
        Assert.True(File.Exists(Path.Combine(dst, ".controltower", "nested", "child.txt")));
    }

    [Fact]
    public async Task CopyDirectory_RefusesSshToSsh()
    {
        var sut = new RelocateFileTransfer();
        var result = await sut.CopyDirectoryAsync(
            new RelocateEndpoint { IsSsh = true, Host = "h1", User = "u", Password = "p", RemotePath = "/a" },
            new RelocateEndpoint { IsSsh = true, Host = "h2", User = "u", Password = "p", RemotePath = "/b" },
            ".controltower",
            long.MaxValue,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("SSH→SSH", result.ErrorMessage);
    }

    [Fact]
    public async Task CopyDirectory_LocalToSshWithoutPolicy_FailsClosed()
    {
        var src = Path.Combine(_root, "src");
        Directory.CreateDirectory(src);

        var sut = new RelocateFileTransfer();
        var result = await sut.CopyDirectoryAsync(
            new RelocateEndpoint { IsSsh = false, LocalPath = src },
            new RelocateEndpoint
            {
                IsSsh = true,
                Host = "host",
                User = "user",
                Password = "password",
                RemotePath = "/destination",
            },
            string.Empty,
            long.MaxValue,
            CancellationToken.None);

        Assert.False(result.Success);
        Assert.Contains("No host-key policy is configured", result.ErrorMessage);
    }

    [Fact]
    public async Task CopyDirectory_LocalToLocal_SkipsFilesOverSizeCap()
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(Path.Combine(src, "data"));
        File.WriteAllText(Path.Combine(src, "data", "small.txt"), "ok");
        File.WriteAllBytes(Path.Combine(src, "data", "big.bin"), new byte[2 * 1024 * 1024]);

        var sut = new RelocateFileTransfer();
        var result = await sut.CopyDirectoryAsync(
            new RelocateEndpoint { IsSsh = false, LocalPath = src },
            new RelocateEndpoint { IsSsh = false, LocalPath = dst },
            "data",
            maxFileBytes: 1 * 1024 * 1024,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesCopied);
        Assert.Equal(1, result.FilesSkipped);
        Assert.Single(result.Warnings);
        Assert.True(File.Exists(Path.Combine(dst, "data", "small.txt")));
        Assert.False(File.Exists(Path.Combine(dst, "data", "big.bin")));
    }

    [Fact]
    public async Task CopyFiles_LocalToLocal_CopiesListAndIgnoresPathTraversal()
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "ok.txt"), "ok");

        var attackTarget = Path.Combine(_root, "ATTACK.txt");
        File.WriteAllText(attackTarget, "should never be written");

        var sut = new RelocateFileTransfer();
        var result = await sut.CopyFilesAsync(
            new RelocateEndpoint { IsSsh = false, LocalPath = src },
            new RelocateEndpoint { IsSsh = false, LocalPath = dst },
            new[] { "ok.txt", "../ATTACK.txt", "/absolute/no.txt" },
            long.MaxValue,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1, result.FilesCopied);
        Assert.True(File.Exists(Path.Combine(dst, "ok.txt")));
        // The pre-existing attack target must not have been overwritten by
        // the traversal entry (it would have ended up with "ok" content if
        // the guard had been bypassed).
        Assert.Equal("should never be written", File.ReadAllText(attackTarget));
    }

    [Fact]
    public async Task CopyFiles_OverwritesExistingDestination()
    {
        var src = Path.Combine(_root, "src");
        var dst = Path.Combine(_root, "dst");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dst);
        File.WriteAllText(Path.Combine(src, "a.txt"), "new content");
        File.WriteAllText(Path.Combine(dst, "a.txt"), "stale content");

        var sut = new RelocateFileTransfer();
        var result = await sut.CopyFilesAsync(
            new RelocateEndpoint { IsSsh = false, LocalPath = src },
            new RelocateEndpoint { IsSsh = false, LocalPath = dst },
            new[] { "a.txt" },
            long.MaxValue,
            CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal("new content", File.ReadAllText(Path.Combine(dst, "a.txt")));
    }
}
