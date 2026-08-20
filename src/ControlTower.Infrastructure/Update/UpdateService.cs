#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Launch;

namespace ControlTower.Infrastructure.Update
{
    /// <summary>
    /// Real implementation of <see cref="IUpdateService"/>. Resolves the
    /// repo root by walking up from the running executable (or honouring
    /// an explicit override), runs the git plumbing through
    /// <see cref="IGitProcessAdapter"/>, and spawns the user-visible
    /// updater console through <see cref="IShellLauncher"/>. All process
    /// invocations go through one of those two seams — no direct
    /// <see cref="System.Diagnostics.Process"/> use.
    /// </summary>
    public sealed class UpdateService : IUpdateService
    {
        private const string SolutionFileName = "DeveloperControlTower.sln";
        private const string DesktopExecutableName = "ControlTower.Desktop.exe";

        // Path inside the repo to the Desktop csproj. Used to drive
        // `dotnet publish` from the update script. We publish (not just
        // build) so the staging output contains exactly the runtime files
        // the installed app needs, with no stale bin/obj cruft mixed in.
        private const string DesktopRelativeProjectPath =
            @"src\ControlTower.Desktop\ControlTower.Desktop.csproj";

        // Target framework moniker the Desktop project ships with. Kept as
        // a constant so the update / install scripts stay in lockstep with
        // the csproj without anyone having to grep two repos.
        private const string DesktopTargetFramework = "net8.0-windows";

        // Sentinel file dropped next to the installed .exe by the publish /
        // install script. Records the absolute path to the source git
        // clone so the updater works regardless of where the .exe was
        // copied (e.g. C:\Program Files\Development Tower). One non-empty
        // non-comment line = the repo root. Lines starting with '#' are
        // ignored so the publish script can leave a human-readable header.
        private const string RepoRootSentinelFileName = "update-repo-root.txt";
        private const string InstallOwnershipMarkerFileName = ".developer-control-tower-install";
        private const string InstallOwnershipMarkerContents =
            "Developer Control Tower managed install v1";

        private static readonly char[] UnsafeCmdCharacters =
            { '\0', '\r', '\n', '"', '&', '|', '<', '>', '^', '%', '!' };
        private static readonly Regex BranchNamePattern = new(
            @"^[A-Za-z0-9_][A-Za-z0-9._/-]*$",
            RegexOptions.CultureInvariant);
        private static readonly Regex RemoteNamePattern = new(
            @"^[A-Za-z0-9_][A-Za-z0-9._-]*$",
            RegexOptions.CultureInvariant);
        private static readonly Regex ShaPattern = new(
            @"^[0-9A-Fa-f]{7,64}$",
            RegexOptions.CultureInvariant);

        private static readonly TimeSpan GitFastTimeout = TimeSpan.FromSeconds(20);
        private static readonly TimeSpan GitFetchTimeout = TimeSpan.FromSeconds(60);

        private readonly IGitProcessAdapter _gitAdapter;
        private readonly IShellLauncher _shellLauncher;
        private readonly Func<string> _executablePathProvider;
        private readonly Func<int> _currentProcessIdProvider;
        private readonly Func<string> _tempPathProvider;
        private readonly Func<string> _logFolderProvider;
        private readonly Action<string, string, Exception?> _logger;
        private readonly Func<string, bool> _installDirWritableProbe;

        public UpdateProviderKind ProviderKind => UpdateProviderKind.SourceRepository;

        public UpdateService(IGitProcessAdapter gitAdapter, IShellLauncher shellLauncher)
            : this(gitAdapter, shellLauncher, null, null, null, null, null, null)
        {
        }

        // Test seam: lets tests stub the running-exe path, current PID, the
        // temp-folder location, the app-log folder, the log-writer callback,
        // and the install-dir writability probe so the launch flow can be
        // exercised without touching real %TEMP% / %LOCALAPPDATA%, without
        // polluting the real app log file, and without depending on whether
        // the test host can write to the simulated install directory.
        public UpdateService(
            IGitProcessAdapter gitAdapter,
            IShellLauncher shellLauncher,
            Func<string>? executablePathProvider,
            Func<int>? currentProcessIdProvider,
            Func<string>? tempPathProvider,
            Func<string>? logFolderProvider = null,
            Action<string, string, Exception?>? logger = null,
            Func<string, bool>? installDirWritableProbe = null)
        {
            _gitAdapter = gitAdapter ?? throw new ArgumentNullException(nameof(gitAdapter));
            _shellLauncher = shellLauncher ?? throw new ArgumentNullException(nameof(shellLauncher));
            _executablePathProvider = executablePathProvider ?? DefaultExecutablePath;
            _currentProcessIdProvider = currentProcessIdProvider ?? (() => Process.GetCurrentProcess().Id);
            _tempPathProvider = tempPathProvider ?? Path.GetTempPath;
            _logFolderProvider = logFolderProvider ?? (() => AppLogger.LogFolder);
            _logger = logger ?? DefaultLogger;
            _installDirWritableProbe = installDirWritableProbe ?? DefaultInstallDirWritable;
        }

        private static void DefaultLogger(string level, string message, Exception? ex)
        {
            switch (level)
            {
                case "WARN":
                    AppLogger.Warn("Update", message);
                    break;
                case "ERROR":
                    AppLogger.Error("Update", message, ex!);
                    break;
                default:
                    AppLogger.Info("Update", message);
                    break;
            }
        }

        private void LogInfo(string message) => _logger("INFO", message, null);
        private void LogWarn(string message) => _logger("WARN", message, null);
        private void LogError(string message, Exception? ex = null) => _logger("ERROR", message, ex);

        public async Task<UpdateCheckResult> CheckForUpdatesAsync(UpdateOptions options, CancellationToken ct)
        {
            var effective = options ?? UpdateOptions.Defaults();
            var branchHint = string.IsNullOrWhiteSpace(effective.Branch) ? "main" : effective.Branch.Trim();
            var overrideHint = string.IsNullOrWhiteSpace(effective.RepoRootOverride) ? "<auto>" : effective.RepoRootOverride;
            LogInfo($"Update check started. branch={branchHint} repoOverride={overrideHint}");

            UpdateCheckResult result;
            try
            {
                result = await CheckForUpdatesCoreAsync(effective, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogError("Update check threw an unhandled exception: " + ex.Message, ex);
                throw;
            }

            LogInfo(
                "Update check complete. status=" + result.Status +
                " branch=" + (string.IsNullOrEmpty(result.Branch) ? "<unknown>" : result.Branch) +
                " current=" + ShortSha(result.CurrentSha) +
                " remote=" + ShortSha(result.RemoteSha) +
                " ahead=" + result.CommitsAhead +
                " behind=" + result.CommitsBehind +
                " message=\"" + (result.Message ?? string.Empty) + "\"");

            return result;
        }

        private async Task<UpdateCheckResult> CheckForUpdatesCoreAsync(UpdateOptions effective, CancellationToken ct)
        {
            var configuredBranch = string.IsNullOrWhiteSpace(effective.Branch) ? "main" : effective.Branch.Trim();
            var executablePath = SafeExecutablePath();

            if (!TryResolveRepoRoot(effective, out var repoRoot, out var resolveStatus, out var resolveMessage))
            {
                return new UpdateCheckResult(
                    Status: resolveStatus,
                    CurrentSha: string.Empty,
                    RemoteSha: string.Empty,
                    Branch: string.Empty,
                    ConfiguredBranch: configuredBranch,
                    CommitsBehind: 0,
                    CommitsAhead: 0,
                    RepoRoot: repoRoot,
                    ExecutablePath: executablePath,
                    Message: resolveMessage);
            }

            string currentSha;
            try
            {
                currentSha = await RunGitTrimmedAsync(
                    new[] { "rev-parse", "HEAD" }, repoRoot, GitFastTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWarn("rev-parse HEAD failed: " + ex.Message);
                return Failure(UpdateStatus.RepoNotFound, repoRoot, configuredBranch, executablePath,
                    "Could not read HEAD: " + ex.Message);
            }

            string currentBranch;
            try
            {
                currentBranch = await RunGitTrimmedAsync(
                    new[] { "rev-parse", "--abbrev-ref", "HEAD" }, repoRoot, GitFastTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWarn("rev-parse --abbrev-ref HEAD failed: " + ex.Message);
                return Failure(UpdateStatus.RepoNotFound, repoRoot, configuredBranch, executablePath,
                    "Could not read current branch: " + ex.Message);
            }

            if (!string.Equals(currentBranch, configuredBranch, StringComparison.Ordinal))
            {
                return new UpdateCheckResult(
                    Status: UpdateStatus.WrongBranch,
                    CurrentSha: currentSha,
                    RemoteSha: string.Empty,
                    Branch: currentBranch,
                    ConfiguredBranch: configuredBranch,
                    CommitsBehind: 0,
                    CommitsAhead: 0,
                    RepoRoot: repoRoot,
                    ExecutablePath: executablePath,
                    Message: $"Currently on '{currentBranch}'. Update branch is '{configuredBranch}'.");
            }

            // git rev-parse --symbolic-full-name @{upstream} -> refs/remotes/<remote>/<branch>
            var upstreamRun = await _gitAdapter.RunAsync(
                new[] { "rev-parse", "--symbolic-full-name", "@{upstream}" },
                repoRoot, GitFastTimeout, null, ct).ConfigureAwait(false);

            if (upstreamRun.ExitCode != 0 || string.IsNullOrWhiteSpace(upstreamRun.Stdout))
            {
                return new UpdateCheckResult(
                    Status: UpdateStatus.NoUpstream,
                    CurrentSha: currentSha,
                    RemoteSha: string.Empty,
                    Branch: currentBranch,
                    ConfiguredBranch: configuredBranch,
                    CommitsBehind: 0,
                    CommitsAhead: 0,
                    RepoRoot: repoRoot,
                    ExecutablePath: executablePath,
                    Message: "No upstream branch is configured for '" + currentBranch + "'.");
            }

            var upstreamRef = upstreamRun.Stdout.Trim();
            if (!TryParseRemoteName(upstreamRef, configuredBranch, out var remoteName))
            {
                return new UpdateCheckResult(
                    Status: UpdateStatus.NoUpstream,
                    CurrentSha: currentSha,
                    RemoteSha: string.Empty,
                    Branch: currentBranch,
                    ConfiguredBranch: configuredBranch,
                    CommitsBehind: 0,
                    CommitsAhead: 0,
                    RepoRoot: repoRoot,
                    ExecutablePath: executablePath,
                    Message:
                        $"Branch '{currentBranch}' must track a supported remote branch with the same name.");
            }

            // git fetch <remote> <branch>
            var fetchRun = await _gitAdapter.RunAsync(
                new[] { "fetch", remoteName, configuredBranch },
                repoRoot, GitFetchTimeout, null, ct).ConfigureAwait(false);

            if (fetchRun.ExitCode != 0 || fetchRun.TimedOut || fetchRun.Cancelled)
            {
                return new UpdateCheckResult(
                    Status: UpdateStatus.FetchFailed,
                    CurrentSha: currentSha,
                    RemoteSha: string.Empty,
                    Branch: currentBranch,
                    ConfiguredBranch: configuredBranch,
                    CommitsBehind: 0,
                    CommitsAhead: 0,
                    RepoRoot: repoRoot,
                    ExecutablePath: executablePath,
                    Message: "git fetch failed (offline or auth issue).");
            }

            string remoteSha;
            try
            {
                remoteSha = await RunGitTrimmedAsync(
                    new[] { "rev-parse", "@{upstream}" }, repoRoot, GitFastTimeout, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Failure(UpdateStatus.FetchFailed, repoRoot, configuredBranch, executablePath,
                    "Could not resolve upstream SHA: " + ex.Message);
            }

            // git rev-list --left-right --count HEAD...@{upstream} -> "<ahead>\t<behind>"
            var counts = await _gitAdapter.RunAsync(
                new[] { "rev-list", "--left-right", "--count", "HEAD...@{upstream}" },
                repoRoot, GitFastTimeout, null, ct).ConfigureAwait(false);

            if (counts.ExitCode != 0
                || counts.TimedOut
                || counts.Cancelled
                || !TryParseAheadBehind(counts.Stdout, out var ahead, out var behind))
            {
                return Failure(
                    UpdateStatus.FetchFailed,
                    repoRoot,
                    configuredBranch,
                    executablePath,
                    "Could not compare local HEAD with its upstream branch.");
            }

            // Decision table — order matters. Ahead/diverged is reported even
            // when the tree is dirty so a user with local commits gets the
            // right message ("push first") rather than "stash changes".
            if (ahead > 0 && behind > 0)
            {
                return new UpdateCheckResult(
                    Status: UpdateStatus.Diverged,
                    CurrentSha: currentSha,
                    RemoteSha: remoteSha,
                    Branch: currentBranch,
                    ConfiguredBranch: configuredBranch,
                    CommitsBehind: behind,
                    CommitsAhead: ahead,
                    RepoRoot: repoRoot,
                    ExecutablePath: executablePath,
                    Message: $"Local and origin have diverged ({ahead} ahead, {behind} behind). Resolve in git first.");
            }

            if (ahead > 0)
            {
                return new UpdateCheckResult(
                    Status: UpdateStatus.AheadOfOrigin,
                    CurrentSha: currentSha,
                    RemoteSha: remoteSha,
                    Branch: currentBranch,
                    ConfiguredBranch: configuredBranch,
                    CommitsBehind: 0,
                    CommitsAhead: ahead,
                    RepoRoot: repoRoot,
                    ExecutablePath: executablePath,
                    Message: $"You have {ahead} local commit(s) not yet pushed. Push before updating.");
            }

            if (behind > 0)
            {
                if (await IsDirtyAsync(repoRoot, ct).ConfigureAwait(false))
                {
                    return new UpdateCheckResult(
                        Status: UpdateStatus.DirtyTree,
                        CurrentSha: currentSha,
                        RemoteSha: remoteSha,
                        Branch: currentBranch,
                        ConfiguredBranch: configuredBranch,
                        CommitsBehind: behind,
                        CommitsAhead: 0,
                        RepoRoot: repoRoot,
                        ExecutablePath: executablePath,
                        Message: "Working tree has uncommitted changes. Commit or stash before updating.");
                }

                return new UpdateCheckResult(
                    Status: UpdateStatus.UpdateAvailable,
                    CurrentSha: currentSha,
                    RemoteSha: remoteSha,
                    Branch: currentBranch,
                    ConfiguredBranch: configuredBranch,
                    CommitsBehind: behind,
                    CommitsAhead: 0,
                    RepoRoot: repoRoot,
                    ExecutablePath: executablePath,
                    Message: $"{behind} new commit(s) on {remoteName}/{configuredBranch}.");
            }

            return new UpdateCheckResult(
                Status: UpdateStatus.UpToDate,
                CurrentSha: currentSha,
                RemoteSha: remoteSha,
                Branch: currentBranch,
                ConfiguredBranch: configuredBranch,
                CommitsBehind: 0,
                CommitsAhead: 0,
                RepoRoot: repoRoot,
                ExecutablePath: executablePath,
                Message: "Up to date.");
        }

        public async Task<UpdateLaunchResult> LaunchUpdateAsync(
            UpdateCheckResult lastCheck,
            CancellationToken ct,
            IProgress<int>? progress = null)
        {
            if (lastCheck == null)
            {
                return new UpdateLaunchResult(false, string.Empty, "No previous check result is available.");
            }

            if (string.IsNullOrWhiteSpace(lastCheck.RepoRoot) || !Directory.Exists(lastCheck.RepoRoot))
            {
                return new UpdateLaunchResult(false, string.Empty,
                    "Repo root is no longer accessible. Re-check before retrying.");
            }

            if (lastCheck.Status != UpdateStatus.UpdateAvailable)
            {
                return new UpdateLaunchResult(false, string.Empty,
                    "Update is not available (status " + lastCheck.Status + ").");
            }

            // Defensive re-check: the working tree could have changed since
            // the check that populated this result. The script ALSO re-runs
            // these guards so a slow user can't sneak a dirty edit past us.
            if (await IsDirtyAsync(lastCheck.RepoRoot, ct).ConfigureAwait(false))
            {
                return new UpdateLaunchResult(false, string.Empty,
                    "Working tree became dirty since the last check. Commit or stash first.");
            }

            var configuredBranch = string.IsNullOrWhiteSpace(lastCheck.ConfiguredBranch)
                ? "main"
                : lastCheck.ConfiguredBranch;
            string remoteName;
            int ahead;
            int behind;
            try
            {
                var currentBranch = await RunGitTrimmedAsync(
                    new[] { "symbolic-ref", "--quiet", "--short", "HEAD" },
                    lastCheck.RepoRoot,
                    GitFastTimeout,
                    ct).ConfigureAwait(false);
                if (!string.Equals(
                    currentBranch,
                    configuredBranch,
                    StringComparison.Ordinal))
                {
                    return new UpdateLaunchResult(
                        false,
                        string.Empty,
                        $"Current branch changed to '{currentBranch}'. Re-check updates first.");
                }

                remoteName = await ResolveRemoteNameAsync(
                    lastCheck.RepoRoot,
                    configuredBranch,
                    ct).ConfigureAwait(false);
                ahead = await CountCommitsAsync(
                    lastCheck.RepoRoot,
                    "@{upstream}..HEAD",
                    ct).ConfigureAwait(false);
                behind = await CountCommitsAsync(
                    lastCheck.RepoRoot,
                    "HEAD..@{upstream}",
                    ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogWarn("Update re-check failed: " + ex.Message);
                return new UpdateLaunchResult(
                    false,
                    string.Empty,
                    "Could not safely re-check the branch and upstream state. Re-check updates first.");
            }

            if (ahead > 0)
            {
                return new UpdateLaunchResult(false, string.Empty,
                    "Local branch is now ahead of origin. Push before updating.");
            }

            if (behind == 0)
            {
                return new UpdateLaunchResult(false, string.Empty,
                    "Repository is already up to date. Nothing to pull.");
            }

            var solutionPath = Path.Combine(lastCheck.RepoRoot, SolutionFileName);
            var exePath = ResolveExecutablePath(lastCheck);
            if (string.IsNullOrWhiteSpace(exePath))
            {
                return new UpdateLaunchResult(false, string.Empty,
                    "Could not determine the installed executable path. Re-check before retrying.");
            }
            var installDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            {
                return new UpdateLaunchResult(false, string.Empty,
                    "Install folder is not accessible: " + installDir + ". Re-install from Install-DeveloperControlTower.ps1.");
            }
            var logFolder = SafeLogFolder();

            string scriptPath;
            try
            {
                scriptPath = WriteUpdateScript(
                    repoRoot: lastCheck.RepoRoot,
                    solutionPath: solutionPath,
                    exePath: exePath,
                    branch: configuredBranch,
                    remoteName: remoteName,
                    currentShaShort: ShortSha(ValidateSha(lastCheck.CurrentSha, "currentSha")),
                    targetShaShort: ShortSha(ValidateSha(lastCheck.RemoteSha, "targetSha")),
                    pid: _currentProcessIdProvider(),
                    logFolder: logFolder);
            }
            catch (Exception ex)
            {
                LogError("Failed to write update script: " + ex.Message, ex);
                return new UpdateLaunchResult(false, string.Empty,
                    "Could not write the update script: " + ex.Message);
            }

            // Decide elevation by probing the install directory rather than
            // assuming it lives under Program Files. A per-user install (e.g.
            // under %LOCALAPPDATA%) is writable in-context, so elevating would
            // only switch to the admin account — which lacks this user's
            // cached Git credentials and PATH and would break fetch/pull.
            var installWritable = _installDirWritableProbe(installDir);

            // Handoff marker: ensures the same app-log file the relaunched
            // app will open already shows that an update is in flight, so
            // the script's appended block reads contiguously.
            LogInfo(
                $"Launching {(installWritable ? "non-elevated" : "elevated")} update script for install dir '{installDir}'. " +
                $"Script output will be appended to the daily app log in: {logFolder}");

            try
            {
                // When the install dir is not writable by the current user
                // (typically C:\Program Files\Development Tower) we elevate so
                // the robocopy step can write; otherwise we run in-context.
                int pid;
                if (installWritable)
                {
                    pid = _shellLauncher.LaunchUpdateConsole(scriptPath);
                    if (pid == 0)
                    {
                        return new UpdateLaunchResult(false, scriptPath,
                            "Update was not started. The update console failed to launch.");
                    }
                }
                else
                {
                    pid = _shellLauncher.LaunchUpdateConsoleElevated(scriptPath);
                    if (pid == 0)
                    {
                        return new UpdateLaunchResult(false, scriptPath,
                            "Update was not started. The UAC prompt was declined, or the elevated console failed to launch.");
                    }
                }

                LogInfo($"Spawned update console PID {pid} for script {scriptPath} (elevated={!installWritable}).");
                return new UpdateLaunchResult(true, scriptPath,
                    "Update console launched. The app will now close.");
            }
            catch (Exception ex)
            {
                LogError("LaunchUpdateConsole threw: " + ex.Message, ex);
                return new UpdateLaunchResult(false, scriptPath,
                    "Could not start the update console: " + ex.Message);
            }
        }

        /// <summary>
        /// Resolves the repo root either from an explicit override or by
        /// walking up from the running executable. Returns false (with a
        /// populated status / message) on any failure mode.
        /// </summary>
        internal bool TryResolveRepoRoot(
            UpdateOptions options,
            out string repoRoot,
            out UpdateStatus status,
            out string message)
        {
            repoRoot = string.Empty;
            status = UpdateStatus.Unknown;
            message = string.Empty;

            if (options != null && !string.IsNullOrWhiteSpace(options.RepoRootOverride))
            {
                var overridePath = options.RepoRootOverride.Trim();
                var dotGitDir = Path.Combine(overridePath, ".git");
                var hasDotGit = Directory.Exists(overridePath) &&
                    (Directory.Exists(dotGitDir) || File.Exists(dotGitDir));
                if (!hasDotGit)
                {
                    status = UpdateStatus.RepoNotFound;
                    message = "Configured repo root override does not contain a .git folder: " + overridePath;
                    return false;
                }

                if (!File.Exists(Path.Combine(overridePath, SolutionFileName)))
                {
                    status = UpdateStatus.InvalidRepoRoot;
                    message = "Configured repo root override does not contain " + SolutionFileName + ".";
                    repoRoot = overridePath;
                    return false;
                }

                repoRoot = overridePath;
                status = UpdateStatus.Unknown;
                return true;
            }

            var start = SafeExecutablePath();
            var seed = string.IsNullOrWhiteSpace(start)
                ? AppContext.BaseDirectory
                : (Path.GetDirectoryName(start) ?? AppContext.BaseDirectory);

            // Sentinel-file path: when the publish/install script copies the
            // app into a non-repo location (e.g. C:\Program Files\Development
            // Tower) it also drops update-repo-root.txt next to the .exe
            // pointing back at the source clone. This is the canonical way
            // to tell the updater where the source repo lives for any
            // installed deployment — the walk-up-from-exe heuristic was
            // removed because (a) it only worked for run-from-source builds
            // we no longer support and (b) it silently picked up unrelated
            // .git folders if the .exe was copied into someone else's repo.
            if (TryReadSentinelRepoRoot(seed, out var sentinelRoot, out var sentinelMessage))
            {
                var sentinelDotGit = Path.Combine(sentinelRoot, ".git");
                var sentinelHasGit = Directory.Exists(sentinelRoot) &&
                    (Directory.Exists(sentinelDotGit) || File.Exists(sentinelDotGit));
                if (sentinelHasGit && File.Exists(Path.Combine(sentinelRoot, SolutionFileName)))
                {
                    repoRoot = sentinelRoot;
                    status = UpdateStatus.Unknown;
                    return true;
                }

                status = UpdateStatus.InvalidRepoRoot;
                message = "Sentinel file '" + RepoRootSentinelFileName +
                          "' next to the executable points at '" + sentinelRoot +
                          "', but that folder is not a valid clone of this app (missing .git or " +
                          SolutionFileName + ").";
                repoRoot = sentinelRoot;
                return false;
            }

            status = UpdateStatus.RepoNotFound;
            if (!string.IsNullOrEmpty(sentinelMessage))
            {
                message = sentinelMessage + " Drop a valid '" + RepoRootSentinelFileName +
                          "' next to the executable, or set Settings → Updates → Repo root override.";
            }
            else
            {
                message = "Could not locate the source git clone for this app. " +
                          "The installer drops '" + RepoRootSentinelFileName +
                          "' next to the executable for this; if you are running an unmanaged build, " +
                          "open Settings → Updates → Repo root override and Browse to your clone " +
                          "(must contain " + SolutionFileName + ").";
            }
            return false;
        }

        // Reads the sentinel file (if present) sitting next to the running
        // executable. The file format is intentionally minimal: blank lines
        // and lines starting with '#' are ignored; the first remaining line
        // is taken as the absolute repo-root path. Returns true only when a
        // non-empty path was successfully read. <paramref name="message"/>
        // is set to a diagnostic when the file exists but couldn't be used.
        private static bool TryReadSentinelRepoRoot(string exeDir, out string repoRoot, out string message)
        {
            repoRoot = string.Empty;
            message = string.Empty;
            if (string.IsNullOrWhiteSpace(exeDir))
            {
                return false;
            }

            string sentinelPath;
            try
            {
                sentinelPath = Path.Combine(exeDir, RepoRootSentinelFileName);
            }
            catch
            {
                return false;
            }

            if (!File.Exists(sentinelPath))
            {
                return false;
            }

            try
            {
                foreach (var rawLine in File.ReadAllLines(sentinelPath))
                {
                    if (rawLine == null) continue;
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (line.StartsWith("#", StringComparison.Ordinal)) continue;
                    // Strip surrounding quotes so an entry like
                    // "C:\path with spaces" still works.
                    if (line.Length >= 2 && line[0] == '"' && line[line.Length - 1] == '"')
                    {
                        line = line.Substring(1, line.Length - 2).Trim();
                    }
                    if (line.Length == 0) continue;
                    repoRoot = line;
                    return true;
                }
                message = "Sentinel file '" + sentinelPath + "' did not contain a repo-root path.";
                return false;
            }
            catch (Exception ex)
            {
                message = "Could not read sentinel file '" + sentinelPath + "': " + ex.Message;
                return false;
            }
        }

        private async Task<string> RunGitTrimmedAsync(
            IEnumerable<string> args, string repoRoot, TimeSpan timeout, CancellationToken ct)
        {
            var run = await _gitAdapter.RunAsync(args, repoRoot, timeout, null, ct).ConfigureAwait(false);
            if (run.ExitCode != 0 || run.TimedOut || run.Cancelled)
            {
                throw new InvalidOperationException(
                    "git exited " + run.ExitCode + ": " + (run.Stderr ?? string.Empty));
            }
            return (run.Stdout ?? string.Empty).Trim();
        }

        private async Task<bool> IsDirtyAsync(string repoRoot, CancellationToken ct)
        {
            var run = await _gitAdapter.RunAsync(
                new[] { "status", "--porcelain" }, repoRoot, GitFastTimeout, null, ct).ConfigureAwait(false);
            if (run.ExitCode != 0)
            {
                // Treat git errors as dirty: better to over-block than to apply
                // an update over a tree we couldn't validate.
                return true;
            }
            return !string.IsNullOrWhiteSpace(run.Stdout);
        }

        private async Task<int> CountCommitsAsync(string repoRoot, string range, CancellationToken ct)
        {
            var run = await _gitAdapter.RunAsync(
                new[] { "rev-list", range, "--count" }, repoRoot, GitFastTimeout, null, ct).ConfigureAwait(false);
            if (run.ExitCode != 0 || run.TimedOut || run.Cancelled)
            {
                throw new InvalidOperationException(
                    "Could not count commits for range '" + range + "'.");
            }
            if (!int.TryParse((run.Stdout ?? string.Empty).Trim(), out var count)
                || count < 0)
            {
                throw new InvalidOperationException(
                    "Git returned an invalid commit count for range '" + range + "'.");
            }
            return count;
        }

        private async Task<string> ResolveRemoteNameAsync(
            string repoRoot,
            string expectedBranch,
            CancellationToken ct)
        {
            var run = await _gitAdapter.RunAsync(
                new[] { "rev-parse", "--symbolic-full-name", "@{upstream}" },
                repoRoot, GitFastTimeout, null, ct).ConfigureAwait(false);
            if (run.ExitCode != 0
                || run.TimedOut
                || run.Cancelled
                || !TryParseRemoteName(
                    run.Stdout,
                    expectedBranch,
                    out var remoteName))
            {
                throw new InvalidOperationException(
                    "The current branch does not have the expected remote upstream.");
            }
            return remoteName;
        }

        internal string WriteUpdateScript(
            string repoRoot,
            string solutionPath,
            string exePath,
            string branch,
            string remoteName,
            string currentShaShort,
            string targetShaShort,
            int pid,
            string logFolder)
        {
            repoRoot = ValidateCommandPath(repoRoot, nameof(repoRoot));
            solutionPath = ValidateCommandPath(solutionPath, nameof(solutionPath));
            exePath = ValidateCommandPath(exePath, nameof(exePath));
            logFolder = ValidateCommandPath(logFolder, nameof(logFolder));
            branch = ValidateBranchName(branch);
            remoteName = ValidateRemoteName(remoteName);
            currentShaShort = ValidateSha(currentShaShort, nameof(currentShaShort));
            targetShaShort = ValidateSha(targetShaShort, nameof(targetShaShort));
            if (pid <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(pid), "Process ID must be positive.");
            }

            if (!string.Equals(
                Path.GetFileName(exePath),
                DesktopExecutableName,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Executable path must end with " + DesktopExecutableName + ".",
                    nameof(exePath));
            }

            var tempRoot = ValidateCommandPath(_tempPathProvider(), "tempPath");
            var scriptFolder = ValidateCommandPath(
                Path.Combine(tempRoot, "controltower-update"),
                "scriptFolder");
            var csprojPath = ValidateCommandPath(
                Path.Combine(repoRoot, DesktopRelativeProjectPath),
                "desktopProjectPath");
            var installDir = Path.GetDirectoryName(exePath);
            if (string.IsNullOrWhiteSpace(installDir))
            {
                throw new InvalidOperationException(
                    "Cannot derive install directory from executable path: " + exePath);
            }
            installDir = ValidateCommandPath(installDir, "installDir");
            var stagingDir = ValidateCommandPath(
                Path.Combine(scriptFolder, "stage-" + Guid.NewGuid().ToString("N").Substring(0, 12)),
                "stagingDir");
            var sentinelPath = ValidateCommandPath(
                Path.Combine(installDir, RepoRootSentinelFileName),
                "sentinelPath");
            var ownershipMarkerPath = ValidateCommandPath(
                Path.Combine(installDir, InstallOwnershipMarkerFileName),
                "ownershipMarkerPath");

            Directory.CreateDirectory(scriptFolder);
            var scriptName = "update-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".cmd";
            var scriptPath = ValidateCommandPath(
                Path.Combine(scriptFolder, scriptName),
                "scriptPath");
            var teeFile = ValidateCommandPath(
                Path.Combine(scriptFolder, "tee-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".txt"),
                "teeFile");

            var sb = new StringBuilder();
            sb.AppendLine("@echo off");
            sb.AppendLine("setlocal EnableExtensions DisableDelayedExpansion");
            sb.AppendLine("title Developer Control Tower - Update");
            sb.AppendLine("chcp 65001 >nul");
            sb.AppendLine();

            // --- Logging setup: append all output to the app's daily log ---
            sb.AppendLine("rem === Logging setup ===");
            sb.AppendLine("set \"LOG_DIR=" + logFolder + "\"");
            sb.AppendLine("if not exist \"%LOG_DIR%\" mkdir \"%LOG_DIR%\" >nul 2>&1");
            sb.AppendLine("set \"TODAY=\"");
            sb.AppendLine("for /f \"usebackq delims=\" %%d in (`powershell -NoProfile -Command \"[DateTime]::UtcNow.ToString('yyyyMMdd')\"`) do set \"TODAY=%%d\"");
            sb.AppendLine("if not defined TODAY set \"TODAY=unknown\"");
            sb.AppendLine("set \"LOG=%LOG_DIR%\\app-%TODAY%.log\"");
            sb.AppendLine("set \"TEEFILE=" + teeFile + "\"");
            sb.AppendLine("set \"STEP=startup\"");
            sb.AppendLine("set \"RC=0\"");
            sb.AppendLine("set \"REPO=" + repoRoot + "\"");
            sb.AppendLine("set \"SOLUTION=" + solutionPath + "\"");
            sb.AppendLine("set \"EXE=" + exePath + "\"");
            sb.AppendLine("set \"CSPROJ=" + csprojPath + "\"");
            sb.AppendLine("set \"INSTALL=" + installDir + "\"");
            sb.AppendLine("set \"STAGE=" + stagingDir + "\"");
            sb.AppendLine("set \"SENTINEL=" + sentinelPath + "\"");
            sb.AppendLine("set \"OWNER=" + ownershipMarkerPath + "\"");
            sb.AppendLine("set \"REMOTE=" + remoteName + "\"");
            sb.AppendLine("set \"BRANCH=" + branch + "\"");
            sb.AppendLine();

            // --- Begin marker (both console and log) ---
            AppendLoggedLine(sb, string.Empty);
            AppendLoggedLine(sb,
                $"========== Update script begin (PID {pid}, branch {branch}, from {currentShaShort} to {targetShaShort}) ==========");
            AppendLoggedLine(sb, "Repo:   " + repoRoot);
            AppendLoggedLine(sb, "Remote: " + remoteName);
            AppendLoggedLine(sb, "Log:    %LOG%");
            AppendLoggedLine(sb, string.Empty);

            // --- Wait for the app process (and any siblings) to exit ---
            sb.AppendLine("echo Waiting for app process (PID " + pid + ") to exit...");
            sb.AppendLine(":waitpid");
            sb.AppendLine("tasklist /fi \"PID eq " + pid + "\" /nh 2>nul | find \"" + pid + "\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("    timeout /t 1 /nobreak >nul");
            sb.AppendLine("    goto waitpid");
            sb.AppendLine(")");
            sb.AppendLine();
            sb.AppendLine("echo Waiting for any other " + DesktopExecutableName + " instances to exit...");
            sb.AppendLine(":waitother");
            sb.AppendLine("tasklist /fi \"imagename eq " + DesktopExecutableName + "\" /nh 2>nul | find /i \"" + DesktopExecutableName + "\" >nul");
            sb.AppendLine("if not errorlevel 1 (");
            sb.AppendLine("    timeout /t 1 /nobreak >nul");
            sb.AppendLine("    goto waitother");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- cd into repo root ---
            sb.AppendLine("set \"STEP=enter repo\"");
            sb.AppendLine("cd /d \"%REPO%\"");
            sb.AppendLine("if errorlevel 1 (");
            AppendLoggedLine(sb, "*** Could not enter %REPO%. Aborting. ***", indent: "    ");
            sb.AppendLine("    set \"RC=2\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- Re-check the selected branch without interpolating Git's
            //     output back into cmd.exe syntax. ---
            sb.AppendLine("set \"STEP=branch check\"");
            AppendTeedCommand(
                sb,
                "git symbolic-ref HEAD",
                "git symbolic-ref --quiet HEAD");
            sb.AppendLine("if not \"%RC%\"==\"0\" (");
            AppendLoggedLine(sb, "*** Could not read the current branch. Aborting. ***", indent: "    ");
            sb.AppendLine("    set \"RC=2\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine("findstr /x /l /c:\"refs/heads/%BRANCH%\" \"%TEEFILE%\" >nul");
            sb.AppendLine("if errorlevel 1 (");
            AppendLoggedLine(sb, "*** Current branch no longer matches %BRANCH%. Re-check updates first. ***", indent: "    ");
            sb.AppendLine("    set \"RC=2\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- Re-check working tree ---
            sb.AppendLine("set \"STEP=working tree check\"");
            AppendLoggedLine(sb, "=== Re-checking working tree state ===");
            sb.AppendLine("git status --porcelain > \"%TEEFILE%\" 2>&1");
            sb.AppendLine("set \"RC=%ERRORLEVEL%\"");
            sb.AppendLine("type \"%TEEFILE%\"");
            sb.AppendLine("type \"%TEEFILE%\" >> \"%LOG%\"");
            sb.AppendLine("if not \"%RC%\"==\"0\" goto fail");
            sb.AppendLine("set \"DIRTYLINES=0\"");
            sb.AppendLine("for /f %%s in ('find /c /v \"\" ^< \"%TEEFILE%\"') do set \"DIRTYLINES=%%s\"");
            sb.AppendLine("if not \"%DIRTYLINES%\"==\"0\" (");
            AppendLoggedLine(sb, "*** Working tree is not clean. Commit or stash local changes, then rerun update. ***", indent: "    ");
            sb.AppendLine("    set \"RC=3\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- git fetch ---
            sb.AppendLine("set \"STEP=git fetch\"");
            AppendTeedCommand(
                sb,
                "git fetch " + remoteName + " " + branch,
                "git fetch \"%REMOTE%\" \"%BRANCH%\"");
            sb.AppendLine("if not \"%RC%\"==\"0\" (");
            AppendLoggedLine(sb, "*** Fetch failed. Check network / credentials. ***", indent: "    ");
            sb.AppendLine("    set \"RC=4\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            // FETCH_HEAD is the exact commit selected by the validated
            // remote/branch fetch above. Do not consult the mutable upstream
            // configuration again after the app has exited.
            sb.AppendLine("set \"STEP=fast-forward check\"");
            AppendTeedCommand(
                sb,
                "git merge-base --is-ancestor HEAD FETCH_HEAD",
                "git merge-base --is-ancestor HEAD FETCH_HEAD");
            sb.AppendLine("if not \"%RC%\"==\"0\" (");
            AppendLoggedLine(sb, "*** The fetched branch is no longer a fast-forward from local HEAD. Resolve in git first. ***", indent: "    ");
            sb.AppendLine("    set \"RC=5\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            sb.AppendLine("set \"STEP=behind count\"");
            AppendTeedCommand(
                sb,
                "git rev-list HEAD..FETCH_HEAD --count",
                "git rev-list HEAD..FETCH_HEAD --count");
            sb.AppendLine("if not \"%RC%\"==\"0\" (");
            AppendLoggedLine(sb, "*** Could not compare local HEAD with the fetched branch. Aborting. ***", indent: "    ");
            sb.AppendLine("    set \"RC=5\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine("set \"BEHIND=\"");
            sb.AppendLine("for /f \"usebackq delims=\" %%c in (\"%TEEFILE%\") do if not defined BEHIND set \"BEHIND=%%c\"");
            sb.AppendLine("if not defined BEHIND (");
            AppendLoggedLine(sb, "*** Git did not return a behind count. Aborting. ***", indent: "    ");
            sb.AppendLine("    set \"RC=5\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            AppendLoggedLine(sb, "behind=%BEHIND%");
            sb.AppendLine("if \"%BEHIND%\"==\"0\" (");
            AppendLoggedLine(sb, "*** Already up to date - nothing to merge. Relaunching. ***", indent: "    ");
            sb.AppendLine("    set \"RC=0\"");
            sb.AppendLine("    goto success");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- fast-forward to the exact commit fetched above ---
            sb.AppendLine("set \"STEP=git fast-forward\"");
            AppendTeedCommand(sb, "git merge --ff-only FETCH_HEAD", "git merge --ff-only FETCH_HEAD");
            sb.AppendLine("if not \"%RC%\"==\"0\" (");
            AppendLoggedLine(sb, "*** Fast-forward merge failed. Branch may have changed locally. ***", indent: "    ");
            sb.AppendLine("    git status -sb");
            sb.AppendLine("    git status -sb >> \"%LOG%\" 2>&1");
            sb.AppendLine("    set \"RC=6\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- dotnet publish (Release) into staging ---
            sb.AppendLine("set \"STEP=dotnet publish\"");
            sb.AppendLine("if exist \"%STAGE%\" rmdir /s /q \"%STAGE%\" >nul 2>&1");
            sb.AppendLine("mkdir \"%STAGE%\" >nul 2>&1");
            AppendTeedCommand(sb,
                "dotnet publish " + csprojPath + " -c Release -f " + DesktopTargetFramework + " --no-self-contained -o " + stagingDir,
                "dotnet publish \"%CSPROJ%\" -c Release -f " + DesktopTargetFramework +
                    " --no-self-contained --nologo -o \"%STAGE%\"");
            sb.AppendLine("if not \"%RC%\"==\"0\" (");
            AppendLoggedLine(sb, "*** Publish failed. Source was updated but the OLD build is still installed. ***", indent: "    ");
            AppendLoggedLine(sb, "*** Fix and rerun the update from the app, or run Install-DeveloperControlTower.ps1. ***", indent: "    ");
            sb.AppendLine("    set \"RC=7\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- Verify install ownership before the destructive mirror ---
            sb.AppendLine("set \"STEP=verify install ownership\"");
            sb.AppendLine(
                "powershell -NoProfile -Command " +
                "\"$item=[IO.DirectoryInfo]::new($env:INSTALL); while ($item) " +
                "{ if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) " +
                "{ exit 1 }; $item=$item.Parent }\" >nul 2>&1");
            sb.AppendLine("if errorlevel 1 goto invalid_install_ownership");
            sb.AppendLine("if not exist \"%OWNER%\" goto verify_legacy_install");
            sb.AppendLine(
                "powershell -NoProfile -Command " +
                "\"$v=[IO.File]::ReadAllText($env:OWNER).TrimEnd([char]13,[char]10); " +
                "if ($v -cne '" + InstallOwnershipMarkerContents + "') { exit 1 }\" >nul 2>&1");
            sb.AppendLine("if errorlevel 1 goto invalid_install_ownership");
            sb.AppendLine("goto install_ownership_ok");
            sb.AppendLine(":verify_legacy_install");
            sb.AppendLine("if not exist \"%INSTALL%\\" + DesktopExecutableName + "\" goto invalid_install_ownership");
            sb.AppendLine("if not exist \"%SENTINEL%\" goto invalid_install_ownership");
            sb.AppendLine(
                "powershell -NoProfile -Command " +
                "\"$line=$null; foreach($raw in [IO.File]::ReadAllLines($env:SENTINEL)) " +
                "{ $candidate=$raw.Trim(); if ($candidate -and -not $candidate.StartsWith('#')) " +
                "{ $line=$candidate; break } }; if (-not $line) { exit 1 }; " +
                "if ($line.Length -ge 2 -and $line[0] -eq [char]34 -and " +
                "$line[$line.Length-1] -eq [char]34) " +
                "{ $line=$line.Substring(1,$line.Length-2).Trim() }; " +
                "$expected=[IO.Path]::GetFullPath($env:REPO).TrimEnd([char]92,[char]47); " +
                "$actual=[IO.Path]::GetFullPath($line).TrimEnd([char]92,[char]47); " +
                "if (-not [string]::Equals($actual,$expected,[StringComparison]::OrdinalIgnoreCase)) " +
                "{ exit 1 }\" >nul 2>&1");
            sb.AppendLine("if errorlevel 1 goto invalid_install_ownership");
            sb.AppendLine("goto install_ownership_ok");
            sb.AppendLine(":invalid_install_ownership");
            AppendLoggedLine(
                sb,
                "*** Refusing to update an unowned or malformed install directory: %INSTALL% ***");
            AppendLoggedLine(
                sb,
                "*** Reinstall into a dedicated Developer Control Tower directory. ***");
            sb.AppendLine("set \"RC=9\"");
            sb.AppendLine("goto fail");
            sb.AppendLine(":install_ownership_ok");
            sb.AppendLine();

            // --- robocopy staging -> install dir ---
            // /MIR mirrors the publish output (preventing stale assemblies)
            // but /XF excludes the sentinel and ownership marker. /XD keeps
            // the legacy app-side library until the new build has migrated
            // it into the writable user configuration root.
            // /XJ avoids following junctions. Robocopy exit codes 0-7 are
            // success (0=nothing copied, 1+ = files copied / extra); 8+
            // are real failures.
            sb.AppendLine("set \"STEP=robocopy install\"");
            AppendTeedCommand(sb,
                "robocopy " + stagingDir + " -> " + installDir,
                "robocopy \"%STAGE%\" \"%INSTALL%\" /MIR /XJ /XD library /XF " +
                    RepoRootSentinelFileName + " " + InstallOwnershipMarkerFileName +
                    " /R:2 /W:2 /NFL /NDL /NP");
            sb.AppendLine("if %RC% GEQ 8 (");
            AppendLoggedLine(sb, "*** Robocopy failed (exit %RC%). The install at %INSTALL% may be partially updated. ***", indent: "    ");
            AppendLoggedLine(sb, "*** Re-run elevated or run Install-DeveloperControlTower.ps1. ***", indent: "    ");
            sb.AppendLine("    set \"RC=8\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            // Robocopy "I did real work" codes (1..7) are NOT failures.
            sb.AppendLine("set \"RC=0\"");
            sb.AppendLine();

            // --- Refresh the sentinel so the install always knows where
            //     the source clone lives (in case a fresh install dir was
            //     created above without one). ---
            sb.AppendLine("set \"STEP=write sentinel\"");
            sb.AppendLine(">\"%SENTINEL%\" echo # Developer Control Tower update sentinel");
            sb.AppendLine(">>\"%SENTINEL%\" echo # Written by update script. Edit only if you intentionally moved the source clone.");
            sb.AppendLine(">>\"%SENTINEL%\" echo %REPO%");
            sb.AppendLine("if errorlevel 1 (");
            AppendLoggedLine(sb, "*** Could not refresh the update sentinel. ***", indent: "    ");
            sb.AppendLine("    set \"RC=10\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- Refresh ownership only after the mirror and sentinel write
            //     have both succeeded. ---
            sb.AppendLine("set \"STEP=write install ownership\"");
            sb.AppendLine(">\"%OWNER%\" echo " + InstallOwnershipMarkerContents);
            sb.AppendLine("if errorlevel 1 (");
            AppendLoggedLine(sb, "*** Could not refresh the install ownership marker. ***", indent: "    ");
            sb.AppendLine("    set \"RC=11\"");
            sb.AppendLine("    goto fail");
            sb.AppendLine(")");
            sb.AppendLine();

            // --- Tidy source tree: drop bin/obj from this clone so the
            //     repo stays small and free of build artifacts. Failures
            //     here are non-fatal. ---
            sb.AppendLine("set \"STEP=dotnet clean\"");
            AppendTeedCommand(sb,
                "dotnet clean " + solutionPath + " -c Release",
                "dotnet clean \"%SOLUTION%\" -c Release --nologo");
            sb.AppendLine("rem clean is best-effort; do not goto fail on RC");
            sb.AppendLine("set \"RC=0\"");
            sb.AppendLine();

            // --- Remove the staging folder we created above ---
            sb.AppendLine("if exist \"%STAGE%\" rmdir /s /q \"%STAGE%\" >nul 2>&1");
            sb.AppendLine();

            // --- Success path ---
            sb.AppendLine(":success");
            AppendLoggedLine(sb, string.Empty);
            AppendLoggedLine(sb,
                "========== Update script end: SUCCESS (relaunching " + exePath + ") ==========");
            AppendLoggedLine(sb, string.Empty);
            sb.AppendLine("if exist \"%TEEFILE%\" del \"%TEEFILE%\" >nul 2>&1");
            sb.AppendLine("timeout /t 1 /nobreak >nul");
            sb.AppendLine("start \"\" \"%EXE%\"");
            sb.AppendLine("endlocal & exit /b 0");
            sb.AppendLine();

            // --- Failure tail (single label; %STEP% and %RC% are set by caller) ---
            sb.AppendLine(":fail");
            AppendLoggedLine(sb, string.Empty);
            AppendLoggedLine(sb,
                "========== Update script end: FAILURE (step=%STEP% exit=%RC%) ==========");
            AppendLoggedLine(sb, string.Empty);
            sb.AppendLine("if exist \"%TEEFILE%\" del \"%TEEFILE%\" >nul 2>&1");
            sb.AppendLine("pause");
            sb.AppendLine("endlocal & exit /b %RC%");

            using (var stream = new FileStream(scriptPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.Write(sb.ToString());
            }

            return scriptPath;
        }

        // Emits an echo that appears on the console AND is appended to the
        // daily app log so a single file shows the whole story.
        //
        // When emitted inside a parenthesised cmd block (indent != ""), any
        // raw '(' or ')' in the message body is escaped to '^(' / '^)' so
        // cmd.exe's paren matcher does not mis-detect the end of the
        // surrounding IF/FOR block. Without this, a line like
        //   echo *** ahead by %N% commit(s). ***
        // inside a skipped IF block aborts the whole script with
        //   ". was unexpected at this time."
        // and the console closes before any failure marker is written.
        // This guard also protects future error messages that interpolate
        // a path containing "(x86)" or similar.
        private static void AppendLoggedLine(StringBuilder sb, string message, string indent = "")
        {
            if (string.IsNullOrEmpty(message))
            {
                sb.AppendLine(indent + "echo.");
                sb.AppendLine(indent + ">>\"%LOG%\" echo.");
                return;
            }
            var safe = string.IsNullOrEmpty(indent)
                ? message
                : message.Replace("(", "^(").Replace(")", "^)");
            sb.AppendLine(indent + "echo " + safe);
            sb.AppendLine(indent + ">>\"%LOG%\" echo " + safe);
        }

        // Runs a command, capturing stdout+stderr to a temp file so we can
        // both display it to the user and append it to the app log. The
        // temp-file approach preserves the command's ERRORLEVEL (a wrapping
        // `for /f` loop would swallow it).
        private static void AppendTeedCommand(StringBuilder sb, string label, string commandLine)
        {
            AppendLoggedLine(sb, "=== " + label + " ===");
            sb.AppendLine(commandLine + " > \"%TEEFILE%\" 2>&1");
            sb.AppendLine("set \"RC=%ERRORLEVEL%\"");
            sb.AppendLine("type \"%TEEFILE%\"");
            sb.AppendLine("type \"%TEEFILE%\" >> \"%LOG%\"");
            AppendLoggedLine(sb, "=== " + label + " exit=%RC% ===");
        }

        private static string ValidateCommandPath(string value, string parameterName)
        {
            ValidateCommandValue(value, parameterName);

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(value);
            }
            catch (Exception ex) when (
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException)
            {
                throw new ArgumentException(
                    parameterName + " is not a valid absolute Windows path.",
                    parameterName,
                    ex);
            }

            if (!Path.IsPathFullyQualified(fullPath))
            {
                throw new ArgumentException(
                    parameterName + " must be an absolute Windows path.",
                    parameterName);
            }

            ValidateCommandValue(fullPath, parameterName);
            return fullPath;
        }

        private static void ValidateCommandValue(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new ArgumentException(
                    parameterName + " must not be empty.",
                    parameterName);
            }

            if (value.IndexOfAny(UnsafeCmdCharacters) >= 0)
            {
                throw new ArgumentException(
                    parameterName + " contains characters that are unsafe for the Windows update command.",
                    parameterName);
            }
        }

        private static string ValidateBranchName(string branch)
        {
            ValidateCommandValue(branch, nameof(branch));
            if (branch.Length > 255 ||
                !BranchNamePattern.IsMatch(branch) ||
                branch.Contains("..", StringComparison.Ordinal) ||
                branch.Contains("//", StringComparison.Ordinal) ||
                branch.Contains("@{", StringComparison.Ordinal) ||
                branch.EndsWith("/", StringComparison.Ordinal) ||
                branch.EndsWith(".", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "branch is not a supported Git branch name.",
                    nameof(branch));
            }

            foreach (var component in branch.Split('/'))
            {
                if (component.StartsWith(".", StringComparison.Ordinal) ||
                    component.EndsWith(".lock", StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException(
                        "branch is not a supported Git branch name.",
                        nameof(branch));
                }
            }

            return branch;
        }

        private static string ValidateRemoteName(string remoteName)
        {
            ValidateCommandValue(remoteName, nameof(remoteName));
            if (remoteName.Length > 255 || !RemoteNamePattern.IsMatch(remoteName))
            {
                throw new ArgumentException(
                    "remoteName is not a supported Git remote name.",
                    nameof(remoteName));
            }

            return remoteName;
        }

        private static string ValidateSha(string sha, string parameterName)
        {
            ValidateCommandValue(sha, parameterName);
            if (!ShaPattern.IsMatch(sha))
            {
                throw new ArgumentException(
                    parameterName + " must be a hexadecimal Git object ID.",
                    parameterName);
            }

            return sha;
        }

        private string SafeLogFolder()
        {
            try
            {
                var folder = _logFolderProvider();
                return string.IsNullOrWhiteSpace(folder) ? AppLogger.LogFolder : folder;
            }
            catch
            {
                return AppLogger.LogFolder;
            }
        }

        private static UpdateCheckResult Failure(
            UpdateStatus status, string repoRoot, string configuredBranch, string executablePath, string message)
        {
            return new UpdateCheckResult(
                Status: status,
                CurrentSha: string.Empty,
                RemoteSha: string.Empty,
                Branch: string.Empty,
                ConfiguredBranch: configuredBranch,
                CommitsBehind: 0,
                CommitsAhead: 0,
                RepoRoot: repoRoot ?? string.Empty,
                ExecutablePath: executablePath,
                Message: message);
        }

        private static bool TryParseRemoteName(
            string upstreamRef,
            string expectedBranch,
            out string remoteName)
        {
            remoteName = string.Empty;
            var prefix = "refs/remotes/";
            var trimmed = upstreamRef.Trim();
            var branchSuffix = "/" + expectedBranch;
            if (!trimmed.StartsWith(prefix, StringComparison.Ordinal)
                || !trimmed.EndsWith(branchSuffix, StringComparison.Ordinal))
            {
                return false;
            }

            var candidate = trimmed.Substring(
                prefix.Length,
                trimmed.Length - prefix.Length - branchSuffix.Length);
            if (candidate.Length == 0
                || candidate.Length > 255
                || candidate.IndexOfAny(UnsafeCmdCharacters) >= 0
                || !RemoteNamePattern.IsMatch(candidate))
            {
                return false;
            }

            remoteName = candidate;
            return true;
        }

        private static bool TryParseAheadBehind(
            string output,
            out int ahead,
            out int behind)
        {
            ahead = 0;
            behind = 0;
            if (string.IsNullOrWhiteSpace(output)) return false;

            var parts = output.Trim().Split(new[] { '\t', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 2
                && int.TryParse(parts[0], out ahead)
                && ahead >= 0
                && int.TryParse(parts[1], out behind)
                && behind >= 0;
        }

        private string SafeExecutablePath()
        {
            try
            {
                return _executablePathProvider() ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        // Probes whether the current user can write into the install dir by
        // creating (and auto-deleting) a temporary file there. Returns false
        // on any failure — including access-denied for protected locations
        // like Program Files — which is exactly the signal we use to decide
        // whether the update must be elevated.
        private static bool DefaultInstallDirWritable(string installDir)
        {
            if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir))
            {
                return false;
            }

            try
            {
                var probe = Path.Combine(
                    installDir,
                    ".ct-write-probe-" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".tmp");
                using (new FileStream(
                    probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
                {
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string DefaultExecutablePath()
        {
            try
            {
                var module = Process.GetCurrentProcess().MainModule;
                if (module != null && !string.IsNullOrWhiteSpace(module.FileName))
                {
                    return module.FileName;
                }
            }
            catch
            {
            }
            return AppContext.BaseDirectory;
        }

        private static string ResolveExecutablePath(UpdateCheckResult result)
        {
            // The currently-running .exe IS the canonical install location:
            // the update script publishes a fresh build, robocopies it on
            // top of this path, then relaunches from here. Earlier versions
            // preferred <repoRoot>\src\...\bin\Release\...\exe, which
            // silently moved the user from their real install (e.g. under
            // C:\Program Files\Development Tower) to a run-from-bin world
            // and left the actual installed copy stale. We trust the
            // captured ExecutablePath from the check pipeline.
            return result.ExecutablePath ?? string.Empty;
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha)) return "(unknown)";
            return sha.Length >= 8 ? sha.Substring(0, 8) : sha;
        }
    }
}
