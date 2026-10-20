#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Infrastructure.Archive
{
    /// <summary>
    /// Orchestrates the local safety-net copy for one <see cref="ArchiveGroup"/>
    /// project: first-run full clone, subsequent-run fetch + fast-forward,
    /// dead-upstream detection via <c>git ls-remote</c>, and a hash-verified
    /// latest-release capture. Read-only against hosted remotes; never
    /// deletes the local clone, even when the upstream has disappeared.
    /// </summary>
    public sealed class ArchiveSyncService : IArchiveSyncService, IArchiveSyncSafetyService
    {
        private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan LsRemoteTimeout = TimeSpan.FromSeconds(20);

        private static readonly string[] GoneRemoteMarkers =
        {
            "repository not found",
            "could not read from remote repository",
            "does not exist",
            "access denied",
            "403"
        };

        private readonly IGitProcessAdapter _gitAdapter;
        private readonly IAsyncCloneService _cloneService;
        private readonly IArchiveStateStore _stateStore;
        private readonly IArchiveReleaseClient _releaseClient;
        private readonly GitWorkspaceInspector _workspaceInspector;

        private const string OwnerMarkerFileName = "controltower-archive-owner";

        public ArchiveSyncService(
            IGitProcessAdapter gitAdapter,
            IAsyncCloneService cloneService,
            IArchiveStateStore stateStore,
            IArchiveReleaseClient releaseClient)
        {
            _gitAdapter = gitAdapter ?? throw new ArgumentNullException(nameof(gitAdapter));
            _cloneService = cloneService ?? throw new ArgumentNullException(nameof(cloneService));
            _stateStore = stateStore ?? throw new ArgumentNullException(nameof(stateStore));
            _releaseClient = releaseClient ?? throw new ArgumentNullException(nameof(releaseClient));
            _workspaceInspector = new GitWorkspaceInspector(_gitAdapter);
        }

        public ArchiveSyncSafetyResult ValidateSafety(ArchiveSyncSafetyRequest request) =>
            ArchiveWorkingSourceSafetyValidator.Validate(request);

        public async Task<ArchiveSyncResult> SyncAsync(
            ArchiveSyncRequest request,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            if (request == null || string.IsNullOrWhiteSpace(request.ProjectId))
            {
                return new ArchiveSyncResult(string.Empty, ArchiveSyncStatus.Skipped, "No archive request supplied.", null);
            }

            if (string.IsNullOrWhiteSpace(request.RemoteUrl) ||
                AsyncCloneService.UrlCarriesCredentials(request.RemoteUrl, out _))
            {
                return new ArchiveSyncResult(
                    request.ProjectId, ArchiveSyncStatus.Failed, "Remote URL missing or contains embedded credentials.", null);
            }

            var safety = ValidateSafety(new ArchiveSyncSafetyRequest(
                request.ArchiveStorageRoot,
                request.WorkingSourceInventory));
            if (!safety.IsSafe)
            {
                return new ArchiveSyncResult(
                    request.ProjectId,
                    ArchiveSyncStatus.Failed,
                    safety.Message,
                    null);
            }

            var archiveStorageRoot = safety.EffectiveArchiveStorageRoot;
            if (!ArchiveProjectIdentity.TryResolveArchivePath(
                    archiveStorageRoot,
                    request.ProjectId,
                    out var archivePath,
                    out var archivePathError))
            {
                return new ArchiveSyncResult(
                    request.ProjectId,
                    ArchiveSyncStatus.Failed,
                    archivePathError,
                    null);
            }

            var state = _stateStore.Load(request.ProjectId);
            var ownershipError = await ValidateExistingArchiveAsync(
                archivePath,
                request.ProjectId,
                request.RemoteUrl,
                state,
                ct).ConfigureAwait(false);
            if (ownershipError != null)
            {
                return new ArchiveSyncResult(
                    request.ProjectId,
                    ArchiveSyncStatus.Failed,
                    ownershipError,
                    state.LastSyncedCommitSha);
            }

            try
            {
                Directory.CreateDirectory(archiveStorageRoot);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return new ArchiveSyncResult(
                    request.ProjectId,
                    ArchiveSyncStatus.Failed,
                    "Archive storage root could not be created: " + ex.Message,
                    state.LastSyncedCommitSha);
            }

            // Recheck after creation so a junction or physical drive mapping
            // introduced while the root was being prepared cannot redirect
            // the clone destination.
            if (!TryRevalidateArchiveDestination(
                    request,
                    archiveStorageRoot,
                    request.ProjectId,
                    archivePath,
                    out archivePathError))
            {
                return new ArchiveSyncResult(
                    request.ProjectId,
                    ArchiveSyncStatus.Failed,
                    archivePathError,
                    state.LastSyncedCommitSha);
            }

            RetainPreviousArchivePath(state, archivePath);

            progress?.Report("Checking remote: " + request.ProjectId);

            var (reachable, goneForGood, lsRemoteMessage) = await CheckRemoteAsync(
                request.RemoteUrl, archiveStorageRoot, ct).ConfigureAwait(false);

            if (!reachable)
            {
                state.UpstreamStatus = goneForGood ? ArchiveUpstreamStatus.Unavailable : state.UpstreamStatus;
                state.LastError = lsRemoteMessage;
                _stateStore.Save(state);

                return goneForGood
                    ? new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.UpstreamUnavailable,
                        "Upstream is gone (" + lsRemoteMessage + "). Local archive copy kept untouched at " +
                        ExistingArchiveLocation(state, archivePath),
                        state.LastSyncedCommitSha)
                    : new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.Failed,
                        "Could not reach the remote (transient): " + lsRemoteMessage,
                        state.LastSyncedCommitSha);
            }

            if (!TryRevalidateArchiveDestination(
                    request,
                    archiveStorageRoot,
                    request.ProjectId,
                    archivePath,
                    out archivePathError))
            {
                return new ArchiveSyncResult(
                    request.ProjectId,
                    ArchiveSyncStatus.Failed,
                    archivePathError,
                    state.LastSyncedCommitSha);
            }

            try
            {
                var isNewClone = !Directory.Exists(Path.Combine(archivePath, ".git"));
                string? commitSha;

                if (isNewClone)
                {
                    progress?.Report("Cloning: " + request.ProjectId);
                    var cloneResult = await _cloneService.CloneAsync(
                        new CloneRequest(request.RemoteUrl, archivePath, Branch: null, SingleBranch: false),
                        progress: null,
                        ct).ConfigureAwait(false);

                    if (!cloneResult.Success)
                    {
                        state.LastError = cloneResult.Message;
                        _stateStore.Save(state);
                        return new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Failed, cloneResult.Message, null);
                    }

                    commitSha = cloneResult.CommitSha;
                }

                if (!TryRevalidateArchiveDestination(
                        request,
                        archiveStorageRoot,
                        request.ProjectId,
                        archivePath,
                        out archivePathError))
                {
                    state.LastError = archivePathError;
                    _stateStore.Save(state);
                    return new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.Failed,
                        archivePathError,
                        state.LastSyncedCommitSha);
                }

                if (!isNewClone)
                {
                    var originError = await ValidateArchiveOriginAsync(
                        archivePath,
                        request.RemoteUrl,
                        ct).ConfigureAwait(false);
                    if (originError != null)
                    {
                        state.LastError = originError;
                        _stateStore.Save(state);
                        return new ArchiveSyncResult(
                            request.ProjectId,
                            ArchiveSyncStatus.Failed,
                            originError,
                            state.LastSyncedCommitSha);
                    }
                }

                if (!TryWriteOwnerMarker(
                        archivePath,
                        request.ProjectId,
                        GetRemoteIdentity(request.RemoteUrl),
                        out var markerError))
                {
                    state.LastError = markerError;
                    _stateStore.Save(state);
                    return new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.Failed,
                        markerError,
                        state.LastSyncedCommitSha);
                }

                if (!TryRevalidateArchiveDestination(
                        request,
                        archiveStorageRoot,
                        request.ProjectId,
                        archivePath,
                        out archivePathError))
                {
                    state.LastError = archivePathError;
                    _stateStore.Save(state);
                    return new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.Failed,
                        archivePathError,
                        state.LastSyncedCommitSha);
                }

                // Resolve the root (non-fork) upstream once per sync so the
                // local archive tracks the original project's default branch
                // — the user never pushes to their own fork, so "origin" is
                // not a useful sync target.
                progress?.Report("Checking upstream: " + request.ProjectId);
                ArchiveUpstreamRepo? upstream = null;
                try
                {
                    upstream = await _releaseClient.ResolveUpstreamAsync(request.RemoteUrl, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    AppLogger.Warn("Archive", "Upstream resolution failed for " + request.ProjectId + ": " + ex.Message);
                }

                if (!TryRevalidateArchiveDestination(
                        request,
                        archiveStorageRoot,
                        request.ProjectId,
                        archivePath,
                        out archivePathError))
                {
                    state.LastError = archivePathError;
                    _stateStore.Save(state);
                    return new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.Failed,
                        archivePathError,
                        state.LastSyncedCommitSha);
                }

                if (upstream != null)
                {
                    progress?.Report("Syncing upstream/" + upstream.DefaultBranch + ": " + request.ProjectId);
                    var upstreamMessage = await FetchAndTrackUpstreamAsync(archivePath, upstream, ct).ConfigureAwait(false);
                    if (upstreamMessage != null)
                    {
                        state.LastError = upstreamMessage;
                        _stateStore.Save(state);
                        return new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Failed, upstreamMessage, state.LastSyncedCommitSha);
                    }

                    commitSha = await ResolveHeadShaAsync(archivePath, ct).ConfigureAwait(false);
                }
                else if (!isNewClone)
                {
                    progress?.Report("Syncing: " + request.ProjectId);
                    var updateMessage = await FetchAndFastForwardAsync(archivePath, ct).ConfigureAwait(false);
                    if (updateMessage != null)
                    {
                        state.LastError = updateMessage;
                        _stateStore.Save(state);
                        return new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Failed, updateMessage, state.LastSyncedCommitSha);
                    }

                    commitSha = await ResolveHeadShaAsync(archivePath, ct).ConfigureAwait(false);
                }
                else
                {
                    commitSha = await ResolveHeadShaAsync(archivePath, ct).ConfigureAwait(false);
                }

                state.ArchivePath = archivePath;
                state.UpstreamStatus = ArchiveUpstreamStatus.Reachable;
                state.LastSyncedUtc = DateTime.UtcNow;
                state.LastSyncedCommitSha = commitSha ?? state.LastSyncedCommitSha;
                state.LastError = string.Empty;
                state.UpstreamFullName = upstream?.FullName ?? string.Empty;

                // Prefer the resolved root upstream's releases — the user
                // only ever keeps a fork and never publishes releases of
                // their own, so "the latest release" means the original
                // project's latest release.
                var releaseSourceUrl = upstream?.CloneUrl ?? request.RemoteUrl;
                await CaptureLatestReleaseAsync(
                    request,
                    state,
                    archiveStorageRoot,
                    archivePath,
                    releaseSourceUrl,
                    progress,
                    ct).ConfigureAwait(false);

                if (state.ReleaseStatus == ArchiveReleaseStatus.Incomplete)
                {
                    var releaseError = string.IsNullOrWhiteSpace(state.LastReleaseError)
                        ? "Current release capture did not complete."
                        : state.LastReleaseError;
                    state.LastError = releaseError;
                    _stateStore.Save(state);
                    return new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.Failed,
                        releaseError,
                        state.LastSyncedCommitSha);
                }

                _stateStore.Save(state);
                var message = state.ReleaseStatus == ArchiveReleaseStatus.NoCurrentRelease
                    ? "Archive source synced; no current release is published."
                    : "Archive source and current release archived at " + archivePath;
                return new ArchiveSyncResult(
                    request.ProjectId, ArchiveSyncStatus.Succeeded, message, state.LastSyncedCommitSha);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is HttpRequestException ||
                ex is InvalidDataException ||
                ex is NotSupportedException ||
                ex is GitNotFoundException)
            {
                state.LastError = ex.Message;
                _stateStore.Save(state);
                AppLogger.Error("Archive", "Archive sync failed for " + request.ProjectId + ": " + ex.Message, ex);
                return new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Failed, ex.Message, state.LastSyncedCommitSha);
            }
        }

        private bool TryRevalidateArchiveDestination(
            ArchiveSyncRequest request,
            string archiveStorageRoot,
            string projectId,
            string expectedArchivePath,
            out string error)
        {
            error = string.Empty;
            var safety = ValidateSafety(new ArchiveSyncSafetyRequest(
                request.ArchiveStorageRoot,
                request.WorkingSourceInventory));
            if (!safety.IsSafe)
            {
                error = safety.Message;
                return false;
            }

            if (!string.Equals(
                    safety.EffectiveArchiveStorageRoot,
                    archiveStorageRoot,
                    StringComparison.OrdinalIgnoreCase))
            {
                error = "Archive storage root changed while it was being prepared.";
                return false;
            }

            if (!ArchiveProjectIdentity.TryResolveArchivePath(
                    archiveStorageRoot,
                    projectId,
                    out var validatedPath,
                    out var validationError))
            {
                error = string.IsNullOrWhiteSpace(validationError)
                    ? "Archive destination could not be revalidated safely."
                    : validationError;
                return false;
            }

            if (!string.Equals(validatedPath, expectedArchivePath, StringComparison.OrdinalIgnoreCase))
            {
                error = "Archive destination changed while it was being prepared.";
                return false;
            }

            return true;
        }

        private async Task<string?> ValidateExistingArchiveAsync(
            string archivePath,
            string projectId,
            string expectedRemoteUrl,
            ArchiveRepoState state,
            CancellationToken ct)
        {
            try
            {
                if (!Directory.Exists(archivePath))
                {
                    return null;
                }

                if (AllowedSettingsRoots.HasExistingReparsePoint(archivePath))
                {
                    return "Existing archive destination crosses a reparse point and cannot be used.";
                }

                if (!Directory.EnumerateFileSystemEntries(archivePath).Any())
                {
                    return null;
                }

                var gitDirectory = Path.Combine(archivePath, ".git");
                if (!Directory.Exists(gitDirectory) ||
                    AllowedSettingsRoots.HasExistingReparsePoint(gitDirectory))
                {
                    return "Archive destination is occupied by an unverified directory.";
                }

                var originError = await ValidateArchiveOriginAsync(
                    archivePath,
                    expectedRemoteUrl,
                    ct).ConfigureAwait(false);
                if (originError != null)
                {
                    return originError;
                }

                var expectedIdentity = GetRemoteIdentity(expectedRemoteUrl);
                var markerPath = GetOwnerMarkerPath(archivePath);
                if (File.Exists(markerPath))
                {
                    if (!TryReadOwnerMarker(archivePath, out var markerProjectId, out var markerRemoteIdentity))
                    {
                        return "Existing archive ownership marker is invalid.";
                    }

                    return string.Equals(markerProjectId, projectId, StringComparison.Ordinal) &&
                        string.Equals(markerRemoteIdentity, expectedIdentity, StringComparison.Ordinal)
                        ? null
                        : "Existing archive ownership does not match this project and remote.";
                }

                if (state != null &&
                    string.Equals(state.ProjectId, projectId, StringComparison.Ordinal) &&
                    HasExactProjectLeaf(state.ArchivePath, projectId) &&
                    IsSamePath(state.ArchivePath, archivePath))
                {
                    return null;
                }

                return "Existing archive ownership cannot be proven safely.";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "Existing archive could not be inspected safely: " + ex.Message;
            }
        }

        private async Task<string?> ValidateArchiveOriginAsync(
            string archivePath,
            string expectedRemoteUrl,
            CancellationToken ct)
        {
            var expectedIdentity = GetRemoteIdentity(expectedRemoteUrl);
            if (string.IsNullOrWhiteSpace(expectedIdentity))
            {
                return "Archive remote could not be normalized safely.";
            }

            try
            {
                var origin = await _gitAdapter.RunAsync(
                    new[] { "remote", "get-url", "origin" },
                    archivePath,
                    LsRemoteTimeout,
                    progress: null,
                    ct).ConfigureAwait(false);
                if (origin.Cancelled || origin.ExitCode != 0)
                {
                    return "Existing archive has no readable origin remote.";
                }

                var values = SplitNonEmptyLines(origin.Stdout).ToArray();
                if (values.Length != 1 ||
                    !string.Equals(
                        GetRemoteIdentity(values[0]),
                        expectedIdentity,
                        StringComparison.Ordinal))
                {
                    return "Existing archive origin does not match the requested remote.";
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GitNotFoundException ex)
            {
                return ex.Message;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                return "Existing archive origin could not be inspected safely: " + ex.Message;
            }
        }

        private bool TryReadOwnerMarker(
            string archivePath,
            out string projectId,
            out string remoteIdentity)
        {
            projectId = string.Empty;
            remoteIdentity = string.Empty;
            try
            {
                var markerPath = GetOwnerMarkerPath(archivePath);
                if (!File.Exists(markerPath) ||
                    AllowedSettingsRoots.HasExistingReparsePoint(markerPath))
                {
                    return false;
                }

                var lines = File.ReadAllLines(markerPath);
                if (lines.Length != 2 ||
                    !lines[0].StartsWith("project_id=", StringComparison.Ordinal) ||
                    !lines[1].StartsWith("remote_identity=", StringComparison.Ordinal))
                {
                    return false;
                }

                projectId = lines[0].Substring("project_id=".Length);
                remoteIdentity = lines[1].Substring("remote_identity=".Length);
                return ArchiveProjectIdentity.IsSafe(projectId) &&
                    !string.IsNullOrWhiteSpace(remoteIdentity);
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private bool TryWriteOwnerMarker(
            string archivePath,
            string projectId,
            string remoteIdentity,
            out string error)
        {
            error = string.Empty;
            if (!ArchiveProjectIdentity.IsSafe(projectId) ||
                string.IsNullOrWhiteSpace(remoteIdentity))
            {
                error = "Archive ownership marker could not be prepared safely.";
                return false;
            }

            try
            {
                var gitDirectory = Path.Combine(archivePath, ".git");
                if (!Directory.Exists(gitDirectory) ||
                    AllowedSettingsRoots.HasExistingReparsePoint(gitDirectory))
                {
                    error = "Archive Git metadata cannot be verified safely.";
                    return false;
                }

                var markerPath = GetOwnerMarkerPath(archivePath);
                File.WriteAllLines(markerPath, new[]
                {
                    "project_id=" + projectId,
                    "remote_identity=" + remoteIdentity
                });
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                error = "Archive ownership marker could not be written: " + ex.Message;
                return false;
            }
        }

        private string GetRemoteIdentity(string remoteUrl) =>
            _workspaceInspector.GetRemoteIdentity(remoteUrl);

        private static string GetOwnerMarkerPath(string archivePath) =>
            Path.Combine(archivePath, ".git", OwnerMarkerFileName);

        private static bool IsSamePath(string first, string second)
        {
            try
            {
                return !string.IsNullOrWhiteSpace(first) &&
                    string.Equals(
                        Path.GetFullPath(first),
                        Path.GetFullPath(second),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static bool HasExactProjectLeaf(string path, string projectId)
        {
            try
            {
                return string.Equals(
                    Path.GetFileName(Path.GetFullPath(path)),
                    projectId,
                    StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        private async Task<(bool reachable, bool goneForGood, string message)> CheckRemoteAsync(
            string remoteUrl, string workingDir, CancellationToken ct)
        {
            try
            {
                var run = await _gitAdapter.RunAsync(
                    new[] { "ls-remote", "--heads", "--", remoteUrl },
                    workingDir,
                    LsRemoteTimeout,
                    progress: null,
                    ct).ConfigureAwait(false);

                if (run.Cancelled)
                {
                    return (false, false, "Remote check cancelled.");
                }

                if (run.ExitCode == 0)
                {
                    return (true, false, string.Empty);
                }

                var stderr = run.Stderr ?? string.Empty;
                var isGone = GoneRemoteMarkers.Any(marker =>
                    stderr.Contains(marker, StringComparison.OrdinalIgnoreCase));
                var message = FirstNonEmptyLine(stderr, "git ls-remote failed (exit " + run.ExitCode + ").");
                return (false, isGone, message);
            }
            catch (GitNotFoundException ex)
            {
                return (false, false, ex.Message);
            }
        }

        private async Task<string?> FetchAndFastForwardAsync(string archivePath, CancellationToken ct)
        {
            var fetch = await _gitAdapter.RunAsync(
                new[] { "fetch", "--all", "--tags", "--prune" },
                archivePath, FetchTimeout, progress: null, ct).ConfigureAwait(false);
            if (fetch.ExitCode != 0)
            {
                return FirstNonEmptyLine(fetch.Stderr, "git fetch failed (exit " + fetch.ExitCode + ").");
            }

            var defaultBranchRun = await _gitAdapter.RunAsync(
                new[] { "symbolic-ref", "refs/remotes/origin/HEAD" },
                archivePath, LsRemoteTimeout, progress: null, ct).ConfigureAwait(false);

            var defaultBranch = defaultBranchRun.ExitCode == 0
                ? defaultBranchRun.Stdout.Trim().Replace("refs/remotes/origin/", string.Empty)
                : null;

            if (string.IsNullOrWhiteSpace(defaultBranch))
            {
                // Archive is still fully refreshed (fetch succeeded); we just
                // couldn't determine a default branch to fast-forward onto.
                return null;
            }

            var checkoutRun = await _gitAdapter.RunAsync(
                new[] { "checkout", defaultBranch },
                archivePath, LsRemoteTimeout, progress: null, ct).ConfigureAwait(false);
            if (checkoutRun.ExitCode != 0)
            {
                return FirstNonEmptyLine(checkoutRun.Stderr, "git checkout failed (exit " + checkoutRun.ExitCode + ").");
            }

            var resetRun = await _gitAdapter.RunAsync(
                new[] { "reset", "--hard", "origin/" + defaultBranch },
                archivePath, LsRemoteTimeout, progress: null, ct).ConfigureAwait(false);
            if (resetRun.ExitCode != 0)
            {
                return FirstNonEmptyLine(resetRun.Stderr, "git reset failed (exit " + resetRun.ExitCode + ").");
            }

            return null;
        }

        private async Task<string?> FetchAndTrackUpstreamAsync(string archivePath, ArchiveUpstreamRepo upstream, CancellationToken ct)
        {
            var remoteError = await EnsureRemoteAsync(archivePath, "upstream", upstream.CloneUrl, ct).ConfigureAwait(false);
            if (remoteError != null)
            {
                return remoteError;
            }

            var fetch = await _gitAdapter.RunAsync(
                new[] { "fetch", "upstream", "--tags", "--prune" },
                archivePath, FetchTimeout, progress: null, ct).ConfigureAwait(false);
            if (fetch.ExitCode != 0)
            {
                return FirstNonEmptyLine(fetch.Stderr, "git fetch upstream failed (exit " + fetch.ExitCode + ").");
            }

            // Create-or-reset the local branch directly onto upstream's tip —
            // simpler and more robust than checkout+reset since it works
            // whether the branch already exists locally or not, and GitHub
            // already told us the exact branch name (no refs/remotes/HEAD
            // guessing needed).
            var checkoutRun = await _gitAdapter.RunAsync(
                new[] { "checkout", "-B", upstream.DefaultBranch, "upstream/" + upstream.DefaultBranch },
                archivePath, LsRemoteTimeout, progress: null, ct).ConfigureAwait(false);
            if (checkoutRun.ExitCode != 0)
            {
                return FirstNonEmptyLine(checkoutRun.Stderr, "git checkout upstream branch failed (exit " + checkoutRun.ExitCode + ").");
            }

            return null;
        }

        // Adds the named remote if it's missing, or repoints it if the
        // configured URL has drifted (e.g. upstream renamed). Never removes
        // or touches "origin" — the archive still has it from the clone.
        private async Task<string?> EnsureRemoteAsync(string archivePath, string remoteName, string url, CancellationToken ct)
        {
            var getUrl = await _gitAdapter.RunAsync(
                new[] { "remote", "get-url", remoteName },
                archivePath, LsRemoteTimeout, progress: null, ct).ConfigureAwait(false);

            if (getUrl.ExitCode != 0)
            {
                var addRun = await _gitAdapter.RunAsync(
                    new[] { "remote", "add", remoteName, url },
                    archivePath, LsRemoteTimeout, progress: null, ct).ConfigureAwait(false);
                return addRun.ExitCode == 0
                    ? null
                    : FirstNonEmptyLine(addRun.Stderr, "git remote add " + remoteName + " failed (exit " + addRun.ExitCode + ").");
            }

            if (string.Equals(getUrl.Stdout.Trim(), url, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var setUrlRun = await _gitAdapter.RunAsync(
                new[] { "remote", "set-url", remoteName, url },
                archivePath, LsRemoteTimeout, progress: null, ct).ConfigureAwait(false);
            return setUrlRun.ExitCode == 0
                ? null
                : FirstNonEmptyLine(setUrlRun.Stderr, "git remote set-url " + remoteName + " failed (exit " + setUrlRun.ExitCode + ").");
        }

        private async Task<string?> ResolveHeadShaAsync(string archivePath, CancellationToken ct)
        {
            var run = await _gitAdapter.RunAsync(
                new[] { "rev-parse", "HEAD" }, archivePath, LsRemoteTimeout, progress: null, ct).ConfigureAwait(false);
            return run.ExitCode == 0 ? run.Stdout.Trim() : null;
        }

        private async Task CaptureLatestReleaseAsync(
            ArchiveSyncRequest request,
            ArchiveRepoState state,
            string archiveStorageRoot,
            string archivePath,
            string releaseSourceUrl,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            try
            {
                var release = await _releaseClient.GetLatestReleaseAsync(releaseSourceUrl, ct).ConfigureAwait(false);
                if (release == null)
                {
                    MarkNoCurrentRelease(state);
                    return;
                }

                var expectedAssetNames = GetCanonicalAssetNames(release);
                if (string.Equals(release.TagName, state.LastReleaseTag, StringComparison.Ordinal) &&
                    state.ReleaseStatus == ArchiveReleaseStatus.Captured &&
                    state.LastReleaseIntegrity == ArchiveReleaseIntegrity.Verified &&
                    HasCompleteReleaseCapture(archivePath, state, expectedAssetNames))
                {
                    return;
                }

                progress?.Report("Capturing release " + release.TagName + ": " + request.ProjectId);

                if (!TryRevalidateArchiveDestination(
                        request,
                        archiveStorageRoot,
                        request.ProjectId,
                        archivePath,
                        out _))
                {
                    throw new IOException("Archive destination could not be revalidated before release capture.");
                }

                if (!TryGetSafeChildPath(archivePath, "releases", out var releasesRoot) ||
                    !TryGetSafeChildPath(releasesRoot, Slugify(release.TagName), out var finalDir))
                {
                    throw new IOException("Archive release content path is unsafe.");
                }

                string? incomingDir = null;
                var incomingPromoted = false;
                try
                {
                    incomingDir = GetFreshReleasePath(
                        releasesRoot,
                        ".incoming-" + Slugify(release.TagName) + "-");
                    Directory.CreateDirectory(incomingDir);
                    if (AllowedSettingsRoots.HasExistingReparsePoint(incomingDir))
                    {
                        throw new IOException("Release staging directory is a reparse point.");
                    }

                    var anyMismatch = false;
                    var anyUnverifiable = false;
                    foreach (var asset in release.Assets)
                    {
                        if (!TryGetSafeChildPath(incomingDir, Slugify(asset.Name), out var destination))
                        {
                            throw new IOException("Release asset destination is unsafe.");
                        }

                        var integrity = await _releaseClient
                            .DownloadAndVerifyAsync(asset, destination, ct)
                            .ConfigureAwait(false);

                        if (integrity == ArchiveReleaseIntegrity.Mismatch)
                        {
                            anyMismatch = true;
                        }
                        else if (integrity == ArchiveReleaseIntegrity.Unverifiable)
                        {
                            anyUnverifiable = true;
                        }
                    }

                    if (!TryRevalidateArchiveDestination(
                            request,
                            archiveStorageRoot,
                            request.ProjectId,
                            archivePath,
                            out _))
                    {
                        throw new IOException("Archive destination changed during release download.");
                    }

                    if (anyMismatch)
                    {
                        var mismatchMessage =
                            "Release asset hash mismatch for " + request.ProjectId + " " + release.TagName +
                            " — kept the previous release and discarded the download.";
                        AppLogger.Warn("Archive", mismatchMessage);
                        MarkReleaseIncomplete(state, mismatchMessage, ArchiveReleaseIntegrity.Mismatch);
                        return;
                    }

                    PromoteIncomingRelease(incomingDir, finalDir, releasesRoot, Directory.Move);
                    incomingPromoted = true;

                    if (!anyUnverifiable)
                    {
                        // Fully verified: safe to drop every previous release folder
                        // to keep disk usage bounded to the current release only.
                        foreach (var dir in Directory.EnumerateDirectories(releasesRoot))
                        {
                            if (!string.Equals(dir, finalDir, StringComparison.OrdinalIgnoreCase))
                            {
                                DeleteDirectoryOrThrow(dir, "previous release directory");
                            }
                        }
                    }

                    MarkReleaseCaptured(state, release, finalDir, expectedAssetNames, anyUnverifiable);
                }
                finally
                {
                    if (!incomingPromoted && incomingDir != null)
                    {
                        try
                        {
                            DeleteDirectoryOrThrow(incomingDir, "incomplete release staging directory");
                        }
                        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                        {
                            AppLogger.Error(
                                "Archive",
                                "Release staging cleanup failed for " + request.ProjectId + ": " + ex.Message,
                                ex);
                            throw new IOException(
                                "Release staging cleanup failed at " + incomingDir + ": " + ex.Message,
                                ex);
                        }
                    }
                }
            }
            catch (NotSupportedException ex)
            {
                MarkReleaseUnsupported(state, ex.Message);
                throw;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                MarkReleaseIncomplete(state, ex.Message, ArchiveReleaseIntegrity.NotCaptured);
                throw;
            }
        }

        private static void MarkNoCurrentRelease(ArchiveRepoState state)
        {
            state.ReleaseStatus = ArchiveReleaseStatus.NoCurrentRelease;
            state.LastReleaseTag = string.Empty;
            state.LastReleaseIntegrity = ArchiveReleaseIntegrity.NotCaptured;
            state.LastReleaseCapturedUtc = null;
            state.LastReleaseDirectoryName = string.Empty;
            state.LastReleaseAssetNames.Clear();
            state.LastReleaseError = string.Empty;
        }

        private static void MarkReleaseCaptured(
            ArchiveRepoState state,
            ArchiveReleaseInfo release,
            string finalDir,
            IReadOnlyList<string> expectedAssetNames,
            bool anyUnverifiable)
        {
            state.ReleaseStatus = ArchiveReleaseStatus.Captured;
            state.LastReleaseTag = release.TagName;
            state.LastReleaseIntegrity = anyUnverifiable
                ? ArchiveReleaseIntegrity.Unverifiable
                : ArchiveReleaseIntegrity.Verified;
            state.LastReleaseCapturedUtc = DateTime.UtcNow;
            state.LastReleaseDirectoryName = Path.GetFileName(finalDir);
            state.LastReleaseAssetNames.Clear();
            foreach (var assetName in expectedAssetNames)
            {
                state.LastReleaseAssetNames.Add(assetName);
            }

            state.LastReleaseError = string.Empty;
        }

        private static void MarkReleaseIncomplete(
            ArchiveRepoState state,
            string error,
            ArchiveReleaseIntegrity integrity)
        {
            state.ReleaseStatus = ArchiveReleaseStatus.Incomplete;
            state.LastReleaseTag = string.Empty;
            state.LastReleaseIntegrity = integrity;
            state.LastReleaseCapturedUtc = null;
            state.LastReleaseDirectoryName = string.Empty;
            state.LastReleaseAssetNames.Clear();
            state.LastReleaseError = error ?? string.Empty;
        }

        private static void MarkReleaseUnsupported(ArchiveRepoState state, string error)
        {
            state.ReleaseStatus = ArchiveReleaseStatus.Unsupported;
            state.LastReleaseTag = string.Empty;
            state.LastReleaseIntegrity = ArchiveReleaseIntegrity.NotCaptured;
            state.LastReleaseCapturedUtc = null;
            state.LastReleaseDirectoryName = string.Empty;
            state.LastReleaseAssetNames.Clear();
            state.LastReleaseError = error ?? string.Empty;
        }

        private static IReadOnlyList<string> GetCanonicalAssetNames(ArchiveReleaseInfo release)
        {
            var assetNames = new List<string>();
            var distinctNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var asset in release.Assets)
            {
                if (asset == null || string.IsNullOrWhiteSpace(asset.Name))
                {
                    throw new InvalidDataException("Release metadata contains an asset without a name.");
                }

                var assetName = Slugify(asset.Name);
                if (!distinctNames.Add(assetName))
                {
                    throw new InvalidDataException(
                        "Release metadata contains assets that collide after local filename normalization.");
                }

                assetNames.Add(assetName);
            }

            return assetNames;
        }

        private static bool HasCompleteReleaseCapture(
            string archivePath,
            ArchiveRepoState state,
            IReadOnlyList<string> expectedAssetNames)
        {
            if (state == null ||
                state.ReleaseStatus != ArchiveReleaseStatus.Captured ||
                (state.LastReleaseIntegrity != ArchiveReleaseIntegrity.Verified &&
                 state.LastReleaseIntegrity != ArchiveReleaseIntegrity.Unverifiable) ||
                string.IsNullOrWhiteSpace(state.LastReleaseDirectoryName) ||
                !TryGetSafeChildPath(archivePath, "releases", out var releasesRoot) ||
                !TryGetSafeChildPath(releasesRoot, state.LastReleaseDirectoryName, out var releaseDirectory) ||
                !Directory.Exists(releaseDirectory))
            {
                return false;
            }

            if (state.LastReleaseAssetNames.Count != expectedAssetNames.Count ||
                !state.LastReleaseAssetNames
                    .OrderBy(assetName => assetName, StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(
                        expectedAssetNames.OrderBy(assetName => assetName, StringComparer.OrdinalIgnoreCase),
                        StringComparer.OrdinalIgnoreCase))
            {
                return false;
            }

            foreach (var assetName in expectedAssetNames)
            {
                if (!TryGetSafeChildPath(releaseDirectory, assetName, out var assetPath) ||
                    !File.Exists(assetPath))
                {
                    return false;
                }
            }

            return true;
        }

        private static string GetFreshReleasePath(string releasesRoot, string prefix)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                if (!TryGetSafeChildPath(
                        releasesRoot,
                        prefix + Guid.NewGuid().ToString("N"),
                        out var candidate))
                {
                    throw new IOException("Release staging path is unsafe.");
                }

                if (!Directory.Exists(candidate) && !File.Exists(candidate))
                {
                    return candidate;
                }
            }

            throw new IOException("Could not create a unique release staging path.");
        }

        internal static void PromoteIncomingRelease(
            string incomingDir,
            string finalDir,
            string releasesRoot,
            Action<string, string> moveDirectory)
        {
            if (moveDirectory == null)
            {
                throw new ArgumentNullException(nameof(moveDirectory));
            }

            string? backupDir = null;
            if (Directory.Exists(finalDir))
            {
                backupDir = GetFreshReleasePath(
                    releasesRoot,
                    ".backup-" + Path.GetFileName(finalDir) + "-");
                moveDirectory(finalDir, backupDir);
            }

            try
            {
                moveDirectory(incomingDir, finalDir);
            }
            catch (Exception promotionException) when (
                promotionException is IOException ||
                promotionException is UnauthorizedAccessException)
            {
                if (backupDir == null)
                {
                    throw;
                }

                try
                {
                    moveDirectory(backupDir, finalDir);
                }
                catch (Exception restorationException) when (
                    restorationException is IOException ||
                    restorationException is UnauthorizedAccessException)
                {
                    throw new IOException(
                        "Release promotion failed and the previous capture could not be restored. " +
                        "The previous capture remains at " + backupDir + ".",
                        new AggregateException(promotionException, restorationException));
                }

                throw;
            }

            if (backupDir != null)
            {
                DeleteDirectoryOrThrow(backupDir, "replaced release backup");
            }
        }

        private static void DeleteDirectoryOrThrow(string path, string description)
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            if (AllowedSettingsRoots.HasExistingReparsePoint(path))
            {
                throw new IOException("Refused to delete reparse-point " + description + " at " + path + ".");
            }

            Directory.Delete(path, recursive: true);
        }

        private static bool TryGetSafeChildPath(string parent, string childName, out string path)
        {
            path = string.Empty;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.IsNullOrWhiteSpace(childName) ||
                childName.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                childName.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
            {
                return false;
            }

            try
            {
                var fullParent = Path.GetFullPath(parent)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var candidate = Path.GetFullPath(Path.Combine(fullParent, childName));
                if (!candidate.StartsWith(
                        fullParent + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase) ||
                    AllowedSettingsRoots.HasExistingReparsePoint(candidate))
                {
                    return false;
                }

                path = candidate;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FirstNonEmptyLine(string? text, string fallback)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return fallback;
            }

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    return trimmed;
                }
            }
            return fallback;
        }

        private static System.Collections.Generic.IEnumerable<string> SplitNonEmptyLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                yield break;
            }

            using var reader = new StringReader(text);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0)
                {
                    yield return trimmed;
                }
            }
        }

        private static void RetainPreviousArchivePath(ArchiveRepoState state, string archivePath)
        {
            if (state == null ||
                string.IsNullOrWhiteSpace(state.ArchivePath) ||
                string.Equals(state.ArchivePath, archivePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (!state.PreviousArchivePaths.Any(path =>
                    string.Equals(path, state.ArchivePath, StringComparison.OrdinalIgnoreCase)))
            {
                state.PreviousArchivePaths.Add(state.ArchivePath);
            }
        }

        private static string ExistingArchiveLocation(ArchiveRepoState state, string requestedArchivePath)
        {
            return !string.IsNullOrWhiteSpace(state?.ArchivePath)
                ? state.ArchivePath
                : requestedArchivePath;
        }

        internal static string Slugify(string value)
        {
            var original = value ?? string.Empty;
            var builder = new StringBuilder(original.Length);
            var changed = false;
            foreach (var character in original)
            {
                if (character < 32 ||
                    character == '<' ||
                    character == '>' ||
                    character == ':' ||
                    character == '"' ||
                    character == '/' ||
                    character == '\\' ||
                    character == '|' ||
                    character == '?' ||
                    character == '*')
                {
                    builder.Append('_');
                    changed = true;
                }
                else
                {
                    builder.Append(character);
                }
            }

            var result = builder.ToString().TrimEnd(' ', '.');
            changed |= !string.Equals(original, result, StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(result) ||
                string.Equals(result, ".", StringComparison.Ordinal) ||
                string.Equals(result, "..", StringComparison.Ordinal) ||
                IsReservedWindowsFileName(result))
            {
                result = "item";
                changed = true;
            }

            if (!changed)
            {
                return result;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(original));
            return result + "-" + Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        private static bool IsReservedWindowsFileName(string value)
        {
            var baseName = value.Split('.')[0];
            if (string.Equals(baseName, "CON", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(baseName, "PRN", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(baseName, "AUX", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(baseName, "NUL", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return baseName.Length == 4 &&
                (baseName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                 baseName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                baseName[3] >= '1' &&
                baseName[3] <= '9';
        }
    }
}
