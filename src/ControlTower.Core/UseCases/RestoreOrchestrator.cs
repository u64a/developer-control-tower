#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Core.UseCases
{
    /// <summary>
    /// Drives a batch of <see cref="RestoreSelection"/>s through optional
    /// quarantine and <see cref="IAsyncCloneService.CloneAsync"/>. Holds
    /// <see cref="ILongRunningGitOperationLock"/> for the duration of
    /// the batch so other long-running git ops cannot interleave.
    /// </summary>
    public sealed class RestoreOrchestrator : IRestoreOrchestrator
    {
        private readonly IAsyncCloneService _cloneService;
        private readonly IQuarantineService _quarantineService;
        private readonly ILongRunningGitOperationLock _operationLock;

        public RestoreOrchestrator(
            IAsyncCloneService cloneService,
            IQuarantineService quarantineService,
            ILongRunningGitOperationLock operationLock)
        {
            _cloneService = cloneService ?? throw new ArgumentNullException(nameof(cloneService));
            _quarantineService = quarantineService ?? throw new ArgumentNullException(nameof(quarantineService));
            _operationLock = operationLock ?? throw new ArgumentNullException(nameof(operationLock));
        }

        public async Task RestoreAsync(
            IReadOnlyList<RestoreSelection> selections,
            IProgress<RestoreRowUpdate>? progress,
            CancellationToken ct)
        {
            if (selections == null || selections.Count == 0)
            {
                return;
            }

            using var _ = await _operationLock.AcquireAsync(ct).ConfigureAwait(false);

            bool cancelledRest = false;
            foreach (var selection in selections)
            {
                if (selection == null || selection.Candidate == null) continue;

                if (cancelledRest || ct.IsCancellationRequested)
                {
                    Emit(progress, selection.Candidate.ProjectId, RestoreRowState.Skipped,
                        detail: "Skipped — batch cancelled before this row started.");
                    cancelledRest = true;
                    continue;
                }

                await ProcessOneAsync(selection, progress, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    cancelledRest = true;
                }
            }
        }

        private async Task ProcessOneAsync(
            RestoreSelection selection,
            IProgress<RestoreRowUpdate>? progress,
            CancellationToken ct)
        {
            var candidate = selection.Candidate;
            var projectId = candidate.ProjectId;

            // Terminal classifications never invoke quarantine or clone.
            if (candidate.Classification == RestoreClassification.AlreadyCloned)
            {
                Emit(progress, projectId, RestoreRowState.AlreadyCloned,
                    detail: candidate.Detail);
                return;
            }

            if (candidate.Classification == RestoreClassification.UnsafeExisting)
            {
                Emit(progress, projectId, RestoreRowState.UnsafeExisting,
                    detail: candidate.Detail);
                return;
            }

            if (selection.Action == RestoreAction.Skip)
            {
                Emit(progress, projectId, RestoreRowState.Skipped, detail: "Skipped by user.");
                return;
            }

            // Credential-bearing RemoteUrl: refuse before starting any process.
            // AsyncCloneService would also refuse, but we surface it through the
            // orchestrator's per-row state machine so the dialog can render the
            // CredentialInUrl error code without having to start a git call.
            if (UrlCarriesCredentials(candidate.RemoteUrl))
            {
                Emit(progress, projectId, RestoreRowState.Failed,
                    errorCode: nameof(CloneError.CredentialInUrl),
                    errorMessage:
                        "Remote URL contains embedded credentials. " +
                        "Use Git Credential Manager or SSH-agent instead.");
                return;
            }

            // Conflict + Skip already handled above; Conflict + QuarantineAndClone goes here.
            string? quarantinePath = null;
            if (candidate.Classification == RestoreClassification.ConflictNonEmpty)
            {
                if (selection.Action != RestoreAction.QuarantineAndClone)
                {
                    Emit(progress, projectId, RestoreRowState.Skipped,
                        detail: "Conflict not resolved; no clone attempted.");
                    return;
                }

                Emit(progress, projectId, RestoreRowState.Quarantining,
                    detail: "Moving existing folder out of the way…");
                try
                {
                    quarantinePath = await _quarantineService
                        .QuarantineAsync(candidate.ExpectedPath, candidate.Slug, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    Emit(progress, projectId, RestoreRowState.Failed,
                        errorCode: "restore/cancelled",
                        errorMessage: "Quarantine cancelled before completion.");
                    return;
                }
                catch (Exception ex)
                {
                    Emit(progress, projectId, RestoreRowState.Failed,
                        errorCode: "restore/quarantine-failed",
                        errorMessage: ex.Message);
                    return;
                }
            }

            // Clone.
            Emit(progress, projectId, RestoreRowState.Cloning,
                detail: "Cloning…", quarantinePath: quarantinePath);

            var rowProgress = new InlineProgress<CloneProgress>(p =>
            {
                Emit(progress, projectId, RestoreRowState.Cloning,
                    percentComplete: p.PercentComplete,
                    detail: p.Message,
                    quarantinePath: quarantinePath);
            });

            CloneResult result;
            try
            {
                result = await _cloneService.CloneAsync(
                    new CloneRequest(candidate.RemoteUrl, candidate.ExpectedPath),
                    rowProgress, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                Emit(progress, projectId, RestoreRowState.Failed,
                    errorCode: "restore/cancelled",
                    errorMessage: "Clone cancelled.",
                    quarantinePath: quarantinePath);
                return;
            }

            if (result.Status == CloneStatus.Succeeded)
            {
                Emit(progress, projectId, RestoreRowState.Done,
                    detail: BuildDoneDetail(result, quarantinePath),
                    quarantinePath: quarantinePath);
                return;
            }

            if (result.Status == CloneStatus.Cancelled)
            {
                Emit(progress, projectId, RestoreRowState.Failed,
                    errorCode: "restore/cancelled",
                    errorMessage: result.Message,
                    quarantinePath: quarantinePath);
                return;
            }

            Emit(progress, projectId, RestoreRowState.Failed,
                errorCode: result.Error.ToString(),
                errorMessage: result.Message,
                quarantinePath: quarantinePath);
        }

        private static string BuildDoneDetail(CloneResult result, string? quarantinePath)
        {
            var branch = string.IsNullOrWhiteSpace(result.ResolvedBranch)
                ? string.Empty
                : "on " + result.ResolvedBranch;
            var detail = string.IsNullOrWhiteSpace(branch) ? "Cloned." : "Cloned " + branch + ".";
            if (!string.IsNullOrWhiteSpace(quarantinePath))
            {
                detail += " Previous content moved to " + quarantinePath + ".";
            }
            return detail;
        }

        private static void Emit(
            IProgress<RestoreRowUpdate>? progress,
            string projectId,
            RestoreRowState state,
            double? percentComplete = null,
            string? detail = null,
            string? quarantinePath = null,
            string? errorCode = null,
            string? errorMessage = null)
        {
            if (progress == null) return;
            try
            {
                progress.Report(new RestoreRowUpdate(
                    ProjectId: projectId,
                    State: state,
                    PercentComplete: percentComplete,
                    Detail: detail,
                    QuarantinePath: quarantinePath,
                    ErrorCode: errorCode,
                    ErrorMessage: errorMessage));
            }
            catch { /* progress sink failures must never break the batch */ }
        }

        // Mirrors AsyncCloneService.UrlCarriesCredentials but is kept here
        // so the orchestrator can pre-check without taking a dependency on
        // Infrastructure. The two implementations must stay in sync.
        internal static bool UrlCarriesCredentials(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;

            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (string.IsNullOrEmpty(uri.UserInfo)) return false;
                // ssh:// with a bare username is identity, not credential.
                if (string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase))
                {
                    return uri.UserInfo.Contains(':');
                }
                return true;
            }

            // scp-like "user@host:path" — never carries a password in user-info.
            return false;
        }

        // IProgress that runs the callback inline on the reporting thread.
        // Used internally so the orchestrator stays single-threaded inside
        // ProcessOneAsync — the *outer* IProgress&lt;RestoreRowUpdate&gt; is
        // responsible for any UI marshalling.
        private sealed class InlineProgress<T> : IProgress<T>
        {
            private readonly Action<T> _onReport;
            public InlineProgress(Action<T> onReport) { _onReport = onReport; }
            public void Report(T value) => _onReport(value);
        }
    }
}
