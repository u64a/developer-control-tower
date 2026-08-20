#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.Ssh;

namespace ControlTower.Core.UseCases
{
    /// <summary>
    /// Drives Phase B Relocate. Walks a project from one store to another
    /// by cloning origin into the new destination, migrating
    /// <c>.controltower/</c> metadata, optionally copying ignored files,
    /// rebinding the portfolio entry, and optionally Recycle-Bin-ing the
    /// source. Preflight is read-only and surfaces every blocker so the
    /// user can fix them in one pass.
    /// </summary>
    /// <remarks>
    /// SSH→SSH is intentionally refused. Move via a local store first.
    /// </remarks>
    public sealed class RelocateProjectService : IRelocateProjectService
    {
        private const long MaxIgnoredFileBytes = 100L * 1024 * 1024;
        private const int MaxIgnoredFilesToCopy = 500;

        // Mirror the SafeFolderRegex in ProjectCreationService: starts with
        // alphanumeric, only [A-Za-z0-9._-], capped at 100 chars.
        private static readonly Regex SafeFolderRegex =
            new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);

        private readonly IGitProcessAdapter _gitAdapter;
        private readonly IGitWorkspaceInspector _workspaceInspector;
        private readonly ISshGitInspector _sshGitInspector;
        private readonly IAsyncCloneService _cloneService;
        private readonly ISshService _sshService;
        private readonly ICredentialStore _credentialStore;
        private readonly IRelocateFileTransfer _fileTransfer;
        private readonly IProjectRegistrationService _registrationService;
        private readonly IStoreProvider _storeProvider;
        private readonly ILongRunningGitOperationLock _gitOperationLock;
        private readonly Func<string, string, bool>? _deleteToRecycleBin;

        public RelocateProjectService(
            IGitProcessAdapter gitAdapter,
            IGitWorkspaceInspector workspaceInspector,
            ISshGitInspector sshGitInspector,
            IAsyncCloneService cloneService,
            ISshService sshService,
            ICredentialStore credentialStore,
            IRelocateFileTransfer fileTransfer,
            IProjectRegistrationService registrationService,
            IStoreProvider storeProvider,
            ILongRunningGitOperationLock gitOperationLock,
            Func<string, string, bool>? deleteToRecycleBin = null)
        {
            _gitAdapter = gitAdapter ?? throw new ArgumentNullException(nameof(gitAdapter));
            _workspaceInspector = workspaceInspector ?? throw new ArgumentNullException(nameof(workspaceInspector));
            _sshGitInspector = sshGitInspector ?? throw new ArgumentNullException(nameof(sshGitInspector));
            _cloneService = cloneService ?? throw new ArgumentNullException(nameof(cloneService));
            _sshService = sshService ?? throw new ArgumentNullException(nameof(sshService));
            _credentialStore = credentialStore ?? throw new ArgumentNullException(nameof(credentialStore));
            _fileTransfer = fileTransfer ?? throw new ArgumentNullException(nameof(fileTransfer));
            _registrationService = registrationService ?? throw new ArgumentNullException(nameof(registrationService));
            _storeProvider = storeProvider ?? throw new ArgumentNullException(nameof(storeProvider));
            _gitOperationLock = gitOperationLock ?? throw new ArgumentNullException(nameof(gitOperationLock));
            _deleteToRecycleBin = deleteToRecycleBin;
        }

        public async Task<RelocatePreflightResult> PreflightAsync(RelocateRequest request, CancellationToken ct)
        {
            var result = new RelocatePreflightResult { OkToRelocate = false };
            if (request == null)
            {
                result.Issues.Add("No request supplied.");
                return result;
            }

            // ---- Target store resolution ----
            var targetStore = _storeProvider.GetStore(request.TargetStoreId);
            if (targetStore == null)
            {
                result.Issues.Add("Target store '" + (request.TargetStoreId ?? string.Empty) + "' not found.");
                return result;
            }

            // Folder name validation (defence in depth even before path math).
            string folder = (request.TargetFolder ?? string.Empty).Trim();
            if (!SafeFolderRegex.IsMatch(folder))
            {
                result.Issues.Add(
                    "Folder name must start with a letter or digit and contain only letters, digits, '.', '_' or '-' (max 100 chars).");
                return result;
            }

            bool sourceIsSsh = ComputeSourceIsSsh(request.SourceSshTarget);
            bool targetIsSsh = targetStore.IsSsh;
            result.SourceIsSsh = sourceIsSsh;
            result.TargetIsSsh = targetIsSsh;
            (string host, int port, string user, string password)? targetCreds = null;
            bool targetIsWindows = false;

            // ---- SSH→SSH refusal (step 2) ----
            if (sourceIsSsh && targetIsSsh)
            {
                result.Issues.Add(
                    "SSH→SSH relocation isn't supported. Move to a local store first, then to the new SSH target.");
                return result;
            }

            if (targetIsSsh)
            {
                targetCreds = ResolveStoreCredentials(targetStore);
                if (targetCreds == null)
                {
                    result.Issues.Add("No credentials available for target SSH store.");
                    return result;
                }

                var targetConnection = _sshService.TestConnection(
                    targetCreds.Value.host,
                    targetCreds.Value.port,
                    targetCreds.Value.user,
                    targetCreds.Value.password);
                if (!targetConnection.Success)
                {
                    result.Issues.Add(
                        "Target SSH unreachable: "
                        + (string.IsNullOrEmpty(targetConnection.Code)
                            ? targetConnection.Error
                            : targetConnection.Code + " — " + targetConnection.Error));
                    return result;
                }

                if (!TryDetectRemoteIsWindows(
                    targetCreds.Value,
                    out targetIsWindows,
                    out var targetOsError))
                {
                    result.Issues.Add(targetOsError);
                    return result;
                }
                result.TargetIsWindows = targetIsWindows;
            }

            // ---- Resolve source and destination paths ----
            string sourcePath;
            if (sourceIsSsh)
            {
                if (!TryParseSshTarget(request.SourceSshTarget, out _, out var sshRemotePath))
                {
                    result.Issues.Add("Source SSH target is not in 'user@host:path' form.");
                    return result;
                }
                sourcePath = sshRemotePath;
            }
            else
            {
                sourcePath = (request.SourceLocalPath ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(sourcePath))
                {
                    result.Issues.Add("Source local path is empty.");
                    return result;
                }
            }

            string targetPath = ComputeTargetPath(targetStore, folder, targetIsWindows);
            result.ResolvedSourcePath = sourceIsSsh ? request.SourceSshTarget : sourcePath;
            result.ResolvedTargetPath = targetIsSsh
                ? FormatSshTarget(targetStore, folder, targetIsWindows)
                : targetPath;

            // ---- No-op guard (step 1) ----
            if (PathsLookIdentical(sourcePath, targetPath, sourceIsSsh: sourceIsSsh, targetIsSsh: targetIsSsh,
                request.SourceSshTarget, result.ResolvedTargetPath))
            {
                result.Issues.Add("Source and destination are identical.");
                return result;
            }

            // ---- Source shape (steps 5, 6) ----
            GitWorkspaceClassification sourceClass;
            try
            {
                if (sourceIsSsh)
                {
                    var sshCreds = ResolveSshCredentials(request.SourceStoreId, request.SourceSshTarget);
                    if (sshCreds == null)
                    {
                        result.Issues.Add("No SSH store/credentials match the source target.");
                        return result;
                    }
                    sourceClass = await _sshGitInspector.ClassifyAsync(
                        sshCreds.Value.host, sshCreds.Value.port, sshCreds.Value.user, sshCreds.Value.password,
                        sourcePath, ct).ConfigureAwait(false);
                }
                else
                {
                    sourceClass = await _workspaceInspector.ClassifyAsync(sourcePath, ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                result.Issues.Add("Could not inspect source: " + ex.Message);
                return result;
            }

            switch (sourceClass)
            {
                case NotARepo:
                    result.Issues.Add("Source is not a git repository.");
                    return result;
                case BareRepo:
                    result.Issues.Add("Source is a bare repo. Relocate only supports working-tree repos.");
                    return result;
            }

            var working = (WorkingTreeRepo)sourceClass;

            // Origin URL must be present (step 3).
            var originUrl = (working.OriginUrl ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(originUrl))
            {
                result.Issues.Add("Project has no origin URL. Set origin in the source repo first.");
                return result;
            }

            // Origin must not embed credentials (step 4).
            if (UrlSanitizer.HasCredentials(originUrl))
            {
                result.Issues.Add("Origin URL contains embedded credentials. Strip them before relocating.");
                return result;
            }
            result.OriginUrl = originUrl;

            // Shape rejects.
            if (working.HasSubmodules) result.Issues.Add("Source has submodules — not supported.");
            if (working.HasWorktrees) result.Issues.Add("Source has additional worktrees — not supported.");
            if (working.IsShallow) result.Issues.Add("Source is a shallow clone — not supported.");
            if (working.IsSparse) result.Issues.Add("Source uses sparse-checkout — not supported.");
            if (working.IsPartialClone) result.Issues.Add("Source is a partial clone — not supported.");
            if (working.IsDetached) result.Issues.Add("Source is in detached HEAD — check out a branch first.");

            // ---- Source clean (step 7) ----
            RelocationGitState sourceState;
            try
            {
                if (sourceIsSsh)
                {
                    var sshCreds = ResolveSshCredentials(request.SourceStoreId, request.SourceSshTarget)!.Value;
                    sourceState = await _sshGitInspector.ReadRelocationStateAsync(
                        sshCreds.host, sshCreds.port, sshCreds.user, sshCreds.password,
                        sourcePath, ct).ConfigureAwait(false);
                }
                else
                {
                    sourceState = await _workspaceInspector.ReadRelocationStateAsync(
                        sourcePath,
                        ct).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                result.Issues.Add("Could not establish relocation-safe source state: " + ex.Message);
                return result;
            }

            if (!sourceState.Success)
            {
                result.Issues.Add(sourceState.ErrorMessage);
                return result;
            }

            if (string.IsNullOrWhiteSpace(sourceState.Branch))
            {
                result.Issues.Add("Source must be on a named branch.");
                return result;
            }

            if (!IsFullObjectId(sourceState.HeadSha))
            {
                result.Issues.Add("Source HEAD SHA is missing or is not a full Git object ID.");
                return result;
            }
            if (!IsFullObjectId(sourceState.OriginHeadSha))
            {
                result.Issues.Add("Origin branch HEAD SHA is missing or is not a full Git object ID.");
                return result;
            }

            result.SourceBranch = sourceState.Branch;
            result.SourceHeadSha = sourceState.HeadSha.ToLowerInvariant();
            result.SourceIsWindows = sourceState.RemoteIsWindows ?? false;
            var status = sourceState.Status;

            if (request.DeleteSourceAfterSuccess
                && !IsSafeSourceDeletionPath(
                    sourcePath,
                    sourceIsSsh,
                    result.SourceIsWindows))
            {
                result.Issues.Add(
                    "Source deletion is not allowed for a root, relative, or ambiguous path.");
            }

            int dirty = status.Modified.Count + status.Staged.Count + status.UntrackedNotIgnored.Count;
            if (dirty > 0)
            {
                result.Issues.Add(
                    $"Source has uncommitted changes (M:{status.Modified.Count} S:{status.Staged.Count} U:{status.UntrackedNotIgnored.Count}). " +
                    "Commit them in VS Code, then re-preflight.");
            }

            if (!status.AheadOfOrigin.HasValue || !status.BehindOrigin.HasValue)
            {
                result.Issues.Add("Source ahead/behind counts relative to origin could not be established.");
                return result;
            }

            int ahead = status.AheadOfOrigin.Value;
            int behind = status.BehindOrigin.Value;
            result.AheadOfOrigin = ahead;
            result.BehindOfOrigin = behind;

            if (ahead > 0 && dirty == 0)
            {
                result.NeedsPush = true;
                result.Issues.Add($"Source is {ahead} commit(s) ahead of origin. Push first (or use the Push button).");
            }

            if (behind > 0 && dirty == 0 && ahead == 0)
            {
                result.Warnings.Add($"Source is {behind} commit(s) behind origin. Origin will be the source of truth on the new clone.");
            }

            if (ahead == 0
                && !string.Equals(
                    sourceState.OriginHeadSha,
                    result.SourceHeadSha,
                    StringComparison.OrdinalIgnoreCase))
            {
                result.Issues.Add(
                    $"Source HEAD does not match the current origin/{result.SourceBranch}. Update the source first.");
            }

            // ---- Target reachability (step 8) ----
            if (!targetIsSsh)
            {
                try
                {
                    Directory.CreateDirectory(targetStore.Root);
                }
                catch (Exception ex)
                {
                    result.Issues.Add("Target store root is not creatable: " + ex.Message);
                    return result;
                }
            }

            // ---- Destination collision (step 9) ----
            if (!targetIsSsh)
            {
                if (Directory.Exists(targetPath) && Directory.EnumerateFileSystemEntries(targetPath).Any())
                {
                    result.Issues.Add("Destination already exists and is non-empty. Choose a different folder name.");
                    return result;
                }
            }
            else
            {
                if (!TryRemoteDirectoryNonEmpty(
                    targetCreds!.Value,
                    targetPath,
                    targetIsWindows,
                    out var targetNonEmpty,
                    out var collisionError))
                {
                    result.Issues.Add(collisionError);
                    return result;
                }
                if (targetNonEmpty)
                {
                    result.Issues.Add("Destination already exists and is non-empty on the SSH target. Choose a different folder name.");
                    return result;
                }
            }

            // ---- Remote-clone capability (step 10, SSH target only) ----
            if (targetIsSsh)
            {
                var tgtCreds = targetCreds!.Value;
                var ver = _sshService.RunCommand(tgtCreds.host, tgtCreds.port, tgtCreds.user, tgtCreds.password, "git --version");
                if (!ver.Success || string.IsNullOrWhiteSpace(ver.Output) || ver.Output.IndexOf("git", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    result.Issues.Add("Target host has no usable git on PATH. Install git on the remote first.");
                    return result;
                }

                var branchRef = "refs/heads/" + result.SourceBranch;
                var probe = _sshService.RunCommand(
                    tgtCreds.host, tgtCreds.port, tgtCreds.user, tgtCreds.password,
                    "git ls-remote --heads -- "
                    + ShellQuoteForRemote(originUrl, targetIsWindows)
                    + " "
                    + ShellQuoteForRemote(branchRef, targetIsWindows));
                if (!probe.Success
                    || !TryReadLsRemoteSha(probe.Output, branchRef, out var targetOriginSha)
                    || (ahead == 0
                        && !string.Equals(
                            targetOriginSha,
                            result.SourceHeadSha,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    result.Issues.Add(
                        "Target host cannot prove that cloning the selected origin branch reproduces source HEAD.");
                    return result;
                }
            }

            // ---- LFS warning (step 11) ----
            if (!sourceIsSsh && File.Exists(Path.Combine(sourcePath, ".gitattributes")))
            {
                try
                {
                    var attrs = File.ReadAllText(Path.Combine(sourcePath, ".gitattributes"));
                    if (attrs.IndexOf("filter=lfs", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        result.Warnings.Add("Repo uses Git LFS. The relocated clone may not pull LFS objects automatically.");
                    }
                }
                catch { /* best-effort */ }
            }

            // ---- Long-path warning (step 12) ----
            if (targetPath.Length > 240)
            {
                result.Warnings.Add($"Resolved target path is long ({targetPath.Length} chars); Windows long-path support may be required.");
            }

            // ---- Ignored inventory (step 13) ----
            result.IgnoredFilesCount = status.IgnoredFiles.Count;
            var transferableIgnored = GetTransferableIgnoredFiles(status.IgnoredFiles);
            if (!sourceIsSsh)
            {
                long bytes = 0;
                foreach (var rel in transferableIgnored.Take(MaxIgnoredFilesToCopy))
                {
                    try
                    {
                        var full = Path.Combine(sourcePath, rel);
                        if (File.Exists(full))
                        {
                            bytes += new FileInfo(full).Length;
                        }
                    }
                    catch { }
                }
                result.IgnoredFilesBytes = bytes;
            }
            if (!sourceState.IgnoredFilesInventoryComplete)
            {
                result.Warnings.Add(
                    "The ignored-file inventory exceeds the 1,000-entry safety limit and is incomplete. "
                    + "Relocation can continue, but source deletion is not permitted.");
            }
            else if (request.CopyIgnoredFiles
                && transferableIgnored.Count > MaxIgnoredFilesToCopy)
            {
                result.Warnings.Add(
                    $"Source has {transferableIgnored.Count} ignored files, exceeding the "
                    + $"{MaxIgnoredFilesToCopy}-file copy safety limit. They will not be copied "
                    + "and the source must be retained.");
            }

            result.OkToRelocate = result.Issues.Count == 0;
            return result;
        }

        public async Task<RelocateResult> RelocateAsync(
            RelocateRequest request,
            IProgress<RelocateStepUpdate>? progress,
            CancellationToken ct)
        {
            var result = new RelocateResult();
            if (request == null)
            {
                result.ErrorMessage = "No request supplied.";
                result.FailedStep = RelocateStep.Preflight;
                return result;
            }

            using var lockHandle = await _gitOperationLock.AcquireAsync(ct).ConfigureAwait(false);

            // Re-run preflight to make sure the world hasn't changed while
            // the user was reading the dialog.
            progress?.Report(new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Running, "Re-checking preflight…", null));
            var pre = await PreflightAsync(request, ct).ConfigureAwait(false);
            if (!pre.OkToRelocate)
            {
                var combined = string.Join("; ", pre.Issues);
                progress?.Report(new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Failed, combined, null));
                result.FailedStep = RelocateStep.Preflight;
                result.ErrorMessage = combined;
                return result;
            }
            progress?.Report(new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Done, "Preflight passed.", null));

            var targetStore = _storeProvider.GetStore(request.TargetStoreId)!;
            bool targetIsSsh = targetStore.IsSsh;
            bool sourceIsSsh = ComputeSourceIsSsh(request.SourceSshTarget);

            string folder = request.TargetFolder.Trim();
            string targetPath = ComputeTargetPath(targetStore, folder, pre.TargetIsWindows);
            string sourcePath;
            if (sourceIsSsh)
            {
                TryParseSshTarget(request.SourceSshTarget, out _, out sourcePath);
            }
            else
            {
                sourcePath = request.SourceLocalPath.Trim();
            }

            (string host, int port, string user, string password)? tgtCreds = null;
            (string host, int port, string user, string password)? srcCreds = null;
            if (targetIsSsh) tgtCreds = ResolveStoreCredentials(targetStore);
            if (sourceIsSsh) srcCreds = ResolveSshCredentials(request.SourceStoreId, request.SourceSshTarget);

            // ---- Step 1: Create destination folder ----
            progress?.Report(new RelocateStepUpdate(RelocateStep.CreateDestFolder, RelocateStepState.Running, targetPath, null));
            try
            {
                if (!targetIsSsh)
                {
                    Directory.CreateDirectory(targetPath);
                }
                else
                {
                    var c = tgtCreds!.Value;
                    var quotedTarget = ShellQuoteForRemote(targetPath, pre.TargetIsWindows);
                    var mkdirCommand = pre.TargetIsWindows
                        ? "if not exist " + quotedTarget + " mkdir " + quotedTarget
                        : "mkdir -p " + quotedTarget;
                    var mk = _sshService.RunCommand(
                        c.host, c.port, c.user, c.password, mkdirCommand);
                    if (!mk.Success) throw new InvalidOperationException("Create remote directory failed: " + mk.Error);
                }
                progress?.Report(new RelocateStepUpdate(RelocateStep.CreateDestFolder, RelocateStepState.Done, "Created.", null));
            }
            catch (Exception ex)
            {
                progress?.Report(new RelocateStepUpdate(RelocateStep.CreateDestFolder, RelocateStepState.Failed, ex.Message, null));
                result.FailedStep = RelocateStep.CreateDestFolder;
                result.ErrorMessage = ex.Message;
                return result;
            }

            // ---- Step 2: Clone origin into destination ----
            progress?.Report(new RelocateStepUpdate(RelocateStep.CloneOrigin, RelocateStepState.Running, "Cloning " + pre.OriginUrl, null));
            try
            {
                if (!targetIsSsh)
                {
                    var cloneReq = new CloneRequest(
                        RemoteUrl: pre.OriginUrl,
                        DestinationPath: targetPath,
                        Branch: pre.SourceBranch,
                        SingleBranch: true);
                    var cloneProgress = progress == null
                        ? null
                        : new Progress<CloneProgress>(cp =>
                            progress.Report(new RelocateStepUpdate(
                                RelocateStep.CloneOrigin, RelocateStepState.Running,
                                cp.Stage + ": " + cp.Message, cp.PercentComplete)));
                    var cloneResult = await _cloneService.CloneAsync(cloneReq, cloneProgress, ct).ConfigureAwait(false);
                    if (cloneResult.Status == CloneStatus.Cancelled)
                    {
                        progress?.Report(new RelocateStepUpdate(RelocateStep.CloneOrigin, RelocateStepState.Cancelled,
                            $"Relocate cancelled. Partial clone at {targetPath}. Source untouched.", null));
                        result.FailedStep = RelocateStep.CloneOrigin;
                        result.Cancelled = true;
                        result.ErrorMessage = "Relocate cancelled.";
                        return result;
                    }
                    if (!cloneResult.Success)
                    {
                        throw new InvalidOperationException(cloneResult.Message);
                    }
                }
                else
                {
                    var c = tgtCreds!.Value;
                    string cmd = "git clone --branch "
                        + ShellQuoteForRemote(pre.SourceBranch, pre.TargetIsWindows)
                        + " --single-branch "
                        + "-- "
                        + ShellQuoteForRemote(pre.OriginUrl, pre.TargetIsWindows)
                        + " "
                        + ShellQuoteForRemote(targetPath, pre.TargetIsWindows);
                    var run = _sshService.RunCommand(c.host, c.port, c.user, c.password, cmd);
                    if (!run.Success)
                    {
                        throw new InvalidOperationException("Remote clone failed: " + (string.IsNullOrEmpty(run.Error) ? run.Output : run.Error));
                    }
                }
                progress?.Report(new RelocateStepUpdate(RelocateStep.CloneOrigin, RelocateStepState.Done, "Clone complete.", null));
            }
            catch (OperationCanceledException)
            {
                progress?.Report(new RelocateStepUpdate(RelocateStep.CloneOrigin, RelocateStepState.Cancelled,
                    $"Relocate cancelled. Partial clone at {targetPath}. Source untouched.", null));
                result.FailedStep = RelocateStep.CloneOrigin;
                result.Cancelled = true;
                result.ErrorMessage = "Relocate cancelled.";
                return result;
            }
            catch (Exception ex)
            {
                progress?.Report(new RelocateStepUpdate(RelocateStep.CloneOrigin, RelocateStepState.Failed, ex.Message, null));
                result.FailedStep = RelocateStep.CloneOrigin;
                result.ErrorMessage = ex.Message;
                return result;
            }

            // ---- Step 3: Metadata is centralized ----
            // Project metadata (.controltower) lives in the central OneDrive
            // stub keyed by project id, not inside the repo working tree, so
            // nothing needs to be copied between the source and destination.
            // Step 6 (Rebind portfolio) updates the stub's working-tree pointer
            // to the new location.
            progress?.Report(new RelocateStepUpdate(RelocateStep.MigrateMetadata, RelocateStepState.Done,
                "Metadata is centralized — no repo copy needed.", null));

            // ---- Step 4: Copy ignored files (optional) ----
            bool copyIgnoredWarned = false;
            IReadOnlyList<string>? copiedIgnoredInventory = null;
            if (request.CopyIgnoredFiles)
            {
                progress?.Report(new RelocateStepUpdate(RelocateStep.CopyIgnoredFiles, RelocateStepState.Running,
                    pre.IgnoredFilesCount + " ignored entries", null));
                try
                {
                    // Re-read ignored to avoid races. The inspector keeps a
                    // bounded inventory and reports whether it is complete.
                    var sourceStateForIgnored = await ReadSourceStateAsync(
                        sourceIsSsh,
                        srcCreds,
                        sourcePath,
                        ct).ConfigureAwait(false);
                    if (!TryValidateUnchangedSource(
                        sourceStateForIgnored,
                        pre,
                        out var sourceChangeError))
                    {
                        throw new InvalidOperationException(
                            sourceChangeError);
                    }

                    var ignored = GetTransferableIgnoredFiles(
                        sourceStateForIgnored.Status.IgnoredFiles);

                    var srcEndpoint = sourceIsSsh
                        ? new RelocateEndpoint { IsSsh = true, Host = srcCreds!.Value.host, Port = srcCreds.Value.port, User = srcCreds.Value.user, Password = srcCreds.Value.password, RemotePath = sourcePath }
                        : new RelocateEndpoint { IsSsh = false, LocalPath = sourcePath };
                    var dstEndpoint = targetIsSsh
                        ? new RelocateEndpoint { IsSsh = true, Host = tgtCreds!.Value.host, Port = tgtCreds.Value.port, User = tgtCreds.Value.user, Password = tgtCreds.Value.password, RemotePath = targetPath }
                        : new RelocateEndpoint { IsSsh = false, LocalPath = targetPath };

                    if (!sourceStateForIgnored.IgnoredFilesInventoryComplete)
                    {
                        copyIgnoredWarned = true;
                        progress?.Report(new RelocateStepUpdate(RelocateStep.CopyIgnoredFiles, RelocateStepState.Warning,
                            "Ignored files were not copied because the inventory exceeds the 1,000-entry safety limit. Source will be retained.", null));
                    }
                    else if (ignored.Count > MaxIgnoredFilesToCopy)
                    {
                        copyIgnoredWarned = true;
                        progress?.Report(new RelocateStepUpdate(RelocateStep.CopyIgnoredFiles, RelocateStepState.Warning,
                            $"Ignored files were not copied because {ignored.Count} entries exceed the {MaxIgnoredFilesToCopy}-file safety limit. Source will be retained.", null));
                    }
                    else
                    {
                        var copy = await _fileTransfer.CopyFilesAsync(
                            srcEndpoint, dstEndpoint, ignored, MaxIgnoredFileBytes, ct).ConfigureAwait(false);
                        long accountedFiles = (long)copy.FilesCopied + copy.FilesSkipped;

                        if (!copy.Success)
                        {
                            copyIgnoredWarned = true;
                            var detail = string.IsNullOrWhiteSpace(copy.ErrorMessage)
                                ? "Ignored-file copy did not complete."
                                : "Some ignored files were not copied: " + copy.ErrorMessage;
                            progress?.Report(new RelocateStepUpdate(
                                RelocateStep.CopyIgnoredFiles,
                                RelocateStepState.Warning,
                                detail,
                                null));
                        }
                        else if (copy.FilesCopied < 0
                            || copy.FilesSkipped < 0
                            || accountedFiles != ignored.Count)
                        {
                            copyIgnoredWarned = true;
                            progress?.Report(new RelocateStepUpdate(
                                RelocateStep.CopyIgnoredFiles,
                                RelocateStepState.Warning,
                                $"Ignored-file copy could account for {accountedFiles} of {ignored.Count} file(s). Source will be retained.",
                                null));
                        }
                        else if (copy.Warnings.Count > 0 || copy.FilesSkipped > 0)
                        {
                            copyIgnoredWarned = true;
                            var warningDetail = copy.Warnings.Count == 0
                                ? string.Empty
                                : " (" + string.Join("; ", copy.Warnings.Take(3)) + ")";
                            var detail = $"Copied {copy.FilesCopied}, skipped {copy.FilesSkipped}{warningDetail}";
                            progress?.Report(new RelocateStepUpdate(RelocateStep.CopyIgnoredFiles, RelocateStepState.Warning, detail, null));
                        }
                        else
                        {
                            copiedIgnoredInventory = ignored;
                            progress?.Report(new RelocateStepUpdate(RelocateStep.CopyIgnoredFiles, RelocateStepState.Done,
                                $"Copied {copy.FilesCopied} file(s) ({FormatBytes(copy.BytesCopied)})", null));
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    progress?.Report(new RelocateStepUpdate(RelocateStep.CopyIgnoredFiles, RelocateStepState.Cancelled, "Cancelled.", null));
                    result.FailedStep = RelocateStep.CopyIgnoredFiles;
                    result.Cancelled = true;
                    result.ErrorMessage = "Relocate cancelled.";
                    return result;
                }
                catch (Exception ex)
                {
                    copyIgnoredWarned = true;
                    progress?.Report(new RelocateStepUpdate(RelocateStep.CopyIgnoredFiles, RelocateStepState.Warning,
                        "Ignored copy failed: " + ex.Message, null));
                }
            }
            else
            {
                progress?.Report(new RelocateStepUpdate(RelocateStep.CopyIgnoredFiles, RelocateStepState.Skipped, "Not requested.", null));
            }

            // ---- Step 5: Verify destination ----
            progress?.Report(new RelocateStepUpdate(RelocateStep.VerifyDestination, RelocateStepState.Running, targetPath, null));
            try
            {
                GitWorkspaceClassification destClass;
                if (!targetIsSsh)
                {
                    destClass = await _workspaceInspector.ClassifyAsync(targetPath, ct).ConfigureAwait(false);
                }
                else
                {
                    var c = tgtCreds!.Value;
                    destClass = await _sshGitInspector.ClassifyAsync(
                        c.host, c.port, c.user, c.password, targetPath, ct).ConfigureAwait(false);
                }

                if (destClass is not WorkingTreeRepo destWorking)
                {
                    throw new InvalidOperationException("Destination is not a working-tree repo after clone.");
                }

                // Compare origin via identity — handles scp vs https equivalence.
                var sourceIdentity = _workspaceInspector.GetRemoteIdentity(pre.OriginUrl);
                var destIdentity = _workspaceInspector.GetRemoteIdentity(destWorking.OriginUrl ?? string.Empty);
                if (!string.Equals(sourceIdentity, destIdentity, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Destination origin '{destWorking.OriginUrl}' does not match source origin '{pre.OriginUrl}'.");
                }

                RelocationGitState destinationState;
                if (!targetIsSsh)
                {
                    destinationState = await _workspaceInspector.ReadRelocationStateAsync(
                        targetPath,
                        ct).ConfigureAwait(false);
                }
                else
                {
                    var c = tgtCreds!.Value;
                    destinationState = await _sshGitInspector.ReadRelocationStateAsync(
                        c.host,
                        c.port,
                        c.user,
                        c.password,
                        targetPath,
                        ct).ConfigureAwait(false);
                }

                if (!destinationState.Success)
                {
                    throw new InvalidOperationException(
                        "Destination Git state could not be verified: " + destinationState.ErrorMessage);
                }
                if (!string.Equals(
                    destinationState.Branch,
                    pre.SourceBranch,
                    StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Destination branch '{destinationState.Branch}' does not match source branch '{pre.SourceBranch}'.");
                }
                if (!string.Equals(
                    destinationState.HeadSha,
                    pre.SourceHeadSha,
                    StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"Destination HEAD '{destinationState.HeadSha}' does not match source HEAD '{pre.SourceHeadSha}'.");
                }

                var currentSourceState = await ReadSourceStateAsync(
                    sourceIsSsh,
                    srcCreds,
                    sourcePath,
                    ct).ConfigureAwait(false);
                if (!TryValidateUnchangedSource(
                    currentSourceState,
                    pre,
                    out var sourceChangeError))
                {
                    throw new InvalidOperationException(
                        sourceChangeError + " The portfolio was not rebound.");
                }

                progress?.Report(new RelocateStepUpdate(RelocateStep.VerifyDestination, RelocateStepState.Done,
                    "Source and destination origin, branch, and HEAD match.", null));
            }
            catch (Exception ex)
            {
                progress?.Report(new RelocateStepUpdate(RelocateStep.VerifyDestination, RelocateStepState.Failed, ex.Message, null));
                result.FailedStep = RelocateStep.VerifyDestination;
                result.ErrorMessage = ex.Message;
                return result;
            }

            // ---- Step 6: Rebind portfolio ----
            progress?.Report(new RelocateStepUpdate(RelocateStep.RebindPortfolio, RelocateStepState.Running, "Updating portfolio entry…", null));
            try
            {
                var regRequest = new ProjectRegistrationRequest
                {
                    ProjectId = request.ProjectId,
                    DisplayName = request.DisplayName,
                    Summary = request.Summary,
                    LifecycleState = string.IsNullOrWhiteSpace(request.LifecycleState) ? "active" : request.LifecycleState,
                    LocalPath = targetIsSsh ? string.Empty : targetPath,
                    SshTarget = targetIsSsh
                        ? FormatSshTarget(targetStore, folder, pre.TargetIsWindows)
                        : string.Empty,
                    GitHubUrl = request.GitHubUrl,
                    AdoUrl = request.AdoUrl,
                    RemoteUrl = request.RemoteUrl,
                    AllowOverwrite = true,
                    // Metadata is central (keyed by project id); RegisterProject
                    // reads the existing stub to preserve docs / external_refs and
                    // rewrites the working-tree pointer to the new location.
                    SourcePath = string.Empty
                };
                var reg = _registrationService.RegisterProject(regRequest);
                if (!reg.Success)
                {
                    throw new InvalidOperationException(reg.Message);
                }
                progress?.Report(new RelocateStepUpdate(RelocateStep.RebindPortfolio, RelocateStepState.Done, "Portfolio updated.", null));
            }
            catch (Exception ex)
            {
                progress?.Report(new RelocateStepUpdate(RelocateStep.RebindPortfolio, RelocateStepState.Failed, ex.Message, null));
                result.FailedStep = RelocateStep.RebindPortfolio;
                result.ErrorMessage = ex.Message;
                return result;
            }

            // ---- Step 7: Delete source (optional) ----
            if (request.DeleteSourceAfterSuccess)
            {
                if (copyIgnoredWarned
                    || (request.CopyIgnoredFiles && copiedIgnoredInventory == null))
                {
                    progress?.Report(new RelocateStepUpdate(RelocateStep.DeleteSource, RelocateStepState.Skipped,
                        "Source not deleted — ignored-file transfer was not proven complete.", null));
                }
                else
                {
                    progress?.Report(new RelocateStepUpdate(RelocateStep.DeleteSource, RelocateStepState.Running, sourcePath, null));
                    try
                    {
                        var currentSourceState = await ReadSourceStateAsync(
                            sourceIsSsh,
                            srcCreds,
                            sourcePath,
                            ct).ConfigureAwait(false);
                        if (!TryValidateUnchangedSource(
                            currentSourceState,
                            pre,
                            out var sourceChangeError))
                        {
                            throw new InvalidOperationException(
                                sourceChangeError + " It was not deleted.");
                        }

                        if (!currentSourceState.IgnoredFilesInventoryComplete)
                        {
                            throw new InvalidOperationException(
                                "The ignored-file inventory is incomplete. Source deletion was refused.");
                        }

                        if (request.CopyIgnoredFiles)
                        {
                            var currentIgnoredInventory = GetTransferableIgnoredFiles(
                                currentSourceState.Status.IgnoredFiles);
                            if (currentIgnoredInventory.Count > MaxIgnoredFilesToCopy
                                || !currentIgnoredInventory.SequenceEqual(
                                    copiedIgnoredInventory!,
                                    StringComparer.Ordinal))
                            {
                                throw new InvalidOperationException(
                                    "The ignored-file inventory changed after copy. Source deletion was refused.");
                            }
                        }

                        if (!sourceIsSsh)
                        {
                            bool deleted = _deleteToRecycleBin != null
                                ? _deleteToRecycleBin(sourcePath, request.ProjectId)
                                : SafeDeleteLocal(sourcePath);
                            if (!deleted) throw new InvalidOperationException("Recycle bin deletion failed.");
                        }
                        else
                        {
                            var s = srcCreds!.Value;
                            string cmd = pre.SourceIsWindows
                                ? "rmdir /s /q " + ShellQuoteForRemote(sourcePath, isWindowsHost: true)
                                : "rm -rf " + ShellQuoteForRemote(sourcePath, isWindowsHost: false);
                            var del = _sshService.RunCommand(s.host, s.port, s.user, s.password, cmd);
                            if (!del.Success) throw new InvalidOperationException("Remote delete failed: " + del.Error);
                        }
                        progress?.Report(new RelocateStepUpdate(RelocateStep.DeleteSource, RelocateStepState.Done, "Source removed.", null));
                    }
                    catch (Exception ex)
                    {
                        progress?.Report(new RelocateStepUpdate(RelocateStep.DeleteSource, RelocateStepState.Warning,
                            "Could not remove source: " + ex.Message, null));
                    }
                }
            }
            else
            {
                progress?.Report(new RelocateStepUpdate(RelocateStep.DeleteSource, RelocateStepState.Skipped, "Source preserved.", null));
            }

            result.Success = true;
            result.FinalTargetPath = targetIsSsh
                ? FormatSshTarget(targetStore, folder, pre.TargetIsWindows)
                : targetPath;
            return result;
        }

        public async Task<RelocateStepUpdate> PushSourceAsync(RelocateRequest request, CancellationToken ct)
        {
            if (request == null)
            {
                return new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Failed, "No request.", null);
            }

            bool sourceIsSsh = ComputeSourceIsSsh(request.SourceSshTarget);
            if (sourceIsSsh)
            {
                var sshCreds = ResolveSshCredentials(request.SourceStoreId, request.SourceSshTarget);
                if (sshCreds == null)
                {
                    return new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Failed,
                        "No SSH credentials for source.", null);
                }
                if (!TryParseSshTarget(request.SourceSshTarget, out _, out var remotePath))
                {
                    return new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Failed,
                        "SSH target malformed.", null);
                }
                var c = sshCreds.Value;
                if (!TryDetectRemoteIsWindows(c, out var remoteIsWindows, out var osError))
                {
                    return new RelocateStepUpdate(
                        RelocateStep.Preflight,
                        RelocateStepState.Failed,
                        osError,
                        null);
                }
                string cmd = "git -C " + ShellQuoteForRemote(remotePath, remoteIsWindows) + " push";
                var run = _sshService.RunCommand(c.host, c.port, c.user, c.password, cmd);
                if (!run.Success)
                {
                    return new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Failed,
                        "Push failed: " + (string.IsNullOrEmpty(run.Error) ? run.Output : run.Error), null);
                }
                return new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Done, "Push succeeded.", null);
            }
            else
            {
                var path = (request.SourceLocalPath ?? string.Empty).Trim();
                if (!Directory.Exists(path))
                {
                    return new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Failed, "Source path missing.", null);
                }
                var run = await _gitAdapter.RunAsync(
                    new[] { "push" }, path, TimeSpan.FromMinutes(5), progress: null, ct).ConfigureAwait(false);
                if (run.ExitCode != 0)
                {
                    return new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Failed,
                        "Push failed: " + (string.IsNullOrEmpty(run.Stderr) ? run.Stdout : run.Stderr), null);
                }
                return new RelocateStepUpdate(RelocateStep.Preflight, RelocateStepState.Done, "Push succeeded.", null);
            }
        }

        // ---- Helpers ----

        private static string ComputeTargetPath(
            RepoStore targetStore,
            string folder,
            bool remoteIsWindows)
        {
            if (targetStore.IsSsh)
            {
                return JoinRemotePath(targetStore.Root, folder, remoteIsWindows);
            }
            return Path.GetFullPath(Path.Combine(targetStore.Root, folder));
        }

        private static string FormatSshTarget(
            RepoStore store,
            string folder,
            bool remoteIsWindows)
        {
            var userPrefix = string.IsNullOrWhiteSpace(store.User) ? string.Empty : store.User + "@";
            var remotePath = JoinRemotePath(store.Root, folder, remoteIsWindows);
            return userPrefix + store.Host + ":" + remotePath;
        }

        private static bool TryParseSshTarget(string sshTarget, out string host, out string remotePath)
        {
            host = string.Empty;
            remotePath = string.Empty;
            if (string.IsNullOrWhiteSpace(sshTarget)) return false;
            var sep = sshTarget.IndexOf(':');
            if (sep <= 1 || sep >= sshTarget.Length - 1) return false;
            host = sshTarget.Substring(0, sep).Trim();
            remotePath = sshTarget.Substring(sep + 1).Trim();
            return true;
        }

        // Defense in depth: treat the source as SSH only when SourceSshTarget
        // is both non-empty AND parses as a user@host:path tuple. This
        // prevents display-layer sentinels ("Not configured") from being
        // misinterpreted as a real SSH target.
        private static bool ComputeSourceIsSsh(string sshTarget)
        {
            return !string.IsNullOrWhiteSpace(sshTarget)
                && TryParseSshTarget(sshTarget, out _, out _);
        }

        private static bool PathsLookIdentical(
            string sourcePath, string targetPath,
            bool sourceIsSsh, bool targetIsSsh,
            string sourceSshTarget, string resolvedTargetPath)
        {
            if (sourceIsSsh != targetIsSsh) return false;
            if (!sourceIsSsh)
            {
                try
                {
                    var s = Path.GetFullPath(sourcePath).TrimEnd(Path.DirectorySeparatorChar);
                    var t = Path.GetFullPath(targetPath).TrimEnd(Path.DirectorySeparatorChar);
                    return string.Equals(s, t, StringComparison.OrdinalIgnoreCase);
                }
                catch
                {
                    return false;
                }
            }
            return string.Equals((sourceSshTarget ?? string.Empty).Trim(),
                (resolvedTargetPath ?? string.Empty).Trim(),
                StringComparison.OrdinalIgnoreCase);
        }

        private (string host, int port, string user, string password)? ResolveStoreCredentials(RepoStore store)
        {
            if (store == null || !store.IsSsh) return null;
            int port = store.Port > 0 ? store.Port : 22;
            string password = string.Empty;
            if (!string.IsNullOrWhiteSpace(store.CredentialTarget))
            {
                password = _credentialStore.GetPassword(store.CredentialTarget) ?? string.Empty;
            }
            return (store.Host, port, store.User, password);
        }

        private (string host, int port, string user, string password)? ResolveSshCredentials(string storeId, string sshTarget)
        {
            if (!TryParseSshTarget(sshTarget, out var hostPart, out _)) return null;
            var atIdx = hostPart.LastIndexOf('@');
            string targetUser = atIdx >= 0 ? hostPart.Substring(0, atIdx) : string.Empty;
            string targetHost = atIdx >= 0 ? hostPart.Substring(atIdx + 1) : hostPart;
            if (string.IsNullOrWhiteSpace(targetHost)) return null;

            // Prefer the explicit store id, but never use its credentials for
            // a different host or user than the project target describes.
            var store = !string.IsNullOrWhiteSpace(storeId) ? _storeProvider.GetStore(storeId) : null;
            if (store != null && store.IsSsh)
            {
                if (!string.Equals(store.Host, targetHost, StringComparison.OrdinalIgnoreCase)
                    || (!string.IsNullOrEmpty(targetUser)
                        && !string.Equals(
                            store.User,
                            targetUser,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }
                return ResolveStoreCredentials(store);
            }

            var match = _storeProvider.GetStores().FirstOrDefault(
                s => s.IsSsh
                     && string.Equals(s.Host, targetHost, StringComparison.OrdinalIgnoreCase)
                     && (string.IsNullOrEmpty(targetUser)
                         || string.Equals(
                             s.User,
                             targetUser,
                             StringComparison.OrdinalIgnoreCase)));
            return ResolveStoreCredentials(match!);
        }

        private async Task<RelocationGitState> ReadSourceStateAsync(
            bool sourceIsSsh,
            (string host, int port, string user, string password)? sourceCredentials,
            string sourcePath,
            CancellationToken ct)
        {
            if (!sourceIsSsh)
            {
                return await _workspaceInspector.ReadRelocationStateAsync(
                    sourcePath,
                    ct).ConfigureAwait(false);
            }

            if (!sourceCredentials.HasValue)
            {
                return RelocationGitState.Failure(
                    "Source SSH credentials are no longer available.");
            }

            var credentials = sourceCredentials.Value;
            return await _sshGitInspector.ReadRelocationStateAsync(
                credentials.host,
                credentials.port,
                credentials.user,
                credentials.password,
                sourcePath,
                ct).ConfigureAwait(false);
        }

        private static bool TryValidateUnchangedSource(
            RelocationGitState state,
            RelocatePreflightResult preflight,
            out string error)
        {
            error = string.Empty;
            if (!state.Success
                || !string.Equals(
                    state.Branch,
                    preflight.SourceBranch,
                    StringComparison.Ordinal)
                || !string.Equals(
                    state.HeadSha,
                    preflight.SourceHeadSha,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    state.OriginHeadSha,
                    preflight.SourceHeadSha,
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    state.Upstream,
                    "origin/" + preflight.SourceBranch,
                    StringComparison.Ordinal)
                || state.Status.AheadOfOrigin != 0
                || state.Status.BehindOrigin != 0
                || state.Status.Modified.Count > 0
                || state.Status.Staged.Count > 0
                || state.Status.UntrackedNotIgnored.Count > 0)
            {
                error =
                    "Source changed or could not be reproduced exactly after cloning.";
                return false;
            }

            return true;
        }

        private static List<string> GetTransferableIgnoredFiles(
            IEnumerable<string> ignoredFiles)
        {
            return (ignoredFiles ?? Enumerable.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Where(path => !path.StartsWith(
                                   ".controltower/",
                                   StringComparison.OrdinalIgnoreCase)
                               && !path.StartsWith(
                                   ".controltower\\",
                                   StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static bool IsSafeSourceDeletionPath(
            string path,
            bool isSsh,
            bool remoteIsWindows)
        {
            if (string.IsNullOrWhiteSpace(path)
                || path.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            {
                return false;
            }

            if (isSsh && !remoteIsWindows)
            {
                if (!path.StartsWith("/", StringComparison.Ordinal)
                    || string.Equals(path.TrimEnd('/'), string.Empty, StringComparison.Ordinal))
                {
                    return false;
                }

                return !path.Split(
                        new[] { '/' },
                        StringSplitOptions.RemoveEmptyEntries)
                    .Any(segment =>
                        string.Equals(segment, ".", StringComparison.Ordinal)
                        || string.Equals(segment, "..", StringComparison.Ordinal));
            }

            var segments = path.Split(
                new[] { '\\', '/' },
                StringSplitOptions.RemoveEmptyEntries);
            if (segments.Any(segment =>
                string.Equals(segment, ".", StringComparison.Ordinal)
                || string.Equals(segment, "..", StringComparison.Ordinal)))
            {
                return false;
            }

            try
            {
                if (!Path.IsPathFullyQualified(path)) return false;
                var fullPath = Path.GetFullPath(path);
                var root = Path.GetPathRoot(fullPath);
                return !string.IsNullOrWhiteSpace(root)
                    && !string.Equals(
                        fullPath.TrimEnd('\\', '/'),
                        root.TrimEnd('\\', '/'),
                        StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private bool TryRemoteDirectoryNonEmpty(
            (string host, int port, string user, string password) creds,
            string remotePath,
            bool remoteIsWindows,
            out bool nonEmpty,
            out string error)
        {
            nonEmpty = false;
            error = string.Empty;
            string quoted = ShellQuoteForRemote(remotePath, remoteIsWindows);
            string cmd = remoteIsWindows
                ? "if exist " + quoted + " (dir /b " + quoted + ") else (echo NOTFOUND)"
                : "test -d " + quoted + " && ls -1A " + quoted + " || echo NOTFOUND";
            var run = _sshService.RunCommand(creds.host, creds.port, creds.user, creds.password, cmd);
            if (!run.Success)
            {
                error = "Could not inspect the SSH destination safely: "
                    + (string.IsNullOrWhiteSpace(run.Error) ? run.Output : run.Error);
                return false;
            }
            var outp = (run.Output ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(outp) || outp.Equals("NOTFOUND", StringComparison.Ordinal))
            {
                return true;
            }
            nonEmpty = true;
            return true;
        }

        private bool TryDetectRemoteIsWindows(
            (string host, int port, string user, string password) creds,
            out bool remoteIsWindows,
            out string error)
        {
            remoteIsWindows = false;
            error = string.Empty;
            var windowsProbe = _sshService.RunCommand(
                creds.host,
                creds.port,
                creds.user,
                creds.password,
                "echo %OS%");
            if (!windowsProbe.Success)
            {
                error = "Remote OS probe failed: "
                    + (string.IsNullOrWhiteSpace(windowsProbe.Error)
                        ? windowsProbe.Output
                        : windowsProbe.Error);
                return false;
            }

            if (windowsProbe.Output.IndexOf("Windows_NT", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                remoteIsWindows = true;
                return true;
            }

            var posixProbe = _sshService.RunCommand(
                creds.host,
                creds.port,
                creds.user,
                creds.password,
                "uname -s");
            if (posixProbe.Success && IsKnownPosixOs(posixProbe.Output.Trim()))
            {
                return true;
            }

            error = $"Cannot determine remote OS on '{creds.host}' safely.";
            return false;
        }

        private static bool IsKnownPosixOs(string value)
        {
            return value.StartsWith("Linux", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("Darwin", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("FreeBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("OpenBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("NetBSD", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("SunOS", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("CYGWIN", StringComparison.OrdinalIgnoreCase)
                || value.StartsWith("MINGW", StringComparison.OrdinalIgnoreCase);
        }

        private static string JoinRemotePath(
            string root,
            string folder,
            bool remoteIsWindows)
        {
            var separator = remoteIsWindows ? '\\' : '/';
            return (root ?? string.Empty).TrimEnd('/', '\\')
                + separator
                + (folder ?? string.Empty).TrimStart('/', '\\');
        }

        private static bool TryReadLsRemoteSha(
            string output,
            string expectedRef,
            out string sha)
        {
            sha = string.Empty;
            using var reader = new StringReader(output ?? string.Empty);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                var parts = line.Split(
                    new[] { ' ', '\t' },
                    StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2
                    && string.Equals(parts[1], expectedRef, StringComparison.Ordinal)
                    && IsFullObjectId(parts[0]))
                {
                    if (!string.IsNullOrEmpty(sha)) return false;
                    sha = parts[0].ToLowerInvariant();
                }
            }
            return !string.IsNullOrEmpty(sha);
        }

        private static bool IsFullObjectId(string value)
        {
            if (value == null || (value.Length != 40 && value.Length != 64))
            {
                return false;
            }
            return value.All(ch =>
                (ch >= '0' && ch <= '9')
                || (ch >= 'a' && ch <= 'f')
                || (ch >= 'A' && ch <= 'F'));
        }

        /// <summary>
        /// Quotes a single value for embedding in a remote SSH command. The
        /// concrete implementation lives in
        /// Delegates to <see cref="SshCommandQuoter"/> which is the single
        /// authoritative implementation for remote-shell quoting.
        /// </summary>
        private static string ShellQuoteForRemote(string value, bool isWindowsHost)
        {
            return isWindowsHost
                ? SshCommandQuoter.QuoteWindows(value)
                : SshCommandQuoter.QuotePosix(value);
        }

        private static bool SafeDeleteLocal(string path)
        {
            // Fallback: directly delete. The production path uses the
            // Recycle-Bin lambda; the fallback is only used by tests.
            try
            {
                Directory.Delete(path, recursive: true);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        }
    }
}
