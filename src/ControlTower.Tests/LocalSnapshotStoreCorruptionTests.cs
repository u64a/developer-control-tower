using System;
using System.IO;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Cache;

namespace ControlTower.Tests;

public class LocalSnapshotStoreCorruptionTests : IDisposable
{
    private readonly string _folder;
    private readonly LocalSnapshotStore _store;

    public LocalSnapshotStoreCorruptionTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "ct-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _store = new LocalSnapshotStore(_folder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* ignore */ }
    }

    private string FilePath(string id) => System.IO.Path.Combine(_folder, id + ".snapshot");

    [Fact]
    public void Load_MissingFile_ReturnsNull_NoIssue()
    {
        var snap = _store.Load("missing", out var issues);
        Assert.Null(snap);
        Assert.Empty(issues);
    }

    [Fact]
    public void Load_RoundTrip_Succeeds()
    {
        var snap = new RepoSnapshot
        {
            RepoPath = @"C:\r",
            IsAvailable = true,
            Branch = "main",
            IsDirty = false,
            HasUpstream = true,
            AheadBy = 1,
            BehindBy = 2,
            LastCommitUtc = new DateTime(2024, 1, 2, 3, 4, 5, DateTimeKind.Utc),
            StatusMessage = "",
            OriginUrl = "https://example/x.git"
        };
        _store.Save("p1", snap);
        var loaded = _store.Load("p1", out var issues);
        Assert.NotNull(loaded);
        Assert.Empty(issues);
        Assert.Equal("main", loaded!.Branch);
        Assert.Equal(1, loaded.AheadBy);
    }

    [Fact]
    public void Load_TruncatedFile_ReturnsNull_AndCacheCorruptIssue()
    {
        // Only a couple of fields present — required keys missing.
        File.WriteAllText(FilePath("p2"), "RepoPath=C:\\foo\nBranch=main\n");
        var snap = _store.Load("p2", out var issues);
        Assert.Null(snap);
        var issue = Assert.Single(issues);
        Assert.Equal("cache/corrupt", issue.Code);
        Assert.Contains("missing", issue.Message);
    }

    [Fact]
    public void Load_InvalidBool_ReturnsNull_AndCacheCorruptIssue()
    {
        var full = string.Join("\n",
            "RepoPath=C:\\r",
            "IsAvailable=yes",       // invalid bool
            "Branch=main",
            "IsDirty=false",
            "HasUpstream=true",
            "AheadBy=0",
            "BehindBy=0",
            "LastCommitUtc=",
            "StatusMessage=",
            "OriginUrl=");
        File.WriteAllText(FilePath("p3"), full);
        var snap = _store.Load("p3", out var issues);
        Assert.Null(snap);
        var issue = Assert.Single(issues);
        Assert.Equal("cache/corrupt", issue.Code);
        Assert.Contains("IsAvailable", issue.Message);
    }

    [Fact]
    public void Load_InvalidDate_ReturnsNull_AndCacheCorruptIssue()
    {
        var full = string.Join("\n",
            "RepoPath=C:\\r",
            "IsAvailable=true",
            "Branch=main",
            "IsDirty=false",
            "HasUpstream=true",
            "AheadBy=0",
            "BehindBy=0",
            "LastCommitUtc=not-a-date",
            "StatusMessage=",
            "OriginUrl=");
        File.WriteAllText(FilePath("p4"), full);
        var snap = _store.Load("p4", out var issues);
        Assert.Null(snap);
        Assert.Contains(issues, i => i.Code == "cache/corrupt" && i.Message.Contains("LastCommitUtc"));
    }

    [Fact]
    public void Load_InvalidInt_ReturnsNull_AndCacheCorruptIssue()
    {
        var full = string.Join("\n",
            "RepoPath=C:\\r",
            "IsAvailable=true",
            "Branch=main",
            "IsDirty=false",
            "HasUpstream=true",
            "AheadBy=NaN",
            "BehindBy=0",
            "LastCommitUtc=",
            "StatusMessage=",
            "OriginUrl=");
        File.WriteAllText(FilePath("p5"), full);
        var snap = _store.Load("p5", out var issues);
        Assert.Null(snap);
        Assert.Contains(issues, i => i.Code == "cache/corrupt" && i.Message.Contains("AheadBy"));
    }

    [Fact]
    public void Load_MalformedLine_ReturnsNull_AndCacheCorruptIssue()
    {
        var full = string.Join("\n",
            "RepoPath=C:\\r",
            "IsAvailable=true",
            "thisIsNotKeyValue",    // no '='
            "Branch=main",
            "IsDirty=false",
            "HasUpstream=true",
            "AheadBy=0",
            "BehindBy=0",
            "LastCommitUtc=",
            "StatusMessage=",
            "OriginUrl=");
        File.WriteAllText(FilePath("p6"), full);
        var snap = _store.Load("p6", out var issues);
        Assert.Null(snap);
        Assert.Contains(issues, i => i.Code == "cache/corrupt");
    }

    [Fact]
    public void Load_EmptyFile_ReturnsNull_AndCacheCorruptIssue()
    {
        File.WriteAllText(FilePath("p7"), string.Empty);
        var snap = _store.Load("p7", out var issues);
        Assert.Null(snap);
        Assert.Contains(issues, i => i.Code == "cache/corrupt");
    }

    [Fact]
    public void Load_DoesNotThrow_OnAnyInput()
    {
        // Garbage bytes; must never throw.
        File.WriteAllBytes(FilePath("p8"), new byte[] { 0x00, 0xFF, 0x10, 0x2A, (byte)'=', 0x7F });
        var ex = Record.Exception(() => _store.Load("p8", out _));
        Assert.Null(ex);
    }
}
