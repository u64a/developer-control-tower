using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Desktop.ViewModels
{
    /// <summary>
    /// View-model for the Restore Projects dialog. Coordinates the
    /// <see cref="RestoreInputBuilder"/> and <see cref="IMissingProjectScanner"/>
    /// (scan phase) and the <see cref="IRestoreOrchestrator"/> (batch phase)
    /// on a background thread while updating per-row state on the UI thread.
    /// </summary>
    public sealed class RestoreProjectsViewModel : ObservableObject
    {
        private readonly IPortfolioProvider _portfolioProvider;
        private readonly RestoreInputBuilder _inputBuilder;
        private readonly IMissingProjectScanner _scanner;
        private readonly IRestoreOrchestrator _orchestrator;
        private readonly Dispatcher _dispatcher;

        private CancellationTokenSource _restoreCts;
        private List<RestoreRowViewModel> _batch = new List<RestoreRowViewModel>();

        /// <summary>
        /// True if anything in this dialog's lifetime changed portfolio.yml
        /// or the on-disk state of a project the main view cares about
        /// (origin URL caching, successful clone). Settings reads this to
        /// decide whether to refresh the host project list after close.
        /// </summary>
        public bool PortfolioMutated { get; private set; }

        /// <summary>
        /// Asks the user to confirm moving existing folders into quarantine.
        /// The dialog replaces this so the prompt is owned by its window.
        /// </summary>
        public Func<string, bool> ConfirmQuarantine { get; set; } = message =>
            MessageBox.Show(message, "Restore from Git", MessageBoxButton.OKCancel, MessageBoxImage.Warning)
                == MessageBoxResult.OK;

        public RestoreProjectsViewModel(
            IPortfolioProvider portfolioProvider,
            RestoreInputBuilder inputBuilder,
            IMissingProjectScanner scanner,
            IRestoreOrchestrator orchestrator)
        {
            _portfolioProvider = portfolioProvider ?? throw new ArgumentNullException(nameof(portfolioProvider));
            _inputBuilder = inputBuilder ?? throw new ArgumentNullException(nameof(inputBuilder));
            _scanner = scanner ?? throw new ArgumentNullException(nameof(scanner));
            _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
            _dispatcher = Dispatcher.CurrentDispatcher;

            Rows = new ObservableCollection<RestoreRowViewModel>();
            RowsView = CollectionViewSource.GetDefaultView(Rows);
            RowsView.Filter = item =>
                _showAlreadyCloned
                || !(item is RestoreRowViewModel row)
                || row.Classification != RestoreClassification.AlreadyCloned;
        }

        public ObservableCollection<RestoreRowViewModel> Rows { get; }

        /// <summary>Filtered view the grid binds to; hides already-cloned rows by default.</summary>
        public ICollectionView RowsView { get; }

        private IEnumerable<RestoreRowViewModel> VisibleRows => RowsView.Cast<RestoreRowViewModel>();

        private bool _showAlreadyCloned;
        public bool ShowAlreadyCloned
        {
            get => _showAlreadyCloned;
            set
            {
                if (_showAlreadyCloned == value) return;
                _showAlreadyCloned = value;
                OnPropertyChanged();
                RowsView.Refresh();
                RefreshDerivedState();
            }
        }

        public int AlreadyClonedCount => Rows.Count(r => r.Classification == RestoreClassification.AlreadyCloned);
        public bool HasAlreadyCloned => AlreadyClonedCount > 0;
        public string ShowAlreadyClonedLabel => "Show already cloned (" + AlreadyClonedCount + ")";

        public bool HasNoRows => !IsScanning && !VisibleRows.Any();

        public string EmptyHeading => Rows.Count == 0
            ? "No local projects in the portfolio"
            : "Nothing needs restoring";

        public string EmptyHint => Rows.Count == 0
            ? "Restore re-clones registered projects whose local folder has gone. Register a project first."
            : "All " + Rows.Count + " local project(s) are already cloned where the portfolio expects them.";

        private bool _isScanning;
        public bool IsScanning
        {
            get => _isScanning;
            private set { if (Set(ref _isScanning, value)) RefreshDerivedState(); }
        }

        private bool _isRestoring;
        public bool IsRestoring
        {
            get => _isRestoring;
            private set { if (Set(ref _isRestoring, value)) RefreshDerivedState(); }
        }

        public bool IsBusy => IsScanning || IsRestoring;
        public bool CanScan => !IsBusy;
        public bool CanRestore => !IsBusy && Rows.Any(r => r.IsSelected && r.IsSelectable);
        public bool CanCancel => IsRestoring;
        public bool CanClose => !IsRestoring;

        public string RestoreButtonText
        {
            get
            {
                var count = Rows.Count(r => r.IsSelected && r.IsSelectable);
                return count == 0 ? "Restore selected" : "Restore selected (" + count + ")";
            }
        }

        /// <summary>
        /// Tri-state backing for the header checkbox: true when every
        /// visible selectable row is ticked, false when none are, null when mixed.
        /// </summary>
        public bool? AreAllSelected
        {
            get
            {
                var selectable = VisibleRows.Where(r => r.IsSelectable).ToList();
                var selected = selectable.Count(r => r.IsSelected);
                if (selected == 0) return false;
                return selected == selectable.Count ? true : (bool?)null;
            }
            set
            {
                // A click on the indeterminate box means "select everything".
                var select = value != false;
                foreach (var row in VisibleRows.Where(r => r.IsSelectable).ToList())
                {
                    row.IsSelected = select;
                }
                RefreshDerivedState();
            }
        }

        /// <summary>Overall batch progress, 0–100. Meaningful only while restoring.</summary>
        private double _overallProgress;
        public double OverallProgress
        {
            get => _overallProgress;
            private set => Set(ref _overallProgress, value);
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set => Set(ref _statusText, value);
        }

        private string _summaryText = string.Empty;
        public string SummaryText
        {
            get => _summaryText;
            private set => Set(ref _summaryText, value);
        }

        public async Task ScanAsync()
        {
            if (IsBusy) return;

            IsScanning = true;
            StatusText = string.Empty;
            SummaryText = "Checking the portfolio against local folders…";
            foreach (var existing in Rows)
            {
                existing.PropertyChanged -= OnRowPropertyChanged;
            }
            Rows.Clear();
            try
            {
                var (inputs, candidates) = await Task.Run(async () =>
                {
                    var built = _inputBuilder.Build();
                    var scanned = await _scanner.ScanAsync(built, CancellationToken.None).ConfigureAwait(false);
                    return (built, scanned);
                }).ConfigureAwait(true);

                AppLogger.Info("Restore", $"Scan complete: {candidates.Count} candidate(s) from {inputs.Count} local project(s).");

                var cachesUpdated = WriteCacheUpdates(RestoreInputBuilder.SelectOriginsToCache(inputs, candidates));
                if (cachesUpdated > 0)
                {
                    PortfolioMutated = true;
                    StatusText = $"Saved {cachesUpdated} origin URL(s) to portfolio.yml for future restores.";
                    AppLogger.Info("Restore", $"Wrote {cachesUpdated} origin URL(s) to portfolio.yml.");
                }

                foreach (var candidate in candidates
                    .OrderBy(SortRank)
                    .ThenBy(c => c.ProjectName, StringComparer.OrdinalIgnoreCase))
                {
                    var row = RestoreRowViewModel.FromCandidate(candidate);
                    // Pre-select what can be restored without any decision
                    // from the user. Conflicts move a folder, so they stay opt-in.
                    row.IsSelected = row.IsSelectable && row.Action == RestoreAction.Clone;
                    row.PropertyChanged += OnRowPropertyChanged;
                    Rows.Add(row);
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Restore", "Scan failed: " + ex.Message);
                StatusText = "Scan failed: " + ex.Message;
            }
            finally
            {
                IsScanning = false;
            }
        }

        public async Task RestoreSelectedAsync()
        {
            if (IsBusy) return;
            var selected = Rows.Where(r => r.IsSelected && r.IsSelectable).ToList();
            if (selected.Count == 0)
            {
                StatusText = "Select at least one project to restore.";
                return;
            }

            var willQuarantine = selected.Count(r => r.Action == RestoreAction.QuarantineAndClone);
            if (willQuarantine > 0)
            {
                var msg = willQuarantine == 1
                    ? "1 project will have its existing folder moved into quarantine before cloning. Continue?"
                    : willQuarantine + " projects will have their existing folders moved into quarantine before cloning. Continue?";
                if (!ConfirmQuarantine(msg)) return;
            }

            _restoreCts = new CancellationTokenSource();
            var token = _restoreCts.Token;
            _batch = selected;
            OverallProgress = 0;
            foreach (var row in Rows) row.IsLocked = true;
            IsRestoring = true;
            StatusText = "Restoring " + selected.Count + " project(s)…";
            try
            {
                foreach (var row in selected) row.MarkPending();

                // The orchestrator clones from the candidate's URL, so fold
                // in whatever the user typed or corrected.
                var selections = selected
                    .Select(row => new RestoreSelection(row.Candidate with { RemoteUrl = row.OriginUrl }, row.Action))
                    .ToList();

                // Project ids are unique in a healthy portfolio; if they are
                // not, the first row wins rather than the whole batch failing.
                var byId = new Dictionary<string, RestoreRowViewModel>(StringComparer.Ordinal);
                foreach (var row in selected) byId.TryAdd(row.ProjectId, row);

                IProgress<RestoreRowUpdate> progress = new DispatcherProgress<RestoreRowUpdate>(_dispatcher, update =>
                {
                    if (byId.TryGetValue(update.ProjectId, out var row))
                    {
                        row.Apply(update);
                        UpdateOverallProgress();
                    }
                });

                AppLogger.Info("Restore", $"Restore starting for {selections.Count} row(s).");
                await Task.Run(() => _orchestrator.RestoreAsync(selections, progress, token)).ConfigureAwait(true);

                // Progress reports are posted to the dispatcher; let the
                // queued ones land before reading final row states.
                await _dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

                var toCache = new Dictionary<string, string>(StringComparer.Ordinal);
                for (int i = 0; i < selected.Count; i++)
                {
                    var url = selections[i].Candidate.RemoteUrl;
                    if (selected[i].State == RestoreRowState.Done && !UrlSanitizer.HasCredentials(url))
                    {
                        toCache[selected[i].ProjectId] = url;
                    }
                }

                if (selected.Any(r => r.State == RestoreRowState.Done))
                {
                    PortfolioMutated = true;
                }
                WriteCacheUpdates(toCache);

                StatusText = BuildCompletionStatus(token.IsCancellationRequested);
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
                foreach (var row in Rows)
                {
                    row.IsLocked = false;
                    // Anything still queued never started (e.g. the wait for
                    // the git lock was cancelled); return it to idle.
                    row.ResetIfPending();
                }
                _restoreCts?.Dispose();
                _restoreCts = null;
                IsRestoring = false;
            }
        }

        public void CancelRestore()
        {
            try
            {
                _restoreCts?.Cancel();
                StatusText = "Cancelling — waiting for git to stop…";
            }
            catch (ObjectDisposedException) { }
        }

        /// <summary>Called when the user tries to close the window mid-restore.</summary>
        public void NotifyCloseBlocked()
        {
            StatusText = "A restore is still running. Cancel it first, or wait for it to finish.";
        }

        private void OnRowPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(RestoreRowViewModel.IsSelected)
                || e.PropertyName == nameof(RestoreRowViewModel.IsSelectable)
                || e.PropertyName == nameof(RestoreRowViewModel.State))
            {
                RefreshDerivedState();
            }
        }

        private void RefreshDerivedState()
        {
            OnPropertyChanged(nameof(IsBusy));
            OnPropertyChanged(nameof(CanScan));
            OnPropertyChanged(nameof(CanRestore));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(CanClose));
            OnPropertyChanged(nameof(RestoreButtonText));
            OnPropertyChanged(nameof(AreAllSelected));
            OnPropertyChanged(nameof(HasNoRows));
            OnPropertyChanged(nameof(EmptyHeading));
            OnPropertyChanged(nameof(EmptyHint));
            OnPropertyChanged(nameof(AlreadyClonedCount));
            OnPropertyChanged(nameof(HasAlreadyCloned));
            OnPropertyChanged(nameof(ShowAlreadyClonedLabel));
            if (!IsScanning) UpdateSummary();
        }

        private void UpdateOverallProgress()
        {
            if (_batch.Count == 0) return;
            double done = 0;
            foreach (var row in _batch)
            {
                if (row.IsFinished) done += 1;
                else if (row.State == RestoreRowState.Cloning && row.PercentComplete.HasValue)
                    done += Math.Min(row.PercentComplete.Value, 100) / 100.0 * 0.95;
            }
            OverallProgress = done / _batch.Count * 100.0;
        }

        private int WriteCacheUpdates(IReadOnlyDictionary<string, string> toCache)
        {
            if (toCache == null || toCache.Count == 0) return 0;
            try
            {
                var current = _portfolioProvider.LoadPortfolio();
                if (current?.Projects == null) return 0;
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

        private void UpdateSummary()
        {
            var parts = new List<string>();
            void Add(int count, string label)
            {
                if (count > 0) parts.Add(count + " " + label);
            }

            Add(Rows.Count(r => r.State == RestoreRowState.Done), "restored");
            Add(Rows.Count(r => r.State == RestoreRowState.Failed), "failed");
            Add(Rows.Count(r => r.IsRestorable && r.State == RestoreRowState.Idle && r.HasValidUrl), "ready to restore");
            Add(Rows.Count(r => r.IsRestorable && r.State == RestoreRowState.Idle && !r.HasValidUrl), "need a URL");
            Add(Rows.Count(r => r.Classification == RestoreClassification.UnsafeExisting), "need review");
            Add(AlreadyClonedCount, "already cloned");
            SummaryText = string.Join("  ·  ", parts);
        }

        private string BuildCompletionStatus(bool cancelled)
        {
            int done = _batch.Count(r => r.State == RestoreRowState.Done);
            int failed = _batch.Count(r => r.State == RestoreRowState.Failed);
            int skipped = _batch.Count(r => r.State == RestoreRowState.Skipped);

            var text = (cancelled ? "Cancelled. " : string.Empty) + "Restored " + done + " of " + _batch.Count + ".";
            if (failed > 0) text += " " + failed + " failed — check the error on each row, then restore again.";
            if (skipped > 0) text += " " + skipped + " skipped.";
            return text;
        }

        private static int SortRank(RestoreCandidate c) => c.Classification switch
        {
            RestoreClassification.Missing => 0,
            RestoreClassification.EmptyFolder => 0,
            RestoreClassification.MissingNeedsUrl => 1,
            RestoreClassification.ConflictNonEmpty => 2,
            RestoreClassification.UnsafeExisting => 3,
            _ => 4
        };

        /// <summary>
        /// IProgress&lt;T&gt; that marshals reports onto the UI dispatcher so
        /// the orchestrator (running on a thread-pool task) can safely
        /// update row view-models.
        /// </summary>
        private sealed class DispatcherProgress<T> : IProgress<T>
        {
            private readonly Dispatcher _dispatcher;
            private readonly Action<T> _onReport;
            public DispatcherProgress(Dispatcher dispatcher, Action<T> onReport)
            {
                _dispatcher = dispatcher;
                _onReport = onReport;
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
    /// Tracks user selection, the origin URL to clone from, the chosen
    /// <see cref="RestoreAction"/>, and live state from the orchestrator.
    /// </summary>
    public sealed class RestoreRowViewModel : ObservableObject
    {
        private RestoreRowViewModel(RestoreCandidate candidate)
        {
            Candidate = candidate;
            _state = MapInitialState(candidate.Classification);
            _detail = candidate.Detail ?? string.Empty;
            _action = candidate.Classification == RestoreClassification.ConflictNonEmpty
                ? RestoreAction.QuarantineAndClone
                : RestoreAction.Clone;
            _originUrl = (candidate.RemoteUrl ?? string.Empty).Trim();
        }

        public static RestoreRowViewModel FromCandidate(RestoreCandidate candidate)
            => new RestoreRowViewModel(candidate);

        public RestoreCandidate Candidate { get; }

        public string ProjectId => Candidate.ProjectId;
        public string ProjectName => Candidate.ProjectName;
        public string ExpectedPath => Candidate.ExpectedPath;
        public RestoreClassification Classification => Candidate.Classification;

        /// <summary>True for rows whose folder can be (re)cloned at all.</summary>
        public bool IsRestorable =>
            Classification == RestoreClassification.Missing
            || Classification == RestoreClassification.MissingNeedsUrl
            || Classification == RestoreClassification.EmptyFolder
            || Classification == RestoreClassification.ConflictNonEmpty;

        /// <summary>
        /// The URL this row will clone from. Pre-filled from the best known
        /// source and editable, so a missing or stale origin can be fixed
        /// in place.
        /// </summary>
        private string _originUrl;
        public string OriginUrl
        {
            get => _originUrl;
            set
            {
                var trimmed = (value ?? string.Empty).Trim();
                if (_originUrl == trimmed) return;

                var wasValid = HasValidUrl;
                _originUrl = trimmed;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasValidUrl));
                OnPropertyChanged(nameof(UrlHint));
                OnPropertyChanged(nameof(IsUrlHintError));
                OnPropertyChanged(nameof(HasUrlHint));
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(StateBrushKey));
                OnPropertyChanged(nameof(IsSelectable));
                OnPropertyChanged(nameof(CanChangeSelection));

                if (!IsSelectable) IsSelected = false;
                // Supplying the missing URL is a clear "restore this one".
                else if (!wasValid && Action == RestoreAction.Clone) IsSelected = true;
            }
        }

        public bool HasValidUrl =>
            RestoreInputBuilder.LooksLikeCloneUrl(_originUrl) && !UrlSanitizer.HasCredentials(_originUrl);

        public bool ShowUrlEditor => IsRestorable && _state != RestoreRowState.Done;
        public bool CanEditUrl => ShowUrlEditor && !_isLocked;

        private bool IsUrlEdited =>
            !string.Equals(_originUrl, (Candidate.RemoteUrl ?? string.Empty).Trim(), StringComparison.Ordinal);

        public string UrlHint
        {
            get
            {
                if (!IsRestorable) return string.Empty;
                if (string.IsNullOrEmpty(_originUrl)) return "No origin recorded — paste the clone URL.";
                if (UrlSanitizer.HasCredentials(_originUrl))
                    return "Remove the embedded credentials — Git Credential Manager or SSH-agent signs in.";
                if (!RestoreInputBuilder.LooksLikeCloneUrl(_originUrl))
                    return "Not a clone URL. Use https://… or git@host:owner/repo.";
                if (IsUrlEdited) return "Entered manually";
                return Candidate.RemoteUrlSource switch
                {
                    RestoreUrlSource.PortfolioCache => "From portfolio.yml",
                    RestoreUrlSource.ProjectMetadata => "From project metadata",
                    RestoreUrlSource.LastScan => "From the last repository scan",
                    RestoreUrlSource.LaunchLink => "From the project's repository link — check before restoring",
                    _ => string.Empty
                };
            }
        }

        public bool HasUrlHint => !string.IsNullOrEmpty(UrlHint);
        public bool IsUrlHintError => IsRestorable && !string.IsNullOrEmpty(_originUrl) && !HasValidUrl;

        public bool IsSelectable => IsRestorable && _state != RestoreRowState.Done && HasValidUrl;

        public bool ShowActionPicker =>
            Classification == RestoreClassification.ConflictNonEmpty && _state != RestoreRowState.Done;

        public IReadOnlyList<RestoreActionOption> ActionOptions { get; } = new[]
        {
            new RestoreActionOption(RestoreAction.QuarantineAndClone, "Quarantine & clone"),
            new RestoreActionOption(RestoreAction.Skip, "Skip"),
        };

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        /// <summary>Set by the dialog while a batch is running so inputs can't change under it.</summary>
        private bool _isLocked;
        public bool IsLocked
        {
            get => _isLocked;
            set
            {
                if (Set(ref _isLocked, value))
                {
                    OnPropertyChanged(nameof(CanEditUrl));
                    OnPropertyChanged(nameof(CanChangeSelection));
                }
            }
        }

        public bool CanChangeSelection => IsSelectable && !_isLocked;

        private RestoreAction _action;
        public RestoreAction Action
        {
            get => _action;
            set
            {
                if (Set(ref _action, value))
                {
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
                if (!Set(ref _state, value)) return;
                OnPropertyChanged(nameof(StateLabel));
                OnPropertyChanged(nameof(StateBrushKey));
                OnPropertyChanged(nameof(IsCloning));
                OnPropertyChanged(nameof(PercentDisplay));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(CanEditUrl));
                OnPropertyChanged(nameof(ShowUrlEditor));
                OnPropertyChanged(nameof(ShowActionPicker));
                OnPropertyChanged(nameof(IsSelectable));
                OnPropertyChanged(nameof(CanChangeSelection));
                if (!IsSelectable) IsSelected = false;
            }
        }

        public string StateLabel
        {
            get
            {
                switch (_state)
                {
                    case RestoreRowState.Pending: return "Queued";
                    case RestoreRowState.Quarantining: return "Moving folder";
                    case RestoreRowState.Cloning: return "Cloning";
                    case RestoreRowState.Done: return "Restored";
                    case RestoreRowState.Failed: return "Failed";
                    case RestoreRowState.Skipped: return "Skipped";
                    case RestoreRowState.AlreadyCloned: return "Already cloned";
                    case RestoreRowState.UnsafeExisting: return "Needs review";
                }

                if (!HasValidUrl) return "Needs URL";
                return Classification switch
                {
                    RestoreClassification.EmptyFolder => "Empty folder",
                    RestoreClassification.ConflictNonEmpty => "Folder has files",
                    _ => "Missing"
                };
            }
        }

        // Resource keys looked up by the converter in XAML.
        public string StateBrushKey
        {
            get
            {
                switch (_state)
                {
                    case RestoreRowState.Done:
                    case RestoreRowState.AlreadyCloned: return "SuccessBrush";
                    case RestoreRowState.Failed: return "CriticalBrush";
                    case RestoreRowState.Pending:
                    case RestoreRowState.Quarantining:
                    case RestoreRowState.Cloning: return "AccentBrush";
                    case RestoreRowState.Skipped:
                    case RestoreRowState.UnsafeExisting: return "WarningBrush";
                }

                return !HasValidUrl || Classification == RestoreClassification.ConflictNonEmpty
                    ? "WarningBrush"
                    : "AccentBrush";
            }
        }

        public bool IsCloning => _state == RestoreRowState.Cloning;

        /// <summary>True once the orchestrator has reached a terminal state for this row.</summary>
        public bool IsFinished =>
            _state == RestoreRowState.Done || _state == RestoreRowState.Failed
            || _state == RestoreRowState.Skipped || _state == RestoreRowState.AlreadyCloned
            || _state == RestoreRowState.UnsafeExisting;

        /// <summary>Rows there is nothing to do for are drawn muted.</summary>
        public bool IsDimmed => !IsRestorable;

        private double? _percent;
        public double? PercentComplete
        {
            get => _percent;
            private set
            {
                _percent = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PercentDisplay));
                OnPropertyChanged(nameof(PercentValue));
                OnPropertyChanged(nameof(IsPercentUnknown));
            }
        }

        public string PercentDisplay =>
            IsCloning && _percent.HasValue ? ((int)Math.Round(_percent.Value)) + "%" : string.Empty;
        public double PercentValue => _percent ?? 0;
        public bool IsPercentUnknown => !_percent.HasValue;

        private string _detail;
        public string Detail
        {
            get => _detail;
            private set => Set(ref _detail, value ?? string.Empty);
        }

        private string _errorMessage = string.Empty;
        public string ErrorMessage
        {
            get => _errorMessage;
            private set
            {
                if (Set(ref _errorMessage, value ?? string.Empty)) OnPropertyChanged(nameof(HasError));
            }
        }

        public bool HasError => !string.IsNullOrEmpty(_errorMessage);

        private string _quarantinePath = string.Empty;
        public string QuarantinePath
        {
            get => _quarantinePath;
            private set
            {
                if (Set(ref _quarantinePath, value ?? string.Empty)) OnPropertyChanged(nameof(HasQuarantinePath));
            }
        }

        public bool HasQuarantinePath => !string.IsNullOrEmpty(_quarantinePath);

        public void MarkPending()
        {
            if (!IsSelectable) return;
            PercentComplete = null;
            Detail = "Waiting for its turn…";
            ErrorMessage = string.Empty;
            State = RestoreRowState.Pending;
        }

        public void ResetIfPending()
        {
            if (_state != RestoreRowState.Pending) return;
            Detail = Candidate.Detail ?? string.Empty;
            State = RestoreRowState.Idle;
        }

        public void Apply(RestoreRowUpdate update)
        {
            if (update.PercentComplete.HasValue) PercentComplete = update.PercentComplete;
            if (!string.IsNullOrEmpty(update.QuarantinePath)) QuarantinePath = update.QuarantinePath;
            if (update.State == RestoreRowState.Failed)
            {
                // The failure message is the detail; don't leave the last
                // git progress line sitting above it.
                Detail = string.Empty;
                ErrorMessage = string.IsNullOrEmpty(update.ErrorMessage) ? "Restore failed." : update.ErrorMessage;
            }
            else if (!string.IsNullOrEmpty(update.Detail))
            {
                Detail = update.Detail;
            }
            State = update.State;
        }

        private static RestoreRowState MapInitialState(RestoreClassification c) => c switch
        {
            RestoreClassification.AlreadyCloned => RestoreRowState.AlreadyCloned,
            RestoreClassification.UnsafeExisting => RestoreRowState.UnsafeExisting,
            _ => RestoreRowState.Idle
        };
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
