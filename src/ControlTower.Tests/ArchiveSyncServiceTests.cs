using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Archive;

namespace ControlTower.Tests;

public class ArchiveSyncServiceTests
{
    private sealed record GitCall(string[] Arguments, string WorkingDirectory);

    private sealed class FakeGitProcessAdapter : IGitProcessAdapter
    {
        public List<GitCall> Calls { get; } = new();
        public Func<string[], string, GitRunResult>? Handler { get; set; }

        public Task<GitRunResult> RunAsync(
            IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            var args = arguments.ToArray();
            Calls.Add(new GitCall(args, workingDirectory));
            if (Handler == null)
            {
                throw new InvalidOperationException("FakeGitProcessAdapter.Handler was not configured.");
            }

            return Task.FromResult(Handler(args, workingDirectory));
        }
    }

    private sealed class FakeCloneService : IAsyncCloneService
    {
        public List<CloneRequest> Requests { get; } = new();
        public CloneResult NextResult { get; set; } = CloneResult.Ok("main", "sha", "ok");
        public Action<CloneRequest>? OnClone { get; set; }

        public Task<CloneResult> CloneAsync(
            CloneRequest request,
            IProgress<CloneProgress>? progress,
            CancellationToken ct)
        {
            Requests.Add(request);
            OnClone?.Invoke(request);
            return Task.FromResult(NextResult);
        }
    }

    private sealed class FakeStateStore : IArchiveStateStore
    {
        public Dictionary<string, ArchiveRepoState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int LoadCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public ArchiveRepoState? LastSaved { get; private set; }
        public Action<string>? OnLoad { get; set; }

        public ArchiveRepoState Load(string projectId)
        {
            LoadCalls++;
            OnLoad?.Invoke(projectId);
            if (States.TryGetValue(projectId, out var state))
            {
                return state;
            }

            var created = new ArchiveRepoState { ProjectId = projectId ?? string.Empty };
            States[created.ProjectId] = created;
            return created;
        }

        public void Save(ArchiveRepoState state)
        {
            SaveCalls++;
            States[state.ProjectId] = state;
            LastSaved = Clone(state);
        }

        private static ArchiveRepoState Clone(ArchiveRepoState source)
        {
            var clone = new ArchiveRepoState
            {
                ProjectId = source.ProjectId,
                ArchivePath = source.ArchivePath,
                LastSyncedUtc = source.LastSyncedUtc,
                LastSyncedCommitSha = source.LastSyncedCommitSha,
                UpstreamStatus = source.UpstreamStatus,
                LastError = source.LastError,
                AutoSyncEnabled = source.AutoSyncEnabled,
                ReleaseStatus = source.ReleaseStatus,
                LastReleaseTag = source.LastReleaseTag,
                LastReleaseIntegrity = source.LastReleaseIntegrity,
                LastReleaseCapturedUtc = source.LastReleaseCapturedUtc,
                LastReleaseDirectoryName = source.LastReleaseDirectoryName,
                LastReleaseError = source.LastReleaseError,
                UpstreamFullName = source.UpstreamFullName,
                SourceSelectionNote = source.SourceSelectionNote
            };

            foreach (var path in source.PreviousArchivePaths)
            {
                clone.PreviousArchivePaths.Add(path);
            }

            foreach (var name in source.LastReleaseAssetNames)
            {
                clone.LastReleaseAssetNames.Add(name);
            }

            return clone;
        }
    }

    private sealed class FakeArchiveReleaseClient : IArchiveReleaseClient
    {
        public ArchiveUpstreamRepo? UpstreamResult { get; set; }
        public ArchiveReleaseInfo? LatestReleaseResult { get; set; }
        public ArchiveReleaseIntegrity DownloadResult { get; set; } = ArchiveReleaseIntegrity.Verified;
        public Exception? ResolveUpstreamException { get; set; }
        public Func<CancellationToken, Exception?>? ResolveUpstreamExceptionFactory { get; set; }
        public Action<ArchiveReleaseAsset, string>? OnDownload { get; set; }
        public Dictionary<string, ArchiveReleaseInfo?> LatestReleaseResultsByUrl { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> LatestReleaseExceptionsByUrl { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        public List<string> ResolveUpstreamUrls { get; } = new();
        public List<string> LatestReleaseUrls { get; } = new();
        public List<(ArchiveReleaseAsset Asset, string DestinationPath)> Downloads { get; } = new();

        public Task<ArchiveReleaseInfo?> GetLatestReleaseAsync(string remoteUrl, CancellationToken ct)
        {
            LatestReleaseUrls.Add(remoteUrl);
            if (LatestReleaseExceptionsByUrl.TryGetValue(remoteUrl, out var ex))
            {
                return Task.FromException<ArchiveReleaseInfo?>(ex);
            }

            if (LatestReleaseResultsByUrl.TryGetValue(remoteUrl, out var release))
            {
                return Task.FromResult(release);
            }

            return Task.FromResult(LatestReleaseResult);
        }

        public Task<ArchiveReleaseIntegrity> DownloadAndVerifyAsync(
            ArchiveReleaseAsset asset,
            string destinationPath,
            CancellationToken ct)
        {
            Downloads.Add((asset, destinationPath));
            OnDownload?.Invoke(asset, destinationPath);
            return Task.FromResult(DownloadResult);
        }

        public Task<ArchiveUpstreamRepo?> ResolveUpstreamAsync(string remoteUrl, CancellationToken ct)
        {
            ResolveUpstreamUrls.Add(remoteUrl);
            var exception = ResolveUpstreamExceptionFactory?.Invoke(ct) ?? ResolveUpstreamException;
            if (exception != null)
            {
                return Task.FromException<ArchiveUpstreamRepo?>(exception);
            }

            return Task.FromResult(UpstreamResult);
        }
    }

    private static GitRunResult Ok(string stdout = "") =>
        new(0, stdout, string.Empty, TimeSpan.Zero, TimedOut: false, Cancelled: false);

    private static GitRunResult Fail(int exitCode, string stderr) =>
        new(exitCode, string.Empty, stderr, TimeSpan.Zero, TimedOut: false, Cancelled: false);

    private static GitRunResult TimedOut(string stderr) =>
        new(124, string.Empty, stderr, TimeSpan.FromSeconds(30), TimedOut: true, Cancelled: false);

    private static GitRunResult Cancelled(string stderr = "") =>
        new(130, string.Empty, stderr, TimeSpan.Zero, TimedOut: false, Cancelled: true);

    private static ArchiveWorkingSourceInventory EmptyKnownSafeInventory() =>
        ArchiveWorkingSourceInventory.Complete(Array.Empty<ArchiveWorkingSource>());

    private static ArchiveSyncService CreateSafetyService()
    {
        var git = new FakeGitProcessAdapter
        {
            Handler = (_, _) => Ok()
        };
        return new ArchiveSyncService(
            git,
            new FakeCloneService(),
            new FakeStateStore(),
            new FakeArchiveReleaseClient());
    }

    [Fact]
    public async Task SyncAsync_MissingWorkingSourceInventory_FailsClosedBeforeAnyMutation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-missing-inventory-" + Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(root));

        var git = new FakeGitProcessAdapter
        {
            Handler = (_, _) => throw new InvalidOperationException("Git should not run before safety inventory is proven.")
        };
        var clone = new FakeCloneService();
        var state = new FakeStateStore();
        var releases = new FakeArchiveReleaseClient();
        var service = new ArchiveSyncService(git, clone, state, releases);

        var result = await service.SyncAsync(
            new ArchiveSyncRequest(
                "missing-inventory",
                "https://github.com/org/repo.git",
                root),
            progress: null,
            CancellationToken.None);

        Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
        Assert.Contains("inventory", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, state.LoadCalls);
        Assert.Equal(0, state.SaveCalls);
        Assert.Empty(clone.Requests);
        Assert.Empty(git.Calls);
        Assert.Empty(releases.ResolveUpstreamUrls);
        Assert.Empty(releases.LatestReleaseUrls);
        Assert.Empty(releases.Downloads);
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("https://github.com/org/repo.git")]
    [InlineData("https://github.com/fork-owner/fork-repo.git")]
    public async Task SyncAsync_ConflictingWorkingSource_FailsBeforeAnyGitCloneOrStateMutation_ForForkAndNonFork(string remoteUrl)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-conflict-" + Guid.NewGuid().ToString("N"));
        var conflictingSource = Path.Combine(root, "working", "repo");
        Directory.CreateDirectory(conflictingSource);

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (_, _) => throw new InvalidOperationException("Git should not run when source safety fails.")
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            var releases = new FakeArchiveReleaseClient();
            var service = new ArchiveSyncService(git, clone, state, releases);

            var inventory = ArchiveWorkingSourceInventory.Complete(new[]
            {
                new ArchiveWorkingSource("inactive-profile-non-archive", conflictingSource)
            });

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    "archive-target",
                    remoteUrl,
                    root,
                    inventory),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Contains("conflicts with registered working source", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("inactive-profile-non-archive", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, state.LoadCalls);
            Assert.Equal(0, state.SaveCalls);
            Assert.Empty(clone.Requests);
            Assert.Empty(git.Calls);
            Assert.Empty(releases.ResolveUpstreamUrls);
            Assert.Empty(releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData("equal")]
    [InlineData("contains")]
    [InlineData("inside")]
    public async Task SyncAsync_ConflictShapes_BlockBeforeAnyOperations(string conflictShape)
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-root-conflict-shape-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive-root");
        var source = Path.Combine(rootBase, "working-source");

        switch (conflictShape)
        {
            case "equal":
                source = root;
                break;
            case "contains":
                source = Path.Combine(root, "working-source");
                break;
            case "inside":
                root = Path.Combine(source, "archive-root");
                break;
            default:
                throw new InvalidOperationException("Unknown test shape: " + conflictShape);
        }

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(source);

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (_, _) => throw new InvalidOperationException("Git should not run when source safety fails.")
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            var releases = new FakeArchiveReleaseClient();
            var service = new ArchiveSyncService(git, clone, state, releases);

            var inventory = ArchiveWorkingSourceInventory.Complete(new[]
            {
                new ArchiveWorkingSource("local-working-source", source)
            });

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    "archive-target",
                    "https://github.com/org/repo.git",
                    root,
                    inventory),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Contains("conflicts with registered working source", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, state.LoadCalls);
            Assert.Equal(0, state.SaveCalls);
            Assert.Empty(clone.Requests);
            Assert.Empty(git.Calls);
            Assert.Empty(releases.ResolveUpstreamUrls);
            Assert.Empty(releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_RootEqualsSource_Blocks()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-equal-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive-root");
        Directory.CreateDirectory(root);

        try
        {
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("same-path-source", root)
                })));

            Assert.False(result.IsSafe);
            Assert.Contains("conflicts with registered working source", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("same-path-source", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_RootContainsSource_Blocks()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-contains-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive-root");
        var source = Path.Combine(root, "working-source");
        Directory.CreateDirectory(source);

        try
        {
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("under-archive-root", source)
                })));

            Assert.False(result.IsSafe);
            Assert.Contains("conflicts with registered working source", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("under-archive-root", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_RootInsideSource_Blocks()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-inside-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(rootBase, "working-source");
        var root = Path.Combine(source, "archive-root");
        Directory.CreateDirectory(root);

        try
        {
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("source-parent", source)
                })));

            Assert.False(result.IsSafe);
            Assert.Contains("conflicts with registered working source", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("source-parent", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_CaseAndTrailingSeparatorConflict_Blocks()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-case-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive-root");
        Directory.CreateDirectory(root);

        try
        {
            var sourceWithVariantCaseAndSeparator = root.ToUpperInvariant() + Path.DirectorySeparatorChar;
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("case-variant", sourceWithVariantCaseAndSeparator)
                })));

            Assert.False(result.IsSafe);
            Assert.Contains("conflicts with registered working source", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_SamePrefixSiblingPaths_AreAllowed()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-sibling-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive");
        var siblingSource = Path.Combine(rootBase, "archive-source");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(siblingSource);

        try
        {
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("prefix-sibling", siblingSource)
                })));

            Assert.True(result.IsSafe);
            Assert.Equal(Path.GetFullPath(root), result.EffectiveArchiveStorageRoot);
            Assert.Equal(string.Empty, result.Message);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_MultipleSources_LaterConflictBlocksWholeBatch()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-multi-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive-root");
        var safeSource = Path.Combine(rootBase, "safe-source");
        var conflictingSource = Path.Combine(root, "conflicting-source");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(safeSource);
        Directory.CreateDirectory(conflictingSource);

        try
        {
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("safe-project", safeSource),
                    new ArchiveWorkingSource("later-conflict", conflictingSource)
                })));

            Assert.False(result.IsSafe);
            Assert.Contains("later-conflict", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("conflicts with registered working source", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_SourcePathMissingButProvableAncestor_StillComparedAndBlocked()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-missing-source-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive-root");
        Directory.CreateDirectory(root);
        var missingSource = Path.Combine(root, "missing", "working-source");

        try
        {
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("missing-source", missingSource)
                })));

            Assert.False(result.IsSafe);
            Assert.Contains("missing-source", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("conflicts with registered working source", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_UnprovableSourcePath_FailsClosed()
    {
        if (!TryGetUnusedDriveLetter(out var driveLetter))
        {
            return;
        }

        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-unprovable-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive-root");
        Directory.CreateDirectory(root);
        var unprovableSource = driveLetter + @":\working-source";

        try
        {
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("unknown-drive-source", unprovableSource)
                })));

            Assert.False(result.IsSafe);
            Assert.Contains("Cannot prove", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("unknown-drive-source", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_RemoteOnlySourcesWithoutLocalPath_AreIgnored()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-remote-only-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(rootBase, "archive-root");
        Directory.CreateDirectory(root);

        try
        {
            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                root,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("ssh-only-a", string.Empty),
                    new ArchiveWorkingSource("ssh-only-b", "   ")
                })));

            Assert.True(result.IsSafe);
            Assert.Equal(Path.GetFullPath(root), result.EffectiveArchiveStorageRoot);
            Assert.Equal(string.Empty, result.Message);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void ValidateSafety_JunctionAliasToWorkingSource_CannotBypassConflictGuard()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-safety-junction-alias-" + Guid.NewGuid().ToString("N"));
        var physicalSource = Path.Combine(rootBase, "physical-source");
        var aliasParent = Path.Combine(rootBase, "alias-parent");
        var aliasPath = Path.Combine(aliasParent, "source-link");
        Directory.CreateDirectory(physicalSource);
        Directory.CreateDirectory(aliasParent);

        try
        {
            if (!TryCreateJunction(aliasPath, physicalSource))
            {
                return;
            }

            var service = CreateSafetyService();
            var result = service.ValidateSafety(new ArchiveSyncSafetyRequest(
                physicalSource,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("junction-alias-source", aliasPath)
                })));

            Assert.False(result.IsSafe);
            Assert.Contains("junction-alias-source", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(
                result.Message.Contains("Cannot prove", StringComparison.OrdinalIgnoreCase) ||
                result.Message.Contains("conflicts with registered working source", StringComparison.OrdinalIgnoreCase),
                "Expected a fail-closed refusal for the junction alias source.");
        }
        finally
        {
            TryRemoveJunction(aliasPath);
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task SyncAsync_InvalidArchiveStorageRoot_FailsExplicitly()
    {
        var git = new FakeGitProcessAdapter();
        var clone = new FakeCloneService();
        var state = new FakeStateStore();
        var releases = new FakeArchiveReleaseClient();
        var service = new ArchiveSyncService(git, clone, state, releases);

        var result = await service.SyncAsync(
            new ArchiveSyncRequest(
                "p1",
                "https://github.com/org/repo.git",
                @"\\server\share\archive-root",
                EmptyKnownSafeInventory()),
            progress: null,
            CancellationToken.None);

        Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
        Assert.Contains("safe, fully qualified local folder", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, state.LoadCalls);
        Assert.Equal(0, state.SaveCalls);
        Assert.Empty(clone.Requests);
        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task SyncAsync_SafeLocalRootOutsideProfile_DoesNotReapplyLegacyRootRestriction()
    {
        var git = new FakeGitProcessAdapter();
        var clone = new FakeCloneService();
        var state = new FakeStateStore();
        var releases = new FakeArchiveReleaseClient();
        var service = new ArchiveSyncService(git, clone, state, releases);

        var result = await service.SyncAsync(
            new ArchiveSyncRequest(
                "project/one",
                "https://github.com/org/repo.git",
                @"C:\GitHub\Archive",
                EmptyKnownSafeInventory()),
            progress: null,
            CancellationToken.None);

        Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
        Assert.Contains("safe directory name", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, state.LoadCalls);
        Assert.Equal(0, state.SaveCalls);
        Assert.Empty(clone.Requests);
        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task SyncAsync_ArchiveDestinationReparseInjectedAfterInitialResolution_FailsBeforeCloneOrGitMutation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-revalidate-" + Guid.NewGuid().ToString("N"));
        var projectId = "reparse-injected";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        var junctionTarget = Path.Combine(root, "junction-target");

        Directory.CreateDirectory(root);
        Directory.CreateDirectory(junctionTarget);

        var junctionCapabilityProbe = Path.Combine(root, "probe-junction");
        if (!TryCreateJunction(junctionCapabilityProbe, junctionTarget))
        {
            TryDeleteDirectory(root);
            return;
        }

        TryRemoveJunction(junctionCapabilityProbe);

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (_, _) => Ok()
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            var releases = new FakeArchiveReleaseClient();
            var service = new ArchiveSyncService(git, clone, state, releases);

            var injected = false;
            state.OnLoad = _ =>
            {
                if (Directory.Exists(archivePath))
                {
                    return;
                }

                injected = TryCreateJunction(archivePath, junctionTarget);
            };

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    "https://github.com/org/repo.git",
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            if (!injected)
            {
                return;
            }

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Contains("reparse point", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, state.LoadCalls);
            Assert.Equal(0, state.SaveCalls);
            Assert.Empty(clone.Requests);
            Assert.Empty(git.Calls);
        }
        finally
        {
            TryRemoveJunction(archivePath);
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("project:one")]
    [InlineData("project/one")]
    [InlineData("project\\one")]
    [InlineData("ProjectOne")]
    public async Task SyncAsync_UnsafeProjectId_FailsBeforeAnyStateOrGitMutation(string projectId)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-unsafe-id-" + Guid.NewGuid().ToString("N"));
        var git = new FakeGitProcessAdapter();
        var clone = new FakeCloneService();
        var state = new FakeStateStore();
        var releases = new FakeArchiveReleaseClient();
        var service = new ArchiveSyncService(git, clone, state, releases);

        var result = await service.SyncAsync(
            new ArchiveSyncRequest(
                projectId,
                "https://github.com/org/repo.git",
                root,
                EmptyKnownSafeInventory()),
            progress: null,
            CancellationToken.None);

        Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
        Assert.Contains("safe directory name", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, state.LoadCalls);
        Assert.Equal(0, state.SaveCalls);
        Assert.Empty(clone.Requests);
        Assert.Empty(git.Calls);
    }

    [Fact]
    public async Task SyncAsync_ExistingArchiveWrongOrigin_FailsBeforeAnyMutation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-wrong-origin-" + Guid.NewGuid().ToString("N"));
        var projectId = "wrong-origin";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok("https://github.com/other/repo.git\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            var releases = new FakeArchiveReleaseClient();
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    "https://github.com/org/repo.git",
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Contains("origin does not match", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(clone.Requests);
            Assert.Equal(1, state.LoadCalls);
            Assert.Equal(0, state.SaveCalls);
            var call = Assert.Single(git.Calls);
            Assert.Equal(new[] { "remote", "get-url", "origin" }, call.Arguments);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_UnownedExistingGitDirectory_FailsBeforeAnyMutation()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-unowned-" + Guid.NewGuid().ToString("N"));
        var projectId = "unowned-existing";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        try
        {
            var remote = "https://github.com/org/repo.git";
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            var releases = new FakeArchiveReleaseClient();
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Contains("ownership cannot be proven safely", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(clone.Requests);
            Assert.Equal(1, state.LoadCalls);
            Assert.Equal(0, state.SaveCalls);
            var call = Assert.Single(git.Calls);
            Assert.Equal(new[] { "remote", "get-url", "origin" }, call.Arguments);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_LegacyOwnedCloneWithoutMarker_RemainsUsableAndIsClaimedSafely()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-legacy-owned-" + Guid.NewGuid().ToString("N"));
        var projectId = "legacy-owned";
        var remote = "https://github.com/org/repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    if (args.Length >= 2 &&
                        args[0] == "fetch" &&
                        args[1] == "origin")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 &&
                        args[0] == "symbolic-ref" &&
                        args[1] == "refs/remotes/origin/HEAD")
                    {
                        return Ok("refs/remotes/origin/main\n");
                    }

                    if (args.Length >= 2 &&
                        args[0] == "checkout" &&
                        args[1] == "main")
                    {
                        return Ok();
                    }

                    if (args.Length >= 3 &&
                        args[0] == "reset" &&
                        args[1] == "--hard" &&
                        args[2] == "origin/main")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 &&
                        args[0] == "rev-parse" &&
                        args[1] == "HEAD")
                    {
                        return Ok("abc123\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                LastSyncedCommitSha = "oldsha"
            };
            var releases = new FakeArchiveReleaseClient();
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Equal("abc123", result.CommitSha);
            Assert.Empty(clone.Requests);
            Assert.NotNull(state.LastSaved);
            Assert.Equal(archivePath, state.LastSaved!.ArchivePath);
            Assert.Equal(ArchiveUpstreamStatus.Reachable, state.LastSaved.UpstreamStatus);

            var markerPath = Path.Combine(archivePath, ".git", "controltower-archive-owner");
            Assert.True(File.Exists(markerPath));
            var markerLines = File.ReadAllLines(markerPath);
            Assert.Equal("project_id=" + projectId, markerLines[0]);
            Assert.Equal("remote_identity=github.com/org/repo", markerLines[1]);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void Slugify_CollisionCandidates_GetDistinctHashedSafeNames()
    {
        var first = ArchiveSyncService.Slugify("v1:release");
        var second = ArchiveSyncService.Slugify("v1/release");

        Assert.StartsWith("v1_release-", first, StringComparison.Ordinal);
        Assert.StartsWith("v1_release-", second, StringComparison.Ordinal);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Slugify_DotLikeAndReservedNames_AreMappedToSafeItems()
    {
        var dot = ArchiveSyncService.Slugify(".");
        var dotDot = ArchiveSyncService.Slugify("..");
        var reserved = ArchiveSyncService.Slugify("CON");

        Assert.StartsWith("item-", dot, StringComparison.Ordinal);
        Assert.StartsWith("item-", dotDot, StringComparison.Ordinal);
        Assert.StartsWith("item-", reserved, StringComparison.Ordinal);
        Assert.NotEqual(dot, dotDot);
    }

    [Fact]
    public async Task SyncAsync_GlobalRootChange_RetainsPreviousPathAndClonesOnlyToNewRoot()
    {
        var oldRoot = Path.Combine(
            Path.GetTempPath(),
            "archive-root-old-" + Guid.NewGuid().ToString("N"));
        var newRoot = Path.Combine(
            Path.GetTempPath(),
            "archive-root-new-" + Guid.NewGuid().ToString("N"));
        var projectId = "archive-project";
        var previousArchivePath = Path.Combine(Path.GetFullPath(oldRoot), projectId);

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "rev-parse" && args[1] == "HEAD")
                    {
                        return Ok("newsha\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService
            {
                NextResult = CloneResult.Ok("main", "newsha", "cloned"),
                OnClone = request => Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"))
            };
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = previousArchivePath,
                LastSyncedCommitSha = "oldsha"
            };
            var releases = new FakeArchiveReleaseClient();

            var service = new ArchiveSyncService(git, clone, state, releases);
            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    "https://github.com/org/repo.git",
                    newRoot,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            var expectedArchivePath = Path.Combine(Path.GetFullPath(newRoot), projectId);
            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Single(clone.Requests);
            Assert.Equal(expectedArchivePath, clone.Requests[0].DestinationPath);
            Assert.NotNull(state.LastSaved);
            Assert.Equal(expectedArchivePath, state.LastSaved!.ArchivePath);
            Assert.Contains(state.LastSaved.PreviousArchivePaths, path =>
                string.Equals(path, previousArchivePath, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(git.Calls, call =>
                string.Equals(call.WorkingDirectory, previousArchivePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TryDeleteDirectory(oldRoot);
            TryDeleteDirectory(newRoot);
        }
    }

    [Fact]
    public async Task SyncAsync_RemoteGone_PreservesExistingArchiveAndMarksUpstreamUnavailable()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-gone-" + Guid.NewGuid().ToString("N"));
        var projectId = "gone-project";
        var existingPath = Path.Combine(root, projectId);

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Fail(128, "fatal: repository not found");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = existingPath,
                LastSyncedCommitSha = "prevsha"
            };
            var releases = new FakeArchiveReleaseClient();
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    "https://github.com/org/missing.git",
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.UpstreamUnavailable, result.Status);
            Assert.Contains(existingPath, result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("prevsha", result.CommitSha);
            Assert.Empty(clone.Requests);
            Assert.NotNull(state.LastSaved);
            Assert.Equal(ArchiveUpstreamStatus.Unavailable, state.LastSaved!.UpstreamStatus);
            Assert.Contains("repository not found", state.LastSaved.LastError, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_WithoutOwnRelease_TracksResolvedUpstreamAndProbesForkFirst()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-" + Guid.NewGuid().ToString("N"));
        var projectId = "forked-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");
        var rootRelease = new ArchiveReleaseInfo(
            "v2.0.0",
            "v2.0.0",
            "Root upstream release",
            DateTime.UtcNow,
            new[]
            {
                new ArchiveReleaseAsset(
                    "root.zip",
                    "https://downloads.example.test/root.zip",
                    12,
                    "abcd")
            });

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok();
                    }

                    if (args.Length >= 3 && args[0] == "remote" && args[1] == "get-url" && args[2] == "upstream")
                    {
                        return Fail(2, "no such remote");
                    }

                    if (args.Length >= 3 && args[0] == "remote" && args[1] == "get-url" && args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    if (args.Length >= 4 && args[0] == "remote" && args[1] == "add" && args[2] == "upstream")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "fetch" && args[1] == "upstream")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "checkout" && args[1] == "-B")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "rev-parse" && args[1] == "HEAD")
                    {
                        return Ok("forksha\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath
            };
            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = null,
                    [upstream.CloneUrl] = rootRelease
                }
            };

            var service = new ArchiveSyncService(git, clone, state, releases);
            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Empty(clone.Requests);
            Assert.Single(releases.ResolveUpstreamUrls);
            Assert.Equal(new[] { remote, upstream.CloneUrl }, releases.LatestReleaseUrls);
            Assert.Single(releases.Downloads);
            Assert.Equal("root.zip", releases.Downloads[0].Asset.Name);
            Assert.NotNull(state.LastSaved);
            Assert.Equal("upstream-owner/upstream-repo", state.LastSaved!.UpstreamFullName);
            Assert.Equal("v2.0.0", state.LastSaved.LastReleaseTag);
            Assert.True(string.IsNullOrWhiteSpace(state.LastSaved.SourceSelectionNote));
            Assert.Contains(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "upstream");
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "origin");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_WithOwnRelease_UsesOriginOnlyAndSkipsOriginalProbe()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-own-release-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-own-release-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");
        var forkRelease = new ArchiveReleaseInfo(
            "v3.1.0",
            "v3.1.0",
            "Fork-owned release",
            DateTime.UtcNow,
            new[]
            {
                new ArchiveReleaseAsset("fork-win.zip", "https://downloads.example.test/fork-win.zip", 10, "sha1"),
                new ArchiveReleaseAsset("fork-linux.tar.gz", "https://downloads.example.test/fork-linux.tar.gz", 20, null)
            });

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    if (args.Length >= 2 && args[0] == "fetch" && args[1] == "origin")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "symbolic-ref" && args[1] == "refs/remotes/origin/HEAD")
                    {
                        return Ok("refs/remotes/origin/main\n");
                    }

                    if (args.Length >= 2 && args[0] == "checkout" && args[1] == "main")
                    {
                        return Ok();
                    }

                    if (args.Length >= 3 && args[0] == "reset" && args[1] == "--hard" && args[2] == "origin/main")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "rev-parse" && args[1] == "HEAD")
                    {
                        return Ok("forksha\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                LastReleaseTag = "old",
                LastReleaseDirectoryName = "old"
            };
            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = forkRelease
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Single(releases.ResolveUpstreamUrls);
            Assert.Equal(new[] { remote }, releases.LatestReleaseUrls);
            Assert.Equal(
                new[] { "fork-win.zip", "fork-linux.tar.gz" },
                releases.Downloads.Select(download => download.Asset.Name).ToArray());
            Assert.NotNull(state.LastSaved);
            Assert.Equal(string.Empty, state.LastSaved!.UpstreamFullName);
            Assert.Equal("v3.1.0", state.LastSaved.LastReleaseTag);
            Assert.False(string.IsNullOrWhiteSpace(state.LastSaved.SourceSelectionNote));
            Assert.Equal(ArchiveUpstreamStatus.Reachable, state.LastSaved.UpstreamStatus);
            Assert.Equal(string.Empty, state.LastSaved.LastError);
            Assert.Contains(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "origin");
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "upstream");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_SourceChangedFromRootToFork_RecapturesSameTagAndAssetPayload()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-provenance-recapture-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-provenance-recapture-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        var releasePath = Path.Combine(archivePath, "releases", "v1");
        var payloadPath = Path.Combine(releasePath, "app.zip");
        var previousCapture = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var oldPayload = new byte[] { 1, 2, 3, 4 };
        var newPayload = new byte[] { 9, 8, 7, 6 };

        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));
        Directory.CreateDirectory(releasePath);
        File.WriteAllBytes(payloadPath, oldPayload);

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");
        var forkRelease = new ArchiveReleaseInfo(
            "v1",
            "v1",
            "Fork-owned release with same tag/name as previous root capture",
            DateTime.UtcNow,
            new[]
            {
                new ArchiveReleaseAsset("app.zip", "https://downloads.example.test/fork/app.zip", 4, null)
            });

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    if (args.Length >= 2 && args[0] == "fetch" && args[1] == "origin")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "symbolic-ref" && args[1] == "refs/remotes/origin/HEAD")
                    {
                        return Ok("refs/remotes/origin/main\n");
                    }

                    if (args.Length >= 2 && args[0] == "checkout" && args[1] == "main")
                    {
                        return Ok();
                    }

                    if (args.Length >= 3 && args[0] == "reset" && args[1] == "--hard" && args[2] == "origin/main")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "rev-parse" && args[1] == "HEAD")
                    {
                        return Ok("forksha\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v1",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = previousCapture,
                LastReleaseDirectoryName = "v1"
            };
            state.States[projectId].LastReleaseAssetNames.Add("app.zip");

            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = forkRelease
                },
                OnDownload = (_, destinationPath) =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.WriteAllBytes(destinationPath, newPayload);
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Equal(new[] { remote }, releases.LatestReleaseUrls);
            Assert.Single(releases.Downloads);
            Assert.Equal("app.zip", releases.Downloads[0].Asset.Name);
            Assert.Equal(newPayload, File.ReadAllBytes(payloadPath));
            Assert.NotEqual(oldPayload, File.ReadAllBytes(payloadPath));
            Assert.NotNull(state.LastSaved);
            Assert.Equal(string.Empty, state.LastSaved!.UpstreamFullName);
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved.ReleaseStatus);
            Assert.Equal("v1", state.LastSaved.LastReleaseTag);
            Assert.Equal(new[] { "app.zip" }, state.LastSaved.LastReleaseAssetNames);
            Assert.Contains(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "origin");
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "upstream");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncAsync_ForkRemote_UpstreamLookupFailure_FailsAndPreservesPreviousEvidenceWithoutFallbackOrReleaseLookup(bool networkFailure)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-upstream-failure-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-upstream-failure-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        var previousCapture = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            var previousState = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = previousCapture,
                LastReleaseDirectoryName = "v9.9.9",
                LastSyncedCommitSha = "prevsha"
            };
            previousState.LastReleaseAssetNames.Add("existing.zip");
            state.States[projectId] = previousState;

            var releases = new FakeArchiveReleaseClient
            {
                ResolveUpstreamException = networkFailure
                    ? new HttpRequestException("network unreachable")
                    : new InvalidDataException("GitHub fork response metadata was incomplete.")
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.StartsWith("Archive source selection failed:", result.Message, StringComparison.Ordinal);
            Assert.Contains(
                networkFailure ? "network unreachable" : "metadata was incomplete",
                result.Message,
                StringComparison.OrdinalIgnoreCase);
            Assert.Single(releases.ResolveUpstreamUrls);
            Assert.Empty(releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);

            Assert.NotNull(state.LastSaved);
            Assert.StartsWith("Archive source selection failed:", state.LastSaved!.LastError, StringComparison.Ordinal);
            Assert.Equal("upstream-owner/upstream-repo", state.LastSaved.UpstreamFullName);
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved.ReleaseStatus);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseTag);
            Assert.Equal(ArchiveReleaseIntegrity.Verified, state.LastSaved.LastReleaseIntegrity);
            Assert.Equal(previousCapture, state.LastSaved.LastReleaseCapturedUtc);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseDirectoryName);
            Assert.Equal(new[] { "existing.zip" }, state.LastSaved.LastReleaseAssetNames);

            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length > 0 &&
                string.Equals(call.Arguments[0], "fetch", StringComparison.Ordinal));
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "checkout" &&
                call.Arguments[1] == "-B");
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "rev-parse" &&
                call.Arguments[1] == "HEAD");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_ForkReleaseLookupFailure_FailsWithoutFallbackAndPreservesEvidence()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-release-lookup-failure-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-release-lookup-failure-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));
        var previousCapture = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = previousCapture,
                LastReleaseDirectoryName = "v9.9.9",
                SourceSelectionNote = "previous selection note"
            };
            state.States[projectId].LastReleaseAssetNames.Add("existing.zip");

            var forkReleaseError = new HttpRequestException(
                "Fork latest-release lookup returned HTTP 429.",
                null,
                HttpStatusCode.TooManyRequests);
            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseExceptionsByUrl =
                {
                    [remote] = forkReleaseError
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.StartsWith("Archive source selection failed:", result.Message, StringComparison.Ordinal);
            Assert.Contains("429", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(releases.ResolveUpstreamUrls);
            Assert.Equal(new[] { remote }, releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
            Assert.NotNull(state.LastSaved);
            Assert.Equal(result.Message, state.LastSaved!.LastError);
            Assert.Equal("upstream-owner/upstream-repo", state.LastSaved.UpstreamFullName);
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved.ReleaseStatus);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseTag);
            Assert.Equal(previousCapture, state.LastSaved.LastReleaseCapturedUtc);
            Assert.Equal(new[] { "existing.zip" }, state.LastSaved.LastReleaseAssetNames);
            Assert.Equal("previous selection note", state.LastSaved.SourceSelectionNote);
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length > 0 &&
                string.Equals(call.Arguments[0], "fetch", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Gone)]
    [InlineData(HttpStatusCode.UnavailableForLegalReasons)]
    public async Task SyncAsync_ForkRemote_OriginalApiUnavailableAndForkHasNoRelease_PreservesHistoricalCaptureAndPayload(
        HttpStatusCode unavailableStatus)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-root-unavailable-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-root-unavailable-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        var previousCapture = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        var previousReleaseDir = Path.Combine(archivePath, "releases", "v9.9.9");
        var previousPayload = Path.Combine(previousReleaseDir, "previous.zip");
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));
        Directory.CreateDirectory(previousReleaseDir);
        File.WriteAllText(previousPayload, "previous release payload");

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length >= 4 &&
                        args[0] == "ls-remote" &&
                        args[1] == "--heads" &&
                        args[2] == "--" &&
                        string.Equals(args[3], remote, StringComparison.OrdinalIgnoreCase))
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    if (args.Length >= 2 && args[0] == "fetch" && args[1] == "origin")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "symbolic-ref" && args[1] == "refs/remotes/origin/HEAD")
                    {
                        return Ok("refs/remotes/origin/main\n");
                    }

                    if (args.Length >= 2 && args[0] == "checkout" && args[1] == "main")
                    {
                        return Ok();
                    }

                    if (args.Length >= 3 && args[0] == "reset" && args[1] == "--hard" && args[2] == "origin/main")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "rev-parse" && args[1] == "HEAD")
                    {
                        return Ok("forksha\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = previousCapture,
                LastReleaseDirectoryName = "v9.9.9"
            };
            state.States[projectId].LastReleaseAssetNames.Add("previous.zip");

            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = null
                },
                LatestReleaseExceptionsByUrl =
                {
                    [upstream.CloneUrl] = new HttpRequestException(
                        "Original latest-release lookup returned HTTP " + (int)unavailableStatus + ".",
                        null,
                        unavailableStatus)
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Contains("no current release", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { remote, upstream.CloneUrl }, releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
            Assert.NotNull(state.LastSaved);
            Assert.Equal(ArchiveReleaseStatus.NoCurrentRelease, state.LastSaved!.ReleaseStatus);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseTag);
            Assert.Equal(ArchiveReleaseIntegrity.Verified, state.LastSaved.LastReleaseIntegrity);
            Assert.Equal(previousCapture, state.LastSaved.LastReleaseCapturedUtc);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseDirectoryName);
            Assert.Equal(new[] { "previous.zip" }, state.LastSaved.LastReleaseAssetNames);
            Assert.Equal("previous release payload", File.ReadAllText(previousPayload));
            Assert.Equal(string.Empty, state.LastSaved.LastError);
            Assert.Equal(ArchiveUpstreamStatus.Reachable, state.LastSaved.UpstreamStatus);
            Assert.Equal(string.Empty, state.LastSaved.UpstreamFullName);
            Assert.False(string.IsNullOrWhiteSpace(state.LastSaved.SourceSelectionNote));
            Assert.Contains(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "origin");
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "upstream");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task SyncAsync_ForkRemote_OriginalApiFailureOutsideUnavailableStatuses_FailsWithoutFallback(
        HttpStatusCode status)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-original-api-failure-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-original-api-failure-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        var previousCapture = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = previousCapture,
                LastReleaseDirectoryName = "v9.9.9"
            };
            state.States[projectId].LastReleaseAssetNames.Add("existing.zip");

            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = null
                },
                LatestReleaseExceptionsByUrl =
                {
                    [upstream.CloneUrl] = new HttpRequestException(
                        "Original latest-release lookup returned HTTP " + (int)status + ".",
                        null,
                        status)
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.StartsWith("Archive source selection failed:", result.Message, StringComparison.Ordinal);
            Assert.Contains(((int)status).ToString(), result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { remote, upstream.CloneUrl }, releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
            Assert.NotNull(state.LastSaved);
            Assert.Equal("upstream-owner/upstream-repo", state.LastSaved!.UpstreamFullName);
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved.ReleaseStatus);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseTag);
            Assert.Equal(previousCapture, state.LastSaved.LastReleaseCapturedUtc);
            Assert.Equal(new[] { "existing.zip" }, state.LastSaved.LastReleaseAssetNames);
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length > 0 &&
                string.Equals(call.Arguments[0], "fetch", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_RootRemoteCheckTimedOutWithRepositoryNotFoundText_FailsWithoutFallback()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-root-timeout-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-root-timeout-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        var previousCapture = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length >= 4 &&
                        args[0] == "ls-remote" &&
                        args[1] == "--heads" &&
                        args[2] == "--" &&
                        string.Equals(args[3], remote, StringComparison.OrdinalIgnoreCase))
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 4 &&
                        args[0] == "ls-remote" &&
                        args[1] == "--heads" &&
                        args[2] == "--" &&
                        string.Equals(args[3], upstream.CloneUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        return TimedOut("fatal: repository not found");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = previousCapture,
                LastReleaseDirectoryName = "v9.9.9"
            };
            state.States[projectId].LastReleaseAssetNames.Add("existing.zip");

            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = null,
                    [upstream.CloneUrl] = null
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.StartsWith("Archive source selection failed:", result.Message, StringComparison.Ordinal);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { remote, upstream.CloneUrl }, releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
            Assert.NotNull(state.LastSaved);
            Assert.Equal("upstream-owner/upstream-repo", state.LastSaved!.UpstreamFullName);
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved.ReleaseStatus);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseTag);
            Assert.Equal(previousCapture, state.LastSaved.LastReleaseCapturedUtc);
            Assert.Equal(new[] { "existing.zip" }, state.LastSaved.LastReleaseAssetNames);
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "origin");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_UpstreamFetchTimedOutWithRepositoryNotFoundText_FailsWithoutFallback()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-upstream-fetch-timeout-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-upstream-fetch-timeout-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        var previousCapture = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length >= 4 &&
                        args[0] == "ls-remote" &&
                        args[1] == "--heads" &&
                        args[2] == "--")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "upstream")
                    {
                        return Fail(2, "no such remote");
                    }

                    if (args.Length >= 4 && args[0] == "remote" && args[1] == "add" && args[2] == "upstream")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "fetch" && args[1] == "upstream")
                    {
                        return TimedOut("fatal: repository not found");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = previousCapture,
                LastReleaseDirectoryName = "v9.9.9"
            };
            state.States[projectId].LastReleaseAssetNames.Add("existing.zip");

            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = null,
                    [upstream.CloneUrl] = null
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Contains("timed out", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { remote, upstream.CloneUrl }, releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
            Assert.NotNull(state.LastSaved);
            Assert.Equal("upstream-owner/upstream-repo", state.LastSaved!.UpstreamFullName);
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved.ReleaseStatus);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseTag);
            Assert.Equal(previousCapture, state.LastSaved.LastReleaseCapturedUtc);
            Assert.Equal(new[] { "existing.zip" }, state.LastSaved.LastReleaseAssetNames);
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "origin");
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "checkout" &&
                call.Arguments[1] == "-B");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_UpstreamCheckoutRepositoryNotFoundError_FailsWithoutFallback()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-upstream-checkout-error-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-upstream-checkout-error-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        var previousCapture = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length >= 4 &&
                        args[0] == "ls-remote" &&
                        args[1] == "--heads" &&
                        args[2] == "--")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "upstream")
                    {
                        return Fail(2, "no such remote");
                    }

                    if (args.Length >= 4 && args[0] == "remote" && args[1] == "add" && args[2] == "upstream")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "fetch" && args[1] == "upstream")
                    {
                        return Ok();
                    }

                    if (args.Length >= 2 && args[0] == "checkout" && args[1] == "-B")
                    {
                        return Fail(1, "fatal: repository not found");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = previousCapture,
                LastReleaseDirectoryName = "v9.9.9"
            };
            state.States[projectId].LastReleaseAssetNames.Add("existing.zip");

            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = null,
                    [upstream.CloneUrl] = null
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    root,
                    EmptyKnownSafeInventory()),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Contains("repository not found", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { remote, upstream.CloneUrl }, releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
            Assert.NotNull(state.LastSaved);
            Assert.Equal("upstream-owner/upstream-repo", state.LastSaved!.UpstreamFullName);
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved.ReleaseStatus);
            Assert.Equal("v9.9.9", state.LastSaved.LastReleaseTag);
            Assert.Equal(previousCapture, state.LastSaved.LastReleaseCapturedUtc);
            Assert.Equal(new[] { "existing.zip" }, state.LastSaved.LastReleaseAssetNames);
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "origin");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_RootRemoteCheckCancelled_PropagatesCancellationWithoutFallback()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-root-cancelled-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-root-cancelled-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        using var cancellation = new CancellationTokenSource();
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        var upstream = new ArchiveUpstreamRepo(
            "upstream-owner",
            "upstream-repo",
            "https://github.com/upstream-owner/upstream-repo.git",
            "main");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length >= 4 &&
                        args[0] == "ls-remote" &&
                        args[1] == "--heads" &&
                        args[2] == "--" &&
                        string.Equals(args[3], remote, StringComparison.OrdinalIgnoreCase))
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 4 &&
                        args[0] == "ls-remote" &&
                        args[1] == "--heads" &&
                        args[2] == "--" &&
                        string.Equals(args[3], upstream.CloneUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        cancellation.Cancel();
                        return Cancelled("repository not found");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9"
            };
            var releases = new FakeArchiveReleaseClient
            {
                UpstreamResult = upstream,
                LatestReleaseResultsByUrl =
                {
                    [remote] = null,
                    [upstream.CloneUrl] = null
                }
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SyncAsync(
                    new ArchiveSyncRequest(
                        projectId,
                        remote,
                        root,
                        EmptyKnownSafeInventory()),
                    progress: null,
                    cancellation.Token));

            Assert.Equal(0, state.SaveCalls);
            Assert.Equal(new[] { remote, upstream.CloneUrl }, releases.LatestReleaseUrls);
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length >= 2 &&
                call.Arguments[0] == "fetch" &&
                call.Arguments[1] == "origin");
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_ForkRemote_UpstreamLookupCancellation_PropagatesAndStopsWithoutFallback()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "archive-root-fork-upstream-cancel-" + Guid.NewGuid().ToString("N"));
        var projectId = "fork-upstream-cancel-project";
        var remote = "https://github.com/fork-owner/fork-repo.git";
        var archivePath = Path.Combine(Path.GetFullPath(root), projectId);
        using var cancellation = new CancellationTokenSource();
        Directory.CreateDirectory(Path.Combine(archivePath, ".git"));

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 3 &&
                        args[0] == "remote" &&
                        args[1] == "get-url" &&
                        args[2] == "origin")
                    {
                        return Ok(remote + "\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService();
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                ArchivePath = archivePath,
                UpstreamFullName = "upstream-owner/upstream-repo",
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v9.9.9"
            };

            var releases = new FakeArchiveReleaseClient
            {
                ResolveUpstreamExceptionFactory = ct => new OperationCanceledException(ct)
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                service.SyncAsync(
                    new ArchiveSyncRequest(
                        projectId,
                        remote,
                        root,
                        EmptyKnownSafeInventory()),
                    progress: null,
                    cancellation.Token));

            Assert.Single(releases.ResolveUpstreamUrls);
            Assert.Empty(releases.LatestReleaseUrls);
            Assert.Empty(releases.Downloads);
            Assert.Equal(0, state.SaveCalls);
            Assert.DoesNotContain(git.Calls, call =>
                call.Arguments.Length > 0 &&
                string.Equals(call.Arguments[0], "fetch", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_SuccessfulDedicatedRoot_KeepsSourceUntouchedAndStoresReleaseDownloadsUnderArchiveTarget()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-root-release-target-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        var sourceSentinel = Path.Combine(sourceRoot, "source-sentinel.txt");
        var projectId = "release-target-project";
        var expectedArchivePath = Path.Combine(Path.GetFullPath(archiveRoot), projectId);
        var remote = "https://github.com/org/repo.git";

        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(sourceSentinel, "do-not-touch");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/main\n");
                    }

                    if (args.Length >= 2 && args[0] == "rev-parse" && args[1] == "HEAD")
                    {
                        return Ok("abc123\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService
            {
                NextResult = CloneResult.Ok("main", "abc123", "cloned"),
                OnClone = request => Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"))
            };
            var state = new FakeStateStore();
            var releases = new FakeArchiveReleaseClient
            {
                LatestReleaseResult = new ArchiveReleaseInfo(
                    "v1.2.3",
                    "v1.2.3",
                    "notes",
                    DateTime.UtcNow,
                    new[]
                    {
                        new ArchiveReleaseAsset(
                            "release.zip",
                            "https://example.test/release.zip",
                            1024,
                            "sha256:abc")
                    }),
                DownloadResult = ArchiveReleaseIntegrity.Verified
            };
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    remote,
                    archiveRoot,
                    ArchiveWorkingSourceInventory.Complete(new[]
                    {
                        new ArchiveWorkingSource("non-archive-source", sourceRoot)
                    })),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Single(clone.Requests);
            Assert.Equal(expectedArchivePath, clone.Requests[0].DestinationPath);
            Assert.True(File.Exists(sourceSentinel));
            Assert.Equal("do-not-touch", File.ReadAllText(sourceSentinel));
            Assert.Single(releases.Downloads);
            Assert.All(releases.Downloads, download =>
            {
                Assert.StartsWith(
                    Path.Combine(expectedArchivePath, "releases") + Path.DirectorySeparatorChar,
                    download.DestinationPath,
                    StringComparison.OrdinalIgnoreCase);
            });
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task SyncAsync_ForgejoLatestRelease_DownloadsEveryPlatformAndBuildOutsideWorkingSource()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "archive-forgejo-release-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        var sourceSentinel = Path.Combine(sourceRoot, "source-sentinel.txt");
        const string projectId = "forgejo-release-project";
        var archivePath = Path.Combine(archiveRoot, projectId);
        Directory.CreateDirectory(sourceRoot);
        File.WriteAllText(sourceSentinel, "do-not-touch");

        try
        {
            var git = new FakeGitProcessAdapter
            {
                Handler = (args, _) =>
                {
                    if (args.Length > 0 && args[0] == "ls-remote")
                    {
                        return Ok("abc\trefs/heads/master\n");
                    }

                    if (args.Length >= 2 && args[0] == "rev-parse" && args[1] == "HEAD")
                    {
                        return Ok("abc123\n");
                    }

                    throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
                }
            };
            var clone = new FakeCloneService
            {
                NextResult = CloneResult.Ok("master", "abc123", "cloned"),
                OnClone = request => Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"))
            };
            var state = new FakeStateStore();
            using var handler = new ReleaseHttpHandler(ForgejoReleaseFixture.Respond);
            using var releases = new GitHubReleaseClient(handler, "test-github-token");
            var service = new ArchiveSyncService(git, clone, state, releases);

            var result = await service.SyncAsync(
                new ArchiveSyncRequest(
                    projectId,
                    "https://forge.example.test/eden-emu/eden.git",
                    archiveRoot,
                    ArchiveWorkingSourceInventory.Complete(new[]
                    {
                        new ArchiveWorkingSource("forgejo-working-source", sourceRoot)
                    })),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Equal(archivePath, Assert.Single(clone.Requests).DestinationPath);
            var releasePath = Path.Combine(archivePath, "releases", "v0.2.1");
            Assert.Equal(
                ForgejoReleaseFixture.AssetNames.OrderBy(name => name, StringComparer.Ordinal),
                Directory.GetFiles(releasePath).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
            foreach (var name in ForgejoReleaseFixture.AssetNames)
            {
                Assert.Equal(
                    ForgejoReleaseFixture.Contents(name),
                    await File.ReadAllBytesAsync(Path.Combine(releasePath, name)));
            }

            Assert.Equal(1 + ForgejoReleaseFixture.AssetNames.Length, handler.Requests.Count);
            Assert.All(handler.Requests, request => Assert.Null(request.Authorization));
            Assert.Equal("v0.2.1", state.LastSaved!.LastReleaseTag);
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved.ReleaseStatus);
            Assert.Equal(ArchiveReleaseIntegrity.Unverifiable, state.LastSaved.LastReleaseIntegrity);
            Assert.Equal(ForgejoReleaseFixture.AssetNames, state.LastSaved.LastReleaseAssetNames);
            Assert.Equal("do-not-touch", File.ReadAllText(sourceSentinel));
            Assert.Equal(new[] { sourceSentinel }, Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories));
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task SyncAsync_ForgejoRefresh_ReplacesSameTagWithoutPromotingStaleStagingFiles()
    {
        var rootBase = Path.Combine(Path.GetTempPath(), "archive-forgejo-refresh-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        const string projectId = "forgejo-refresh-project";
        var releaseRoot = Path.Combine(archiveRoot, projectId, "releases");
        var finalPath = Path.Combine(releaseRoot, "v0.2.1");
        var staleIncoming = Path.Combine(releaseRoot, ".incoming-v0.2.1");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            using var handler = new ReleaseHttpHandler(ForgejoReleaseFixture.Respond);
            using var releases = new GitHubReleaseClient(handler, gitHubToken: null);
            var state = new FakeStateStore();
            var service = CreateForgejoCaptureService(releases, state, _ =>
            {
                Directory.CreateDirectory(finalPath);
                File.WriteAllText(Path.Combine(finalPath, "previous.zip"), "previous complete capture");
                Directory.CreateDirectory(staleIncoming);
                File.WriteAllText(Path.Combine(staleIncoming, "stale.zip"), "interrupted earlier attempt");
            });

            var result = await service.SyncAsync(
                CreateForgejoCaptureRequest(projectId, archiveRoot, sourceRoot),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Equal(
                ForgejoReleaseFixture.AssetNames.OrderBy(name => name, StringComparer.Ordinal),
                Directory.GetFiles(finalPath).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
            Assert.All(
                Directory.GetDirectories(releaseRoot, ".incoming-*"),
                path => Assert.Equal(staleIncoming, path));
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SyncAsync_ForgejoInterruptedRefresh_PreservesPreviousReleaseAndCleansStaging(bool cancel)
    {
        var rootBase = Path.Combine(Path.GetTempPath(), "archive-forgejo-interrupted-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        const string projectId = "forgejo-interrupted-project";
        var releaseRoot = Path.Combine(archiveRoot, projectId, "releases");
        var finalPath = Path.Combine(releaseRoot, "v0.2.1");
        var previousAsset = Path.Combine(finalPath, "previous.zip");
        var previousCapture = new DateTime(2026, 6, 2, 0, 0, 0, DateTimeKind.Utc);
        Directory.CreateDirectory(sourceRoot);
        using var cancellation = new CancellationTokenSource();

        try
        {
            using var handler = new ReleaseHttpHandler((request, token) =>
            {
                if (request.RequestUri!.AbsoluteUri == ForgejoReleaseFixture.DownloadUrl(ForgejoReleaseFixture.AssetNames[4]))
                {
                    if (cancel)
                    {
                        cancellation.Cancel();
                        return Task.FromCanceled<HttpResponseMessage>(token);
                    }

                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                }

                return ForgejoReleaseFixture.Respond(request, token);
            });
            using var releases = new GitHubReleaseClient(handler, gitHubToken: null);
            var state = new FakeStateStore();
            state.States[projectId] = new ArchiveRepoState
            {
                ProjectId = projectId,
                LastReleaseTag = "v0.2.1",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Unverifiable,
                LastReleaseCapturedUtc = previousCapture
            };
            var service = CreateForgejoCaptureService(releases, state, _ =>
            {
                Directory.CreateDirectory(finalPath);
                File.WriteAllText(previousAsset, "previous complete capture");
            });
            var request = CreateForgejoCaptureRequest(projectId, archiveRoot, sourceRoot);

            if (cancel)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                    service.SyncAsync(request, progress: null, cancellation.Token));
            }
            else
            {
                var result = await service.SyncAsync(request, progress: null, CancellationToken.None);
                Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
                Assert.Contains("503", result.Message);
            }

            Assert.Equal(new[] { previousAsset }, Directory.GetFiles(finalPath));
            Assert.Equal("previous complete capture", File.ReadAllText(previousAsset));
            Assert.Empty(Directory.GetDirectories(releaseRoot, ".incoming-*"));
            if (cancel)
            {
                Assert.Equal(previousCapture, state.States[projectId].LastReleaseCapturedUtc);
            }
            else
            {
                Assert.Equal(ArchiveReleaseStatus.Incomplete, state.LastSaved!.ReleaseStatus);
                Assert.Contains("503", state.LastSaved.LastReleaseError);
                Assert.Null(state.LastSaved.LastReleaseCapturedUtc);
            }
            Assert.True(handler.Requests.Count > 2, "The failure must occur after some assets were downloaded.");
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task SyncAsync_ForgejoMalformedAsset_FailsWithoutPromotingPartialRelease()
    {
        var rootBase = Path.Combine(Path.GetTempPath(), "archive-forgejo-malformed-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        const string projectId = "forgejo-malformed-project";
        Directory.CreateDirectory(sourceRoot);

        try
        {
            using var handler = new ReleaseHttpHandler((_, _) => Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                        {"tag_name":"v0.2.1","assets":[{"name":"app.apk"}]}
                        """)
                }));
            using var releases = new GitHubReleaseClient(handler, gitHubToken: null);
            var state = new FakeStateStore();
            var service = CreateForgejoCaptureService(releases, state);

            var result = await service.SyncAsync(
                CreateForgejoCaptureRequest(projectId, archiveRoot, sourceRoot),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.False(string.IsNullOrWhiteSpace(result.Message));
            Assert.Null(state.LastSaved!.LastReleaseCapturedUtc);
            Assert.False(Directory.Exists(Path.Combine(archiveRoot, projectId, "releases", "v0.2.1")));
            Assert.Single(handler.Requests);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SyncAsync_SameVerifiedTagWithAddedOrMissingAssets_RecapturesFullCurrentRelease(bool addedAssets)
    {
        var rootBase = Path.Combine(Path.GetTempPath(), "archive-forgejo-recapture-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        const string projectId = "forgejo-recapture-project";
        var finalPath = Path.Combine(archiveRoot, projectId, "releases", "v0.2.1");
        Directory.CreateDirectory(sourceRoot);

        try
        {
            using var handler = new ReleaseHttpHandler(ForgejoReleaseFixture.Respond);
            using var releases = new GitHubReleaseClient(handler, gitHubToken: null);
            var state = new FakeStateStore();
            var previous = new ArchiveRepoState
            {
                ProjectId = projectId,
                ReleaseStatus = ArchiveReleaseStatus.Captured,
                LastReleaseTag = "v0.2.1",
                LastReleaseDirectoryName = "v0.2.1",
                LastReleaseIntegrity = ArchiveReleaseIntegrity.Verified,
                LastReleaseCapturedUtc = DateTime.UtcNow
            };
            var capturedNames = addedAssets ? new[] { ForgejoReleaseFixture.AssetNames[0] } : ForgejoReleaseFixture.AssetNames;
            foreach (var name in capturedNames)
            {
                previous.LastReleaseAssetNames.Add(name);
            }

            state.States[projectId] = previous;
            var service = CreateForgejoCaptureService(releases, state, _ =>
            {
                Directory.CreateDirectory(finalPath);
                foreach (var name in capturedNames)
                {
                    if (addedAssets || name != ForgejoReleaseFixture.AssetNames[0])
                    {
                        File.WriteAllBytes(Path.Combine(finalPath, name), ForgejoReleaseFixture.Contents(name));
                    }
                }
            });

            var result = await service.SyncAsync(
                CreateForgejoCaptureRequest(projectId, archiveRoot, sourceRoot),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Equal(1 + ForgejoReleaseFixture.AssetNames.Length, handler.Requests.Count);
            Assert.Equal(
                ForgejoReleaseFixture.AssetNames.OrderBy(name => name, StringComparer.Ordinal),
                Directory.GetFiles(finalPath).Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal));
            Assert.Equal(ArchiveReleaseStatus.Captured, state.LastSaved!.ReleaseStatus);
            Assert.Equal(ForgejoReleaseFixture.AssetNames, state.LastSaved.LastReleaseAssetNames);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task SyncAsync_ReleaseHashMismatch_IsIncompleteNotSuccessfullySynced()
    {
        var rootBase = Path.Combine(Path.GetTempPath(), "archive-release-mismatch-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        const string projectId = "release-mismatch-project";
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var releases = new FakeArchiveReleaseClient
            {
                LatestReleaseResult = new ArchiveReleaseInfo(
                    "v1", "v1", "notes", DateTime.UtcNow,
                    new[] { new ArchiveReleaseAsset("app.zip", "https://downloads.example.test/app.zip", 1, "abcd") }),
                DownloadResult = ArchiveReleaseIntegrity.Mismatch
            };
            var state = new FakeStateStore();
            var service = CreateForgejoCaptureService(releases, state);

            var result = await service.SyncAsync(
                CreateForgejoCaptureRequest(projectId, archiveRoot, sourceRoot),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Contains("mismatch", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ArchiveReleaseStatus.Incomplete, state.LastSaved!.ReleaseStatus);
            Assert.Equal(ArchiveReleaseIntegrity.Mismatch, state.LastSaved.LastReleaseIntegrity);
            Assert.False(Directory.Exists(Path.Combine(archiveRoot, projectId, "releases", "v1")));
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task SyncAsync_ConfirmedNoRelease_ReportsSourceOnlyArchive()
    {
        var rootBase = Path.Combine(Path.GetTempPath(), "archive-no-release-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        const string projectId = "no-release-project";
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var state = new FakeStateStore();
            var service = CreateForgejoCaptureService(new FakeArchiveReleaseClient(), state);

            var result = await service.SyncAsync(
                CreateForgejoCaptureRequest(projectId, archiveRoot, sourceRoot),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Succeeded, result.Status);
            Assert.Contains("no current release", result.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ArchiveReleaseStatus.NoCurrentRelease, state.LastSaved!.ReleaseStatus);
            Assert.True(Directory.Exists(Path.Combine(archiveRoot, projectId, ".git")));
            Assert.Null(state.LastSaved.LastReleaseCapturedUtc);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public void PromoteIncomingRelease_FailedReplacement_RestoresPreviousCapture()
    {
        var root = Path.Combine(Path.GetTempPath(), "archive-release-rollback-" + Guid.NewGuid().ToString("N"));
        var incoming = Path.Combine(root, ".incoming-v1");
        var final = Path.Combine(root, "v1");
        Directory.CreateDirectory(incoming);
        Directory.CreateDirectory(final);
        File.WriteAllText(Path.Combine(incoming, "new.zip"), "new capture");
        File.WriteAllText(Path.Combine(final, "previous.zip"), "previous complete capture");
        var moves = new List<(string Source, string Destination)>();
        try
        {
            Assert.Throws<IOException>(() => ArchiveSyncService.PromoteIncomingRelease(
                incoming,
                final,
                root,
                (source, destination) =>
                {
                    moves.Add((source, destination));
                    if (source == incoming)
                    {
                        throw new IOException("Injected incoming promotion failure.");
                    }

                    Directory.Move(source, destination);
                }));

            Assert.Equal(3, moves.Count);
            Assert.Equal(final, moves[0].Source);
            Assert.Equal((incoming, final), moves[1]);
            Assert.Equal(final, moves[2].Destination);
            Assert.Equal("previous complete capture", File.ReadAllText(Path.Combine(final, "previous.zip")));
            Assert.Equal("new capture", File.ReadAllText(Path.Combine(incoming, "new.zip")));
            Assert.Empty(Directory.GetDirectories(root, ".backup-*"));
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void PromoteIncomingRelease_FailedRestoration_RetainsBackupAndReportsRecoveryPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "archive-release-recovery-" + Guid.NewGuid().ToString("N"));
        var incoming = Path.Combine(root, ".incoming-v1");
        var final = Path.Combine(root, "v1");
        Directory.CreateDirectory(incoming);
        Directory.CreateDirectory(final);
        File.WriteAllText(Path.Combine(final, "previous.zip"), "previous complete capture");
        try
        {
            var error = Assert.Throws<IOException>(() => ArchiveSyncService.PromoteIncomingRelease(
                incoming,
                final,
                root,
                (source, destination) =>
                {
                    if (destination == final)
                    {
                        throw new IOException("Injected final-directory move failure.");
                    }

                    Directory.Move(source, destination);
                }));

            var backup = Assert.Single(Directory.GetDirectories(root, ".backup-*"));
            Assert.Contains(backup, error.Message);
            Assert.Equal("previous complete capture", File.ReadAllText(Path.Combine(backup, "previous.zip")));
            Assert.IsType<AggregateException>(error.InnerException);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public async Task SyncAsync_CaseCollidingAssetNames_FailsInsteadOfSilentlyOverwritingVariant()
    {
        var rootBase = Path.Combine(Path.GetTempPath(), "archive-release-collision-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");
        const string projectId = "release-collision-project";
        Directory.CreateDirectory(sourceRoot);
        try
        {
            var releases = new FakeArchiveReleaseClient
            {
                LatestReleaseResult = new ArchiveReleaseInfo(
                    "v1", "v1", "notes", DateTime.UtcNow,
                    new[]
                    {
                        new ArchiveReleaseAsset("App.zip", "https://downloads.example.test/App.zip", 1, null),
                        new ArchiveReleaseAsset("app.zip", "https://downloads.example.test/app.zip", 1, null)
                    })
            };
            var state = new FakeStateStore();
            var service = CreateForgejoCaptureService(releases, state);

            var result = await service.SyncAsync(
                CreateForgejoCaptureRequest(projectId, archiveRoot, sourceRoot),
                progress: null,
                CancellationToken.None);

            Assert.Equal(ArchiveSyncStatus.Failed, result.Status);
            Assert.Equal(ArchiveReleaseStatus.Incomplete, state.LastSaved!.ReleaseStatus);
            Assert.Empty(releases.Downloads);
            Assert.False(Directory.Exists(Path.Combine(archiveRoot, projectId, "releases", "v1")));
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    private static ArchiveSyncService CreateForgejoCaptureService(
        IArchiveReleaseClient releases,
        FakeStateStore state,
        Action<string>? seedArchive = null)
    {
        var git = new FakeGitProcessAdapter
        {
            Handler = (args, _) =>
            {
                if (args.Length > 0 && args[0] == "ls-remote")
                {
                    return Ok("abc\trefs/heads/master\n");
                }

                if (args.Length >= 2 && args[0] == "rev-parse" && args[1] == "HEAD")
                {
                    return Ok("abc123\n");
                }

                throw new InvalidOperationException("Unexpected git command: " + string.Join(" ", args));
            }
        };
        var clone = new FakeCloneService
        {
            NextResult = CloneResult.Ok("master", "abc123", "cloned"),
            OnClone = request =>
            {
                Directory.CreateDirectory(Path.Combine(request.DestinationPath, ".git"));
                seedArchive?.Invoke(request.DestinationPath);
            }
        };
        return new ArchiveSyncService(git, clone, state, releases);
    }

    private static ArchiveSyncRequest CreateForgejoCaptureRequest(
        string projectId,
        string archiveRoot,
        string sourceRoot) =>
        new(
            projectId,
            "https://forge.example.test/eden-emu/eden.git",
            archiveRoot,
            ArchiveWorkingSourceInventory.Complete(new[]
            {
                new ArchiveWorkingSource("forgejo-working-source", sourceRoot)
            }));

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static bool TryGetUnusedDriveLetter(out char driveLetter)
    {
        driveLetter = '\0';
        var usedLetters = DriveInfo.GetDrives()
            .Select(drive => char.ToUpperInvariant(drive.Name[0]))
            .ToHashSet();

        for (var candidate = 'Z'; candidate >= 'D'; candidate--)
        {
            if (!usedLetters.Contains(candidate))
            {
                driveLetter = candidate;
                return true;
            }
        }

        return false;
    }

    private static bool TryCreateJunction(string junction, string target)
    {
        try
        {
            var psi = new ProcessStartInfo(
                "cmd.exe",
                $"/c mklink /J \"{junction}\" \"{target}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            proc.WaitForExit(5_000);
            return proc.ExitCode == 0 && Directory.Exists(junction);
        }
        catch
        {
            return false;
        }
    }

    private static void TryRemoveJunction(string junction)
    {
        try
        {
            if (!Directory.Exists(junction))
            {
                return;
            }

            var psi = new ProcessStartInfo("cmd.exe", $"/c rmdir \"{junction}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
        }
        catch
        {
        }
    }
}
