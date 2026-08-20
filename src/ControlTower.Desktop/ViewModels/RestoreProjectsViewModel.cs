using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Desktop.ViewModels
{
    /// <summary>
    /// View-model for the Restore Projects dialog. Coordinates the
    /// <see cref="IMissingProjectScanner"/> (scan phase) and the
    /// <see cref="IRestoreOrchestrator"/> (batch phase) on a background
    /// thread while updating per-row state on the UI thread through the
    /// <see cref="System.Windows.Threading.Dispatcher"/>.
    /// </summary>
    public sealed class RestoreProjectsViewModel : ObservableObject
    {
        private readonly IPortfolioProvider _portfolioProvider;
        private readonly IProjectProvider _projectProvider;
        private readonly IStoreProvider _storeProvider;
        private readonly IMissingProjectScanner _scanner;
        private readonly IRestoreOrchestrator _orchestrator;
        private readonly WorkspaceProfile _activeProfile;

        private CancellationTokenSource _restoreCts;

        /// <summary>
        /// True if anything in this dialog's lifetime changed portfolio.yml
        /// or the on-disk state of a project the main view cares about
        /// (origin URL caching, successful clone). Settings reads this to
        /// decide whether to refresh the host project list after close.
        /// </summary>
        public bool PortfolioMutated { get; private set; }

        public RestoreProjectsViewModel(
            IPortfolioProvider portfolioProvider,
            IProjectProvider projectProvider,
            IStoreProvider storeProvider,
            IMissingProjectScanner scanner,
            IRestoreOrchestrator orchestrator,
            WorkspaceProfile activeProfile = null)
        {
            _portfolioProvider = portfolioProvider ?? throw new ArgumentNullException(nameof(portfolioProvider));
            _projectProvider = projectProvider ?? throw new ArgumentNullException(nameof(projectProvider));
            _storeProvider = storeProvider ?? throw new ArgumentNullException(nameof(storeProvider));
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _activeProfile = activeProfile ?? WorkspaceProfilePolicy.CreateAllProjectsProfile();

            Rows = new ObservableCollection<RestoreRowViewModel>();
            Rows.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(HasNoRows));
                OnPropertyChanged(nameof(CanRestore));
            };
        }

        public ObservableCollection<RestoreRowViewModel> Rows { get; }

        public bool HasNoRows => Rows.Count == 0 && !IsScanning;

        private bool _isScanning;
        public bool IsScanning
        {
            get => _isScanning;
            private set { _isScanning = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanScan)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(HasNoRows)); }
        }

        private bool _isRestoring;
        public bool IsRestoring
        {
            get => _isRestoring;
            private set { _isRestoring = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanScan)); OnPropertyChanged(nameof(CanRestore)); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(CanClose)); }
        }

        public bool IsBusy => IsScanning || IsRestoring;
        public bool CanScan => !IsBusy;
        public bool CanRestore => !IsBusy && Rows.Any(r => r.IsSelected && r.IsSelectable);
        public bool CanCancel => IsRestoring;
        public bool CanClose => !IsRestoring;

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value; OnPropertyChanged(); }
        }

        private string _summaryText = string.Empty;
        public string SummaryText
        {
            get => _summaryText;
            private set { _summaryText = value; OnPropertyChanged(); }
        }

        public async Task ScanAsync()
        {
            if (IsBusy) return;

            IsScanning = true;
            StatusText = "Scanning portfolio for local projects that need cloning…";
            foreach (var existing in Rows)
            {
                existing.PropertyChanged -= OnRowPropertyChanged;
            }
            Rows.Clear();
            try
            {
                var inputs = BuildInputs();
                if (inputs.Count == 0)
                {
                    SummaryText = "No local-store projects were found in the portfolio.";
                    StatusText = string.Empty;
                    return;
                }

                AppLogger.Info("Restore", $"Scan starting for {inputs.Count} input project(s).");
                IReadOnlyList<RestoreCandidate> candidates =
                    await Task.Run(() => _scanner.ScanAsync(inputs, CancellationToken.None)).ConfigureAwait(true);

                // Persist any new origin URLs the scanner discovered. Compare each
                // candidate's RemoteUrl (live > project.yml > cache) against the
                // cached input value; if it's a non-credential change, write it back.
                var cachesUpdated = TryPersistDiscoveredOrigins(inputs, candidates);
                if (cachesUpdated > 0)
                {
                    PortfolioMutated = true;
                    StatusText = $"Cached {cachesUpdated} project origin URL(s) into portfolio.yml.";
                    AppLogger.Info("Restore", $"Wrote {cachesUpdated} new origin URL(s) to portfolio.yml.");
                }
                else
                {
                    StatusText = string.Empty;
                }

                foreach (var candidate in candidates)
                {
                    var row = RestoreRowViewModel.FromCandidate(candidate);
                    row.PropertyChanged += OnRowPropertyChanged;
                    Rows.Add(row);
                }
                // Pre-select the rows we can act on without user prompting.
                // MissingNeedsUrl rows are intentionally NOT pre-selected — the
                // user has to type a URL to make them selectable.
                foreach (var r in Rows.Where(r => r.IsSelectable && r.Action == RestoreAction.Clone))
                {
                    r.IsSelected = true;
                }
                UpdateSummary();
                AppLogger.Info("Restore", $"Scan complete: {Rows.Count} candidate(s).");
            }
            catch (Exception ex)
            {
                AppLogger.Error("Restore", "Scan failed: " + ex.Message);
                StatusText = "Scan failed: " + ex.Message;
            }
            finally
            {
                IsScanning = false;
                NotifyCommandsChanged();
            }
        }

        public void SelectAllSelectable()
        {
            foreach (var r in Rows.Where(r => r.IsSelectable))
            {
                r.IsSelected = true;
            }
            NotifyCommandsChanged();
        }

        public void SelectNone()
        {
            foreach (var r in Rows)
            {
                r.IsSelected = false;
            }
            NotifyCommandsChanged();
        }

        public async Task RestoreSelectedAsync()
        {
            if (IsBusy) return;
            var selected = Rows.Where(r => r.IsSelected && r.IsSelectable).ToList();
            if (selected.Count == 0)
            {
                StatusText = "Select at least one row to restore.";
                return;
            }

            // Confirm quarantine actions.
            var willQuarantine = selected.Count(r => r.Action == RestoreAction.QuarantineAndClone);
            if (willQuarantine > 0)
            {
                var msg = willQuarantine == 1
                    ? "1 project will have its existing folder moved into a quarantine before cloning. Continue?"
                    : willQuarantine + " projects will have their existing folders moved into a quarantine before cloning. Continue?";
                var confirm = MessageBox.Show(msg, "Restore from Git",
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.OK) return;
            }

            _restoreCts = new CancellationTokenSource();
            IsRestoring = true;
            StatusText = "Restoring…";
            try
            {
                var byId = Rows.ToDictionary(r => r.ProjectId, StringComparer.Ordinal);
                foreach (var r in selected) r.MarkPending();

                // For MissingNeedsUrl rows the user typed a URL inline — substitute
                // it into the candidate so the orchestrator clones from the right
                // source. Records support `with` for non-destructive updates.
                var selections = selected.Select(r =>
                {
                    var candidate = r.Candidate;
                    var effectiveUrl = r.EffectiveRemoteUrl;
                    if (!string.Equals(candidate.RemoteUrl, effectiveUrl, StringComparison.Ordinal))
                    {
                        candidate = candidate with { RemoteUrl = effectiveUrl };
                    }
                    return new RestoreSelection(candidate, r.Action);
                }).ToList();

                IProgress<RestoreRowUpdate> progress = new DispatcherProgress<RestoreRowUpdate>(update =>
                {
                    if (byId.TryGetValue(update.ProjectId, out var row))
                    {
                        row.Apply(update);
                    }
                });

                AppLogger.Info("Restore", $"Restore starting for {selections.Count} row(s).");
                await Task.Run(
                    () => _orchestrator.RestoreAsync(selections, progress, _restoreCts.Token),
                    _restoreCts.Token).ConfigureAwait(true);

                // Any clone that reached Done changes the project's IsReady
                // state in the host portfolio view, and may have written a
                // remote_url back below.
                if (Rows.Any(r => r.State == RestoreRowState.Done))
                {
                    PortfolioMutated = true;
                }

                // Cache the URLs we just used for any newly-successful clones so
                // future restores don't need the user to retype them.
                var savedAfterRestore = TryPersistAfterRestore(selections);
                if (savedAfterRestore > 0)
                {
                    PortfolioMutated = true;
                }
                StatusText = BuildCompletionStatus()
                    + (savedAfterRestore > 0 ? $" Cached {savedAfterRestore} URL(s) to portfolio.yml." : string.Empty);
                AppLogger.Info("Restore", "Restore complete. " + StatusText);
            }
            catch (OperationCanceledException)
            {
                StatusText = "Restore cancelled.";
                AppLogger.Info("Restore", "Restore cancelled by user.");
            }
            catch (Exception ex)
            {
                StatusText = "Restore failed: " + ex.Message;
                AppLogger.Error("Restore", "Restore threw: " + ex.Message);
            }
            finally
            {
                IsRestoring = false;
                _restoreCts?.Dispose();
                _restoreCts = null;
                UpdateSummary();
                NotifyCommandsChanged();
            }
        }

        public void CancelRestore()
        {
            try
            {
                _restoreCts?.Cancel();
                StatusText = "Cancelling…";
            }
            catch { /* ignore */ }
        }

        public void NotifyCommandsChanged()
        {
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanRestore));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanClose));
            OnPropertyChanged(nameof(IsBusy));
        }

        private void OnRowPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RestoreRowViewModel.IsSelected)
                || e.PropertyName == nameof(RestoreRowViewModel.Action)
                || e.PropertyName == nameof(RestoreRowViewModel.IsSelectable))
            {
                OnPropertyChanged(nameof(CanRestore));
            }
        }

        private IReadOnlyList<ProjectRestoreInput> BuildInputs()
        {
            var inputs = new List<ProjectRestoreInput>();
            PortfolioIndex portfolio;
            try
            {
                portfolio = _portfolioProvider.LoadPortfolio();
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Restore", "LoadPortfolio threw: " + ex.Message);
                return inputs;
            }

            if (portfolio?.Projects == null) return inputs;

            foreach (var projectRef in WorkspaceProfilePolicy.FilterProjects(
                portfolio.Projects,
                _activeProfile))
            {
                if (string.IsNullOrWhiteSpace(projectRef.Id)) continue;

                bool isLocalStore;
                string expectedPath;

                if (!string.IsNullOrWhiteSpace(projectRef.StoreId))
                {
                    // Store-backed entry: ask the store provider. SSH stores
                    // are filtered out (handled by Feature B Relocate later).
                    var store = _storeProvider.GetStore(projectRef.StoreId);
                    isLocalStore = store != null && !store.IsSsh;
                    expectedPath = isLocalStore
                        ? _storeProvider.ResolveProjectPath(projectRef.StoreId, projectRef.Id, projectRef.Folder)
                        : string.Empty;
                }
                else
                {
                    // Legacy v0 entry: portfolio.yml carries a raw `path:` value.
                    // Treat it as local if the path is a fully qualified file path
                    // and not an SSH-style host:path string. Mapped drives and UNC
                    // paths are accepted (git clone supports both).
                    expectedPath = projectRef.Path ?? string.Empty;
                    isLocalStore = IsFullyQualifiedLocalPath(expectedPath);
                }

                if (string.IsNullOrWhiteSpace(expectedPath))
                {
                    continue;
                }

                var slug = string.IsNullOrWhiteSpace(projectRef.Folder)
                    ? projectRef.Id
                    : projectRef.Folder;

                // RemoteUrl priority for the SCANNER INPUT:
                //   1. Live project.yml Locations.RemoteUrl (if folder + yaml exist)
                //   2. Cached projectRef.RemoteUrl (always tried as fallback)
                // The scanner then runs git inspection and may overwrite the
                // candidate's RemoteUrl with the live .git/config origin — that
                // becomes the authoritative value used for cache writeback.
                string remoteUrl = string.Empty;
                string displayName = projectRef.Id;

                if (isLocalStore && !string.IsNullOrWhiteSpace(expectedPath))
                {
                    try
                    {
                        var loaded = _projectProvider.LoadProject(expectedPath);
                        if (loaded?.Project != null)
                        {
                            remoteUrl = loaded.Project.Locations?.RemoteUrl ?? string.Empty;
                            if (!string.IsNullOrWhiteSpace(loaded.Project.DisplayName))
                            {
                                displayName = loaded.Project.DisplayName;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        // Folder missing is the dominant case here — fine. Other
                        // errors are logged for diagnostics but don't block the row.
                        AppLogger.Warn(
                            "Restore",
                            $"LoadProject('{expectedPath}') failed: {ex.Message}");
                    }
                }

                if (string.IsNullOrWhiteSpace(remoteUrl))
                {
                    remoteUrl = projectRef.RemoteUrl ?? string.Empty;
                }

                inputs.Add(new ProjectRestoreInput(
                    ProjectId: projectRef.Id,
                    ProjectName: displayName,
                    Slug: slug,
                    ExpectedPath: expectedPath,
                    RemoteUrl: remoteUrl,
                    IsLocalStore: isLocalStore));
            }
            return inputs;
        }

        /// <summary>
        /// True when the path is a fully qualified Windows-style filesystem
        /// path (drive-rooted, UNC, or absolute POSIX). Excludes drive-relative
        /// (<c>C:foo</c>) and SCP-style SSH (<c>user@host:path</c>) forms.
        /// </summary>
        private static bool IsFullyQualifiedLocalPath(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;

            // Reject SCP-style SSH: "user@host:path" or "host:path". A drive
            // letter looks like "C:" so we must distinguish — drive letter is
            // a single letter followed by `:` and then `\` or `/`.
            var atIndex = value.IndexOf('@');
            var colonIndex = value.IndexOf(':');
            if (colonIndex > 0)
            {
                bool isDriveLetter = colonIndex == 1
                    && char.IsLetter(value[0])
                    && value.Length > 2 && (value[2] == '\\' || value[2] == '/');
                bool isScpStyle = !isDriveLetter && (atIndex >= 0 || colonIndex > 1);
                if (isScpStyle) return false;
            }

            try
            {
                return Path.IsPathFullyQualified(value);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Walks the scan output and persists any newly-discovered, clean
        /// origin URL back into portfolio.yml. Returns the count of entries
        /// updated. Never writes credential-bearing URLs.
        /// </summary>
        private int TryPersistDiscoveredOrigins(
            IReadOnlyList<ProjectRestoreInput> inputs,
            IReadOnlyList<RestoreCandidate> candidates)
        {
            if (inputs == null || candidates == null || candidates.Count == 0) return 0;

            var inputsById = inputs.ToDictionary(i => i.ProjectId, StringComparer.Ordinal);
            var toCache = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var c in candidates)
            {
                if (string.IsNullOrWhiteSpace(c.RemoteUrl)) continue;
                if (UrlCarriesCredentials(c.RemoteUrl))
                {
                    AppLogger.Warn("Restore",
                        $"Refused to cache credential-bearing origin URL for project '{c.ProjectId}'.");
                    continue;
                }

                inputsById.TryGetValue(c.ProjectId, out var input);
                var inputUrl = input?.RemoteUrl ?? string.Empty;

                // Only persist when this differs from what the input carried
                // (input may be cache OR live project.yml — both are persisted
                // sources, no point rewriting if they already match).
                if (!string.Equals(inputUrl, c.RemoteUrl, StringComparison.Ordinal))
                {
                    toCache[c.ProjectId] = c.RemoteUrl;
                }
            }

            if (toCache.Count == 0) return 0;
            return WriteCacheUpdates(toCache);
        }

        private int TryPersistAfterRestore(IReadOnlyList<RestoreSelection> selections)
        {
            if (selections == null || selections.Count == 0) return 0;

            var byId = Rows.ToDictionary(r => r.ProjectId, StringComparer.Ordinal);
            var toCache = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var sel in selections)
            {
                if (sel?.Candidate == null) continue;
                if (!byId.TryGetValue(sel.Candidate.ProjectId, out var row)) continue;
                if (row.State != RestoreRowState.Done) continue;
                var url = sel.Candidate.RemoteUrl;
                if (string.IsNullOrWhiteSpace(url)) continue;
                if (UrlCarriesCredentials(url)) continue;
                toCache[sel.Candidate.ProjectId] = url;
            }

            if (toCache.Count == 0) return 0;
            return WriteCacheUpdates(toCache);
        }

        private int WriteCacheUpdates(IReadOnlyDictionary<string, string> toCache)
        {
            try
            {
                var current = _portfolioProvider.LoadPortfolio();
                int updated = 0;
                foreach (var p in current.Projects)
                {
                    if (toCache.TryGetValue(p.Id, out var url) &&
                        !string.Equals(p.RemoteUrl, url, StringComparison.Ordinal))
                    {
                        p.RemoteUrl = url;
                        updated++;
                    }
                }
                if (updated > 0)
                {
                    _portfolioProvider.SavePortfolio(current);
                }
                return updated;
            }
            catch (Exception ex)
            {
                AppLogger.Warn("Restore", "Failed to persist origin URL cache: " + ex.Message);
                return 0;
            }
        }

        // Mirrors RestoreOrchestrator.UrlCarriesCredentials / AsyncCloneService.UrlCarriesCredentials.
        // Kept here so the VM can pre-check before writing to portfolio.yml.
        // All three implementations must stay in sync.
        private static bool UrlCarriesCredentials(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (string.IsNullOrEmpty(uri.UserInfo)) return false;
                if (string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase))
                    return uri.UserInfo.Contains(':');
                return true;
            }
            return false;
        }

        private void UpdateSummary()
        {
            if (Rows.Count == 0)
            {
                SummaryText = "No candidates.";
                return;
            }
            var actionable = Rows.Count(r => r.IsSelectable);
            var blocked = Rows.Count - actionable;
            SummaryText = blocked == 0
                ? actionable + " candidate(s) ready to restore."
                : actionable + " ready, " + blocked + " require manual review.";
        }

        private string BuildCompletionStatus()
        {
            int done = Rows.Count(r => r.State == RestoreRowState.Done);
            int failed = Rows.Count(r => r.State == RestoreRowState.Failed);
            int skipped = Rows.Count(r => r.State == RestoreRowState.Skipped);
            return $"Done: {done}, failed: {failed}, skipped: {skipped}.";
        }

        /// <summary>
        /// IProgress&lt;T&gt; that marshals reports onto the UI dispatcher
        /// captured at construction time. Used so the orchestrator (which
        /// runs on a thread-pool task) can safely mutate the ObservableCollection.
        /// </summary>
        private sealed class DispatcherProgress<T> : IProgress<T>
        {
            private readonly System.Windows.Threading.Dispatcher _dispatcher;
            private readonly Action<T> _onReport;
            public DispatcherProgress(Action<T> onReport)
            {
                _onReport = onReport;
                _dispatcher = System.Windows.Application.Current?.Dispatcher
                    ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            }
            public void Report(T value)
            {
                if (_dispatcher.CheckAccess()) _onReport(value);
                else _dispatcher.BeginInvoke(_onReport, value);
            }
        }
    }

    /// <summary>
    /// Row-level view-model for a single <see cref="RestoreCandidate"/>.
    /// Tracks user selection, the chosen <see cref="RestoreAction"/>, and
    /// live state emitted by the orchestrator.
    /// </summary>
    public sealed class RestoreRowViewModel : ObservableObject
    {
        private RestoreRowViewModel(RestoreCandidate candidate)
        {
            Candidate = candidate;
            _state = MapInitialState(candidate.Classification);
            _detail = candidate.Detail ?? string.Empty;
            _action = DefaultActionFor(candidate.Classification);
            _userProvidedUrl = string.Empty;
        }

        public static RestoreRowViewModel FromCandidate(RestoreCandidate candidate)
            => new RestoreRowViewModel(candidate);

        public RestoreCandidate Candidate { get; }

        public string ProjectId => Candidate.ProjectId;
        public string ProjectName => Candidate.ProjectName;
        public string Slug => Candidate.Slug;
        public string ExpectedPath => Candidate.ExpectedPath;
        public string RemoteUrl => Candidate.RemoteUrl;
        public RestoreClassification Classification => Candidate.Classification;

        public string ClassificationLabel => MapClassificationLabel(Classification);

        /// <summary>
        /// For <see cref="RestoreClassification.MissingNeedsUrl"/> rows the
        /// user must paste the origin URL before the row can be cloned. For
        /// all other classifications this is empty and ignored.
        /// </summary>
        private string _userProvidedUrl;
        public string UserProvidedUrl
        {
            get => _userProvidedUrl;
            set
            {
                var trimmed = (value ?? string.Empty).Trim();
                if (_userProvidedUrl != trimmed)
                {
                    _userProvidedUrl = trimmed;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(EffectiveRemoteUrl));
                    OnPropertyChanged(nameof(IsSelectable));
                    OnPropertyChanged(nameof(UrlValidationMessage));
                    OnPropertyChanged(nameof(HasUrlValidationMessage));
                    // Auto-deselect if URL becomes invalid.
                    if (!IsSelectable && IsSelected) IsSelected = false;
                }
            }
        }

        public bool NeedsUserUrl => Classification == RestoreClassification.MissingNeedsUrl;

        public string EffectiveRemoteUrl =>
            NeedsUserUrl ? _userProvidedUrl : Candidate.RemoteUrl;

        public string UrlValidationMessage
        {
            get
            {
                if (!NeedsUserUrl) return string.Empty;
                if (string.IsNullOrWhiteSpace(_userProvidedUrl)) return string.Empty;
                if (UrlCarriesCredentials(_userProvidedUrl))
                    return "URL contains embedded credentials. Remove them — use Git Credential Manager or SSH-agent.";
                if (!LooksLikeSupportedUrl(_userProvidedUrl))
                    return "URL is not in a supported form (https://… or git@host:owner/repo).";
                return string.Empty;
            }
        }

        public bool HasUrlValidationMessage => !string.IsNullOrEmpty(UrlValidationMessage);

        public bool IsSelectable
        {
            get
            {
                switch (Classification)
                {
                    case RestoreClassification.Missing:
                    case RestoreClassification.EmptyFolder:
                    case RestoreClassification.ConflictNonEmpty:
                        return true;
                    case RestoreClassification.MissingNeedsUrl:
                        return !string.IsNullOrWhiteSpace(_userProvidedUrl)
                            && !UrlCarriesCredentials(_userProvidedUrl)
                            && LooksLikeSupportedUrl(_userProvidedUrl);
                    default:
                        return false;
                }
            }
        }

        public bool ShowActionPicker => Classification == RestoreClassification.ConflictNonEmpty;

        public bool ShowUrlEditor => NeedsUserUrl;

        public IReadOnlyList<RestoreActionOption> ActionOptions { get; } = new[]
        {
            new RestoreActionOption(RestoreAction.QuarantineAndClone, "Quarantine & clone"),
            new RestoreActionOption(RestoreAction.Skip, "Skip"),
        };

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (_isSelected != value) { _isSelected = value; OnPropertyChanged(); } }
        }

        private RestoreAction _action;
        public RestoreAction Action
        {
            get => _action;
            set
            {
                if (_action != value)
                {
                    _action = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedActionOption));
                }
            }
        }

        public RestoreActionOption SelectedActionOption
        {
            get => ActionOptions.FirstOrDefault(o => o.Value == _action) ?? ActionOptions[0];
            set { if (value != null) Action = value.Value; }
        }

        private RestoreRowState _state;
        public RestoreRowState State
        {
            get => _state;
            private set
            {
                if (_state != value)
                {
                    _state = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StateLabel));
                    OnPropertyChanged(nameof(StateBrushKey));
                    OnPropertyChanged(nameof(IsInProgress));
                }
            }
        }

        public string StateLabel => MapStateLabel(_state);
        public string StateBrushKey => MapStateBrushKey(_state);
        public bool IsInProgress =>
            _state == RestoreRowState.Quarantining || _state == RestoreRowState.Cloning;

        private double? _percent;
        public double? PercentComplete
        {
            get => _percent;
            private set { _percent = value; OnPropertyChanged(); OnPropertyChanged(nameof(PercentDisplay)); }
        }
        public string PercentDisplay =>
            _percent.HasValue ? ((int)Math.Round(_percent.Value)) + "%" : string.Empty;

        private string _detail;
        public string Detail
        {
            get => _detail;
            private set { _detail = value ?? string.Empty; OnPropertyChanged(); }
        }

        private string _errorCode = string.Empty;
        public string ErrorCode
        {
            get => _errorCode;
            private set { _errorCode = value ?? string.Empty; OnPropertyChanged(); }
        }

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            private set { _errorMessage = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(HasError)); }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        private string _quarantinePath = string.Empty;
        public string QuarantinePath
        {
            get => _quarantinePath;
            private set { _quarantinePath = value ?? string.Empty; OnPropertyChanged(); OnPropertyChanged(nameof(HasQuarantinePath)); }
        }

        public bool HasQuarantinePath => !string.IsNullOrEmpty(_quarantinePath);

        public void MarkPending()
        {
            if (!IsSelectable) return;
            State = RestoreRowState.Pending;
            PercentComplete = null;
            Detail = "Queued…";
            ErrorCode = string.Empty;
            ErrorMessage = string.Empty;
        }

        public void Apply(RestoreRowUpdate update)
        {
            State = update.State;
            if (update.PercentComplete.HasValue) PercentComplete = update.PercentComplete;
            if (!string.IsNullOrEmpty(update.Detail)) Detail = update.Detail;
            if (!string.IsNullOrEmpty(update.QuarantinePath)) QuarantinePath = update.QuarantinePath;
            if (!string.IsNullOrEmpty(update.ErrorCode)) ErrorCode = update.ErrorCode;
            if (!string.IsNullOrEmpty(update.ErrorMessage)) ErrorMessage = update.ErrorMessage;
        }

        private static RestoreRowState MapInitialState(RestoreClassification c) => c switch
        {
            RestoreClassification.AlreadyCloned => RestoreRowState.AlreadyCloned,
            RestoreClassification.UnsafeExisting => RestoreRowState.UnsafeExisting,
            _ => RestoreRowState.Idle
        };

        private static RestoreAction DefaultActionFor(RestoreClassification c) => c switch
        {
            RestoreClassification.ConflictNonEmpty => RestoreAction.QuarantineAndClone,
            _ => RestoreAction.Clone
        };

        private static string MapClassificationLabel(RestoreClassification c) => c switch
        {
            RestoreClassification.Missing => "Missing",
            RestoreClassification.MissingNeedsUrl => "Missing — needs URL",
            RestoreClassification.EmptyFolder => "Empty folder",
            RestoreClassification.ConflictNonEmpty => "Conflict",
            RestoreClassification.AlreadyCloned => "Already cloned",
            RestoreClassification.UnsafeExisting => "Unsafe — manual review",
            _ => c.ToString()
        };

        private static string MapStateLabel(RestoreRowState s) => s switch
        {
            RestoreRowState.Idle => "Ready",
            RestoreRowState.Pending => "Pending",
            RestoreRowState.Quarantining => "Quarantining",
            RestoreRowState.Cloning => "Cloning",
            RestoreRowState.Done => "Done",
            RestoreRowState.Failed => "Failed",
            RestoreRowState.Skipped => "Skipped",
            RestoreRowState.AlreadyCloned => "Already cloned",
            RestoreRowState.UnsafeExisting => "Unsafe",
            _ => s.ToString()
        };

        // Resource keys looked up by the converter in XAML.
        private static string MapStateBrushKey(RestoreRowState s) => s switch
        {
            RestoreRowState.Done => "SuccessBrush",
            RestoreRowState.Failed => "CriticalBrush",
            RestoreRowState.Quarantining => "SystemAccentColorBrush",
            RestoreRowState.Cloning => "SystemAccentColorBrush",
            RestoreRowState.Skipped => "WarningBrush",
            RestoreRowState.AlreadyCloned => "SecondaryTextBrush",
            RestoreRowState.UnsafeExisting => "WarningBrush",
            _ => "SecondaryTextBrush"
        };

        // Mirrors the static check on RestoreOrchestrator / AsyncCloneService /
        // RestoreProjectsViewModel. Kept private static to avoid coupling row VM
        // to Infrastructure types.
        internal static bool UrlCarriesCredentials(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                if (string.IsNullOrEmpty(uri.UserInfo)) return false;
                if (string.Equals(uri.Scheme, "ssh", StringComparison.OrdinalIgnoreCase))
                    return uri.UserInfo.Contains(':');
                return true;
            }
            return false;
        }

        /// <summary>
        /// Lightweight syntactic check that the URL looks like one of the
        /// forms <c>git clone</c> accepts: <c>https://…</c>, <c>http://…</c>,
        /// <c>ssh://…</c>, <c>git://…</c>, <c>file://…</c>, or scp-like
        /// <c>user@host:owner/repo</c>. Not a guarantee that the remote
        /// exists — the clone itself is the final validation.
        /// </summary>
        internal static bool LooksLikeSupportedUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var s = uri.Scheme;
                return s == "https" || s == "http" || s == "ssh"
                    || s == "git" || s == "file";
            }

            // scp-like: at least one '@' followed by 'host:' followed by something.
            var at = url.IndexOf('@');
            if (at <= 0) return false;
            var colon = url.IndexOf(':', at);
            return colon > at + 1 && colon < url.Length - 1;
        }
    }

    public sealed class RestoreActionOption
    {
        public RestoreActionOption(RestoreAction value, string label)
        {
            Value = value;
            Label = label;
        }
        public RestoreAction Value { get; }
        public string Label { get; }
        public override string ToString() => Label;
    }
}
