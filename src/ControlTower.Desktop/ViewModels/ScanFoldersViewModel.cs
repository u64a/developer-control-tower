using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Desktop.ViewModels
{
    /// <summary>
    /// View-model for the Scan Folders dialog. Coordinates root selection,
    /// the <see cref="IRepoScanService"/> on a background task, and the
    /// per-row registration sequence. Marshals scan progress onto the UI
    /// dispatcher.
    /// </summary>
    public sealed class ScanFoldersViewModel : ObservableObject
    {
        private const int MaxRoots = 4;

        private readonly IRepoScanService _scanService;
        private readonly IProjectRegistrationService _registrationService;
        private readonly WorkspaceProfileManager _profileManager;
        private readonly WorkspaceProfile _activeProfile;

        private CancellationTokenSource _scanCts;

        /// <summary>
        /// True if at least one row was successfully registered into the
        /// portfolio during this dialog's lifetime. Used by the host
        /// (Settings → Main) to decide whether to reload the project list
        /// after the dialog closes.
        /// </summary>
        public bool PortfolioMutated { get; private set; }

        /// <summary>
        /// True when registration changed the synced profile definitions or
        /// selected the machine-local All projects fallback. The host must
        /// rebuild its session because profile membership is captured when the
        /// service graph is constructed.
        /// </summary>
        public bool ProfileStateMutated { get; private set; }

        public ScanFoldersViewModel(
            IRepoScanService scanService,
            IProjectRegistrationService registrationService,
            WorkspaceProfileManager profileManager = null,
            WorkspaceProfile activeProfile = null)
        {
            _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
            _registrationService = registrationService ?? throw new ArgumentNullException(nameof(registrationService));
            _profileManager = profileManager;
            _activeProfile = activeProfile;

            Roots = new ObservableCollection<ScanFolderRootViewModel>();
            Roots.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(CanAddRoot));
                OnPropertyChanged(nameof(CanRemoveRoot));
                OnPropertyChanged(nameof(CanScan));
            };

            Rows = new ObservableCollection<ScanRowViewModel>();
            Rows.CollectionChanged += (_, __) =>
            {
                OnPropertyChanged(nameof(HasRows));
                OnPropertyChanged(nameof(HasNoRows));
                OnPropertyChanged(nameof(CanRegister));
            };
        }

        public ObservableCollection<ScanFolderRootViewModel> Roots { get; }

        public ObservableCollection<ScanRowViewModel> Rows { get; }

        public bool HasRows => Rows.Count > 0;
        public bool HasNoRows => Rows.Count == 0 && !IsScanning;

        private bool _isScanning;
        public bool IsScanning
        {
            get => _isScanning;
            private set
            {
                _isScanning = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanScan));
                OnPropertyChanged(nameof(CanCancel));
                OnPropertyChanged(nameof(CanClose));
                OnPropertyChanged(nameof(CanRegister));
                OnPropertyChanged(nameof(HasNoRows));
                OnPropertyChanged(nameof(CanAddRoot));
                OnPropertyChanged(nameof(CanRemoveRoot));
            }
        }

        private bool _isRegistering;
        public bool IsRegistering
        {
            get => _isRegistering;
            private set
            {
                _isRegistering = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBusy));
                OnPropertyChanged(nameof(CanScan));
                OnPropertyChanged(nameof(CanClose));
                OnPropertyChanged(nameof(CanRegister));
            }
        }

        public bool IsBusy => IsScanning || IsRegistering;

        public bool CanAddRoot => !IsBusy && Roots.Count < MaxRoots;
        public bool CanRemoveRoot => !IsBusy && Roots.Count > 0;
        public bool CanScan => !IsBusy && Roots.Count > 0;
        public bool CanCancel => IsScanning;
        public bool CanClose => !IsBusy;
        public bool CanRegister => !IsBusy && Rows.Any(r => r.IsSelected && r.IsSelectable);

        private string _progressText = string.Empty;
        public string ProgressText
        {
            get => _progressText;
            private set { _progressText = value ?? string.Empty; OnPropertyChanged(); }
        }

        private string _statusText = string.Empty;
        public string StatusText
        {
            get => _statusText;
            private set { _statusText = value ?? string.Empty; OnPropertyChanged(); }
        }

        public void AddRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            if (Roots.Count >= MaxRoots) return;
            var trimmed = path.Trim();
            // De-dup the root list itself — adding the same folder twice
            // just confuses the scan.
            foreach (var existing in Roots)
            {
                if (string.Equals(existing.Path, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            Roots.Add(new ScanFolderRootViewModel(trimmed));
        }

        public void RemoveRoot(ScanFolderRootViewModel root)
        {
            if (root == null) return;
            Roots.Remove(root);
        }

        public async Task ScanAsync()
        {
            if (IsBusy || Roots.Count == 0) return;

            IsScanning = true;
            StatusText = string.Empty;
            ProgressText = "Starting scan…";
            // Clear stale rows on rescan.
            Rows.Clear();
            _scanCts = new CancellationTokenSource();

            var rootPaths = Roots.Select(r => r.Path).ToList();
            var options = new ScanOptions();
            var progress = new DispatcherProgress<ScanProgressUpdate>(u =>
            {
                ProgressText = "Scanning " + u.RootPath + " — walked " + u.FoldersWalked
                    + " folders, found " + u.ReposFound + " repos."
                    + (string.IsNullOrEmpty(u.CurrentPath) ? string.Empty : "  At: " + u.CurrentPath);
            });

            try
            {
                AppLogger.Info("ScanFolders", $"Scan starting for {rootPaths.Count} root(s).");
                ScanResult result = await Task.Run(
                    () => _scanService.ScanAsync(rootPaths, options, progress, _scanCts.Token),
                    _scanCts.Token).ConfigureAwait(true);

                foreach (var candidate in result.Candidates)
                {
                    var row = ScanRowViewModel.FromCandidate(candidate);
                    row.PropertyChanged += OnRowPropertyChanged;
                    Rows.Add(row);
                }

                // Pre-select rows likely to be useful: clean working tree with
                // a non-credential origin, not a duplicate.
                foreach (var row in Rows)
                {
                    if (row.Candidate.Kind == RepoKind.WorkingTree
                        && row.Candidate.RemoteState == RemoteState.HasOrigin
                        && row.Candidate.DuplicateKind == DuplicateKind.None)
                    {
                        row.IsSelected = true;
                    }
                }

                ProgressText = "Scan complete: walked " + result.TotalFoldersWalked
                    + " folder(s), found " + result.Candidates.Count + " repo(s).";
                UpdateStatus(result);
                AppLogger.Info("ScanFolders", $"Scan complete: {result.Candidates.Count} candidate(s), {result.Issues.Count} issue(s).");
            }
            catch (OperationCanceledException)
            {
                ProgressText = "Scan cancelled.";
                StatusText = "Scan cancelled by user.";
                AppLogger.Info("ScanFolders", "Scan cancelled by user.");
            }
            catch (Exception ex)
            {
                ProgressText = "Scan failed: " + ex.Message;
                StatusText = ex.Message;
                AppLogger.Error("ScanFolders", "Scan failed: " + ex.Message);
            }
            finally
            {
                IsScanning = false;
                _scanCts?.Dispose();
                _scanCts = null;
                OnPropertyChanged(nameof(CanRegister));
            }
        }

        public void CancelScan()
        {
            try
            {
                _scanCts?.Cancel();
                ProgressText = "Cancelling…";
            }
            catch
            {
                // ignore
            }
        }

        public void SelectAllSelectable()
        {
            foreach (var row in Rows.Where(r => r.IsSelectable))
            {
                row.IsSelected = true;
            }
            OnPropertyChanged(nameof(CanRegister));
        }

        public void SelectNone()
        {
            foreach (var row in Rows)
            {
                row.IsSelected = false;
            }
            OnPropertyChanged(nameof(CanRegister));
        }

        public async Task RegisterSelectedAsync()
        {
            if (IsBusy) return;
            var selected = Rows.Where(r => r.IsSelected && r.IsSelectable).ToList();
            if (selected.Count == 0)
            {
                StatusText = "Select at least one row to register.";
                return;
            }

            IsRegistering = true;
            StatusText = "Registering…";
            int succeeded = 0;
            int failed = 0;
            int profileFailures = 0;

            try
            {
                // Sequential — we want stable per-row error reporting and
                // portfolio.yml writes are not concurrency-safe anyway.
                foreach (var row in selected)
                {
                    row.MarkPending();
                    var c = row.Candidate;
                    var request = new ProjectRegistrationRequest
                    {
                        ProjectId = c.SuggestedSlug,
                        DisplayName = c.FolderName,
                        LocalPath = c.FolderPath,
                        LifecycleState = "active",
                        RemoteUrl = c.DisplayOriginUrl,
                        AllowOverwrite = false
                    };

                    // Populate the host-specific launch URLs for the standard
                    // hosts so the launch buttons "just work" after register.
                    if (!string.IsNullOrEmpty(c.DisplayOriginUrl))
                    {
                        var identity = c.DedupeIdentity ?? string.Empty;
                        if (identity.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
                        {
                            request.GitHubUrl = c.DisplayOriginUrl;
                        }
                        else if (identity.StartsWith("dev.azure.com/", StringComparison.OrdinalIgnoreCase)
                                 || identity.IndexOf(".visualstudio.com/", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            request.AdoUrl = c.DisplayOriginUrl;
                        }
                    }

                    try
                    {
                        var result = await Task.Run(() => _registrationService.RegisterProject(request))
                            .ConfigureAwait(true);
                        if (result.Success)
                        {
                            succeeded++;
                            PortfolioMutated = true;

                            var detail = result.Message;
                            if (_profileManager != null &&
                                _activeProfile != null &&
                                !_activeProfile.IsSynthetic)
                            {
                                try
                                {
                                    if (_profileManager.AppendProjectToActive(
                                        _activeProfile,
                                        result.ProjectId))
                                    {
                                        ProfileStateMutated = true;
                                    }
                                }
                                catch (Exception profileException)
                                {
                                    profileFailures++;
                                    var fallbackDetail = string.Empty;
                                    try
                                    {
                                        _profileManager.SelectSyntheticFallback();
                                        ProfileStateMutated = true;
                                        fallbackDetail = " All projects will be shown after this dialog closes.";
                                    }
                                    catch (Exception fallbackException)
                                    {
                                        fallbackDetail =
                                            " The project remains in the canonical portfolio and can be added through Manage profiles. " +
                                            "The All projects fallback also failed: " +
                                            fallbackException.Message;
                                    }

                                    detail += " Profile membership could not be saved: " +
                                        profileException.Message + fallbackDetail;
                                    AppLogger.Warn(
                                        "ScanFolders",
                                        "Registered '" + result.ProjectId +
                                        "' but profile membership failed: " +
                                        profileException.Message);
                                }
                            }

                            row.MarkRegistered(detail);
                        }
                        else
                        {
                            row.MarkFailed(result.Message);
                            failed++;
                        }
                    }
                    catch (Exception ex)
                    {
                        row.MarkFailed(ex.Message);
                        failed++;
                        AppLogger.Error("ScanFolders", "Registration threw for '" + c.SuggestedSlug + "': " + ex.Message);
                    }
                }

                StatusText = succeeded + " registered" +
                    (failed > 0 ? ", " + failed + " failed" : string.Empty) +
                    (profileFailures > 0
                        ? ", " + profileFailures + " profile update" +
                          (profileFailures == 1 ? string.Empty : "s") +
                          " failed"
                        : string.Empty) +
                    ((failed > 0 || profileFailures > 0)
                        ? " (see Detail column)."
                        : ".");
                AppLogger.Info("ScanFolders", "Registration complete. " + StatusText);
            }
            finally
            {
                IsRegistering = false;
                OnPropertyChanged(nameof(CanRegister));
            }
        }

        private void OnRowPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ScanRowViewModel.IsSelected))
            {
                OnPropertyChanged(nameof(CanRegister));
            }
        }

        private void UpdateStatus(ScanResult result)
        {
            int newRows = Rows.Count(r => r.Candidate.DuplicateKind == DuplicateKind.None);
            int duplicates = Rows.Count - newRows;
            int issues = result.Issues?.Count ?? 0;
            StatusText = newRows + " new, " + duplicates + " duplicates, " + issues + " issues.";
        }

        /// <summary>
        /// IProgress&lt;T&gt; that marshals reports onto the UI dispatcher
        /// captured at construction time.
        /// </summary>
        private sealed class DispatcherProgress<T> : IProgress<T>
        {
            private readonly System.Windows.Threading.Dispatcher _dispatcher;
            private readonly Action<T> _onReport;
            public DispatcherProgress(Action<T> onReport)
            {
                _onReport = onReport;
                _dispatcher = Application.Current?.Dispatcher
                    ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
            }
            public void Report(T value)
            {
                if (_dispatcher.CheckAccess()) _onReport(value);
                else _dispatcher.BeginInvoke(_onReport, value);
            }
        }
    }

    /// <summary>Single scan root path.</summary>
    public sealed class ScanFolderRootViewModel
    {
        public ScanFolderRootViewModel(string path)
        {
            Path = path ?? string.Empty;
        }

        public string Path { get; }
        public override string ToString() => Path;
    }

    /// <summary>
    /// Row-level view-model for a single <see cref="ScanCandidate"/>.
    /// </summary>
    public sealed class ScanRowViewModel : ObservableObject
    {
        private ScanRowViewModel(ScanCandidate candidate)
        {
            Candidate = candidate;
            _detail = candidate.Detail ?? string.Empty;
        }

        public static ScanRowViewModel FromCandidate(ScanCandidate candidate)
            => new ScanRowViewModel(candidate);

        public ScanCandidate Candidate { get; }

        public string FolderName => Candidate.FolderName;
        public string FolderPath => Candidate.FolderPath;
        public string SuggestedSlug => Candidate.SuggestedSlug;
        public string DisplayOriginUrl => Candidate.DisplayOriginUrl;
        public string Branch => Candidate.Branch;

        public string StateLabel => BuildStateLabel(Candidate);

        public bool IsDuplicate => Candidate.DuplicateKind != DuplicateKind.None;

        public bool IsSelectable
        {
            get
            {
                if (_outcome == RegistrationOutcomeKind.Succeeded) return false;
                if (Candidate.DuplicateKind != DuplicateKind.None) return false;
                if (Candidate.RemoteState == RemoteState.OriginHasCredentials) return false;
                if (Candidate.Kind == RepoKind.Other) return false;
                return true;
            }
        }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged();
                }
            }
        }

        private string _detail;
        public string Detail
        {
            get => _detail;
            private set { _detail = value ?? string.Empty; OnPropertyChanged(); }
        }

        private RegistrationOutcomeKind _outcome = RegistrationOutcomeKind.None;
        private string _outcomeMessage = string.Empty;
        public string OutcomeMessage
        {
            get => _outcomeMessage;
            private set
            {
                _outcomeMessage = value ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasOutcome));
            }
        }
        public bool HasOutcome => !string.IsNullOrEmpty(_outcomeMessage);

        public void MarkPending()
        {
            _outcome = RegistrationOutcomeKind.Pending;
            OutcomeMessage = "Registering…";
        }

        public void MarkRegistered(string message)
        {
            _outcome = RegistrationOutcomeKind.Succeeded;
            OutcomeMessage = string.IsNullOrWhiteSpace(message) ? "Registered." : "Registered: " + message;
            OnPropertyChanged(nameof(IsSelectable));
        }

        public void MarkFailed(string message)
        {
            _outcome = RegistrationOutcomeKind.Failed;
            OutcomeMessage = "Failed: " + (message ?? "unknown error");
        }

        private static string BuildStateLabel(ScanCandidate c)
        {
            string kindLabel;
            switch (c.Kind)
            {
                case RepoKind.WorkingTree: kindLabel = "Working tree"; break;
                case RepoKind.BareRepo: kindLabel = "Bare"; break;
                case RepoKind.WorktreePointer: kindLabel = "Worktree"; break;
                case RepoKind.Submodule: kindLabel = "Submodule"; break;
                default: kindLabel = "Other"; break;
            }

            switch (c.RemoteState)
            {
                case RemoteState.NoRemote: return kindLabel + " · no remote";
                case RemoteState.OriginHasCredentials: return kindLabel + " · creds in URL";
                default: return kindLabel;
            }
        }

        private enum RegistrationOutcomeKind
        {
            None,
            Pending,
            Succeeded,
            Failed
        }
    }
}
