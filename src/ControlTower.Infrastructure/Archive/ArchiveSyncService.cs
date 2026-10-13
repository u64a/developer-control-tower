#nullable enable
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Git;

namespace ControlTower.Infrastructure.Archive
{
    /// <summary>
    /// Orchestrates the local safety-net copy for one <see cref="ArchiveGroup"/>
    /// project: first-run full clone, subsequent-run fetch + fast-forward,
    /// dead-upstream detection via <c>git ls-remote</c>, and a hash-verified
    /// latest-release capture. Read-only against GitHub; never deletes the
    /// local clone, even when the upstream has disappeared.
    /// </summary>
    public sealed class ArchiveSyncService : IArchiveSyncService
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
        }

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

            if (string.IsNullOrWhiteSpace(request.ArchiveStorageRoot))
            {
                return new ArchiveSyncResult(
                    request.ProjectId, ArchiveSyncStatus.Failed, "No archive storage root is configured.", null);
            }

            var state = _stateStore.Load(request.ProjectId);
            Directory.CreateDirectory(request.ArchiveStorageRoot);

            var archivePath = string.IsNullOrWhiteSpace(state.ArchivePath)
                ? Path.Combine(request.ArchiveStorageRoot, Slugify(request.ProjectId))
                : state.ArchivePath;

            progress?.Report("Checking remote: " + request.ProjectId);

            var (reachable, goneForGood, lsRemoteMessage) = await CheckRemoteAsync(
                request.RemoteUrl, request.ArchiveStorageRoot, ct).ConfigureAwait(false);

            if (!reachable)
            {
                state.ArchivePath = archivePath;
                state.UpstreamStatus = goneForGood ? ArchiveUpstreamStatus.Unavailable : state.UpstreamStatus;
                state.LastError = lsRemoteMessage;
                _stateStore.Save(state);

                return goneForGood
                    ? new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.UpstreamUnavailable,
                        "Upstream is gone (" + lsRemoteMessage + "). Local archive copy kept untouched at " + archivePath,
                        state.LastSyncedCommitSha)
                    : new ArchiveSyncResult(
                        request.ProjectId,
                        ArchiveSyncStatus.Failed,
                        "Could not reach the remote (transient): " + lsRemoteMessage,
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
                await CaptureLatestReleaseAsync(request, state, archivePath, releaseSourceUrl, progress, ct).ConfigureAwait(false);

                _stateStore.Save(state);
                return new ArchiveSyncResult(
                    request.ProjectId, ArchiveSyncStatus.Succeeded, "Archive synced at " + archivePath, state.LastSyncedCommitSha);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is GitNotFoundException)
            {
                state.LastError = ex.Message;
                _stateStore.Save(state);
                AppLogger.Error("Archive", "Archive sync failed for " + request.ProjectId + ": " + ex.Message, ex);
                return new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Failed, ex.Message, state.LastSyncedCommitSha);
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
            string archivePath,
            string releaseSourceUrl,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            ArchiveReleaseInfo? release;
            try
            {
                release = await _releaseClient.GetLatestReleaseAsync(releaseSourceUrl, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                AppLogger.Warn("Archive", "Release lookup failed for " + request.ProjectId + ": " + ex.Message);
                return;
            }

            if (release == null ||
                string.Equals(release.TagName, state.LastReleaseTag, StringComparison.Ordinal) &&
                state.LastReleaseIntegrity == ArchiveReleaseIntegrity.Verified)
            {
                return;
            }

            progress?.Report("Capturing release " + release.TagName + ": " + request.ProjectId);

            var releasesRoot = Path.Combine(archivePath, "releases");
            var incomingDir = Path.Combine(releasesRoot, ".incoming-" + Slugify(release.TagName));
            Directory.CreateDirectory(incomingDir);

            var anyMismatch = false;
            var anyUnverifiable = false;
            foreach (var asset in release.Assets)
            {
                var destination = Path.Combine(incomingDir, Slugify(asset.Name));
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

            if (anyMismatch)
            {
                AppLogger.Warn(
                    "Archive",
                    "Release asset hash mismatch for " + request.ProjectId + " " + release.TagName +
                    " — kept the previous release, discarded the download.");
                TryDeleteDirectory(incomingDir);
                state.LastReleaseIntegrity = ArchiveReleaseIntegrity.Mismatch;
                return;
            }

            var finalDir = Path.Combine(releasesRoot, Slugify(release.TagName));
            if (Directory.Exists(finalDir))
            {
                TryDeleteDirectory(finalDir);
            }
            Directory.Move(incomingDir, finalDir);

            if (!anyUnverifiable)
            {
                // Fully verified: safe to drop every previous release folder
                // to keep disk usage bounded to the current release only.
                foreach (var dir in Directory.EnumerateDirectories(releasesRoot))
                {
                    if (!string.Equals(dir, finalDir, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteDirectory(dir);
                    }
                }
            }

            state.LastReleaseTag = release.TagName;
            state.LastReleaseIntegrity = anyUnverifiable
                ? ArchiveReleaseIntegrity.Unverifiable
                : ArchiveReleaseIntegrity.Verified;
            state.LastReleaseCapturedUtc = DateTime.UtcNow;
        }

        private static void TryDeleteDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
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

        internal static string Slugify(string value)
        {
            var result = value ?? string.Empty;
            foreach (var invalid in Path.GetInvalidFileNameChars())
            {
                result = result.Replace(invalid, '_');
            }
            return result;
        }
    }
}
