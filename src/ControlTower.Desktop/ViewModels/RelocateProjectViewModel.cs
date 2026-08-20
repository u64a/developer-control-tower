using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Desktop.ViewModels
{
    /// <summary>
    /// View-model for <c>RelocateProjectDialog</c>. Owns the target-store
    /// selection, folder-name validation, the Preflight pipeline result,
    /// and the running Relocate state machine's step-row collection.
    /// </summary>
    public sealed class RelocateProjectViewModel : ObservableObject
    {
        private static readonly Regex SafeFolderRegex =
            new Regex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.Compiled);

        private readonly ProjectOverview _source;
        private readonly IReadOnlyList<RepoStore> _allStores;
        private readonly IRelocateProjectService _relocateService;
        private readonly System.Windows.Threading.Dispatcher _dispatcher;
        private readonly string _sourceStoreId;

        private RepoStore _selectedTargetStore;
        private string _targetFolder = string.Empty;
        private string _resolvedTargetPathPreview = string.Empty;
        private bool _copyIgnoredFiles;
        private bool _deleteSourceAfterSuccess;

        private bool _isPreflighting;
        private bool _isRelocating;
        private bool _isPreflightGreen;
        private bool _isPreflightYellow;
        private bool _hasNeedsPush;
        private string _preflightSummary = string.Empty;
        private string _statusMessage = string.Empty;
        private CancellationTokenSource _cts;

        public RelocateProjectViewModel(
            ProjectOverview source,
            IReadOnlyList<RepoStore> stores,
            IRelocateProjectService relocateService)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _allStores = stores ?? Array.Empty<RepoStore>();
            _relocateService = relocateService ?? throw new ArgumentNullException(nameof(relocateService));
            _dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;

            TargetStores = new ObservableCollection<RepoStore>(_allStores);
            Steps = new ObservableCollection<RelocateStepRow>();

            _sourceStoreId = InferSourceStoreId(source, _allStores);

            // Seed folder name from source folder leaf.
            _targetFolder = DeriveSourceFolderName(source);

            // Default target store: prefer source's store (same store moves
            // are allowed). Else first store.
            _selectedTargetStore = _allStores.FirstOrDefault(s => string.Equals(s.Id, _sourceStoreId, StringComparison.OrdinalIgnoreCase))
                ?? _allStores.FirstOrDefault();

            RefreshResolvedTargetPath();
        }

        public string SourceDisplayName => _source.DisplayName;
        public string SourceCurrentPath
        {
            get
            {
                if (IsConfigured(_source.SshTarget)) return _source.SshTarget;
                if (IsConfigured(_source.LocalPath)) return _source.LocalPath;
                return _source.SourcePath ?? string.Empty;
            }
        }

        // OverviewComposer emits these display sentinels for missing values.
        // The Relocate VM must treat them as "not set" — otherwise the service
        // sees a non-empty SshTarget and either refuses SSH→SSH or fails to
        // parse "Not configured" as a user@host:path tuple.
        private static bool IsConfigured(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            var trimmed = value.Trim();
            if (string.Equals(trimmed, "Not configured", StringComparison.OrdinalIgnoreCase)) return false;
            if (string.Equals(trimmed, "Not available", StringComparison.OrdinalIgnoreCase)) return false;
            return true;
        }
        public string SourceStoreLabel
        {
            get
            {
                var store = _allStores.FirstOrDefault(s => string.Equals(s.Id, _sourceStoreId, StringComparison.OrdinalIgnoreCase));
                if (store == null) return "(unknown)";
                return store.IsSsh
                    ? $"{store.Id} (SSH — {store.Host}:{store.Root})"
                    : $"{store.Id} ({store.Root})";
            }
        }

        public ObservableCollection<RepoStore> TargetStores { get; }
        public ObservableCollection<RelocateStepRow> Steps { get; }

        public RepoStore SelectedTargetStore
        {
            get => _selectedTargetStore;
            set
            {
                if (Set(ref _selectedTargetStore, value))
                {
                    RefreshResolvedTargetPath();
                    ResetPreflightFlags();
                }
            }
        }

        public string TargetFolder
        {
            get => _targetFolder;
            set
            {
                if (Set(ref _targetFolder, value ?? string.Empty))
                {
                    RefreshResolvedTargetPath();
                    ResetPreflightFlags();
                }
            }
        }

        public string ResolvedTargetPathPreview
        {
            get => _resolvedTargetPathPreview;
            private set => Set(ref _resolvedTargetPathPreview, value);
        }

        public bool CopyIgnoredFiles
        {
            get => _copyIgnoredFiles;
            set => Set(ref _copyIgnoredFiles, value);
        }

        public bool DeleteSourceAfterSuccess
        {
            get => _deleteSourceAfterSuccess;
            set => Set(ref _deleteSourceAfterSuccess, value);
        }

        public bool IsPreflighting
        {
            get => _isPreflighting;
            private set
            {
                if (Set(ref _isPreflighting, value))
                {
                    OnPropertyChanged(nameof(CanPreflight));
                    OnPropertyChanged(nameof(CanRelocate));
                    OnPropertyChanged(nameof(CanCancel));
                }
            }
        }

        public bool IsRelocating
        {
            get => _isRelocating;
            private set
            {
                if (Set(ref _isRelocating, value))
                {
                    OnPropertyChanged(nameof(CanPreflight));
                    OnPropertyChanged(nameof(CanRelocate));
                    OnPropertyChanged(nameof(CanCancel));
                    OnPropertyChanged(nameof(CanClose));
                }
            }
        }

        public bool IsPreflightGreen
        {
            get => _isPreflightGreen;
            private set
            {
                if (Set(ref _isPreflightGreen, value))
                {
                    OnPropertyChanged(nameof(CanRelocate));
                }
            }
        }

        public bool IsPreflightYellow
        {
            get => _isPreflightYellow;
            private set => Set(ref _isPreflightYellow, value);
        }

        public bool HasNeedsPush
        {
            get => _hasNeedsPush;
            private set => Set(ref _hasNeedsPush, value);
        }

        public string PreflightSummary
        {
            get => _preflightSummary;
            private set => Set(ref _preflightSummary, value);
        }

        public string StatusMessage
        {
            get => _statusMessage;
            private set => Set(ref _statusMessage, value);
        }

        public bool CanPreflight => !IsPreflighting && !IsRelocating && _selectedTargetStore != null
            && SafeFolderRegex.IsMatch((_targetFolder ?? string.Empty).Trim());
        public bool CanRelocate => !IsPreflighting && !IsRelocating && _isPreflightGreen;
        public bool CanCancel => IsPreflighting || IsRelocating;
        public bool CanClose => !IsRelocating;

        /// <summary>
        /// True if Relocate completed successfully — caller (MainWindow)
        /// uses this to refresh the project list.
        /// </summary>
        public bool PortfolioMutated { get; private set; }

        public async Task RunPreflightAsync()
        {
            if (!CanPreflight) return;
            IsPreflighting = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            try
            {
                Steps.Clear();
                IsPreflightGreen = false;
                IsPreflightYellow = false;
                HasNeedsPush = false;
                PreflightSummary = "Running preflight…";

                var req = BuildRequest();
                var result = await Task.Run(() => _relocateService.PreflightAsync(req, ct).GetAwaiter().GetResult(), ct);

                await _dispatcher.InvokeAsync(() =>
                {
                    HasNeedsPush = result.NeedsPush;
                    if (result.OkToRelocate)
                    {
                        IsPreflightGreen = true;
                        IsPreflightYellow = result.Warnings.Count > 0;
                        var summary = $"Preflight OK. {result.IgnoredFilesCount} ignored entries ({FormatBytes(result.IgnoredFilesBytes)}).";
                        if (result.Warnings.Count > 0)
                        {
                            summary += " Warnings: " + string.Join("; ", result.Warnings);
                        }
                        PreflightSummary = summary;
                    }
                    else
                    {
                        IsPreflightGreen = false;
                        PreflightSummary = "Preflight blocked: " + string.Join(" | ", result.Issues);
                    }
                });
            }
            catch (OperationCanceledException)
            {
                PreflightSummary = "Preflight cancelled.";
            }
            catch (Exception ex)
            {
                PreflightSummary = "Preflight error: " + ex.Message;
            }
            finally
            {
                IsPreflighting = false;
            }
        }

        public async Task RunRelocateAsync()
        {
            if (!CanRelocate) return;

            // Confirm delete-source if opted in.
            if (_deleteSourceAfterSuccess)
            {
                var ok = MessageBox.Show(
                    Application.Current?.MainWindow,
                    $"After a successful relocate, send '{SourceCurrentPath}' to the Recycle Bin?\n\nThis cannot be easily undone.",
                    "Confirm delete after relocate",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (ok != MessageBoxResult.Yes)
                {
                    DeleteSourceAfterSuccess = false;
                }
            }

            IsRelocating = true;
            _cts?.Dispose();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            try
            {
                Steps.Clear();
                StatusMessage = "Relocating…";
                var progress = new Progress<RelocateStepUpdate>(u => MarshalStepUpdate(u));

                var req = BuildRequest();
                var result = await _relocateService.RelocateAsync(req, progress, ct);

                if (result.Success)
                {
                    StatusMessage = "Relocate complete. New location: " + result.FinalTargetPath;
                    PortfolioMutated = true;
                }
                else if (result.Cancelled)
                {
                    StatusMessage = "Relocate cancelled.";
                }
                else
                {
                    StatusMessage = $"Relocate failed at {result.FailedStep}: {result.ErrorMessage}";
                }
            }
            catch (OperationCanceledException)
            {
                StatusMessage = "Relocate cancelled.";
            }
            catch (Exception ex)
            {
                StatusMessage = "Relocate error: " + ex.Message;
            }
            finally
            {
                IsRelocating = false;
            }
        }

        public async Task PushSourceAsync()
        {
            if (!HasNeedsPush || IsPreflighting || IsRelocating) return;
            IsPreflighting = true;
            try
            {
                var req = BuildRequest();
                _cts?.Dispose();
                _cts = new CancellationTokenSource();
                var result = await Task.Run(
                    () => _relocateService.PushSourceAsync(req, _cts.Token).GetAwaiter().GetResult(),
                    _cts.Token);
                if (result.State == RelocateStepState.Done)
                {
                    PreflightSummary = "Push succeeded. Re-running preflight…";
                }
                else
                {
                    PreflightSummary = "Push failed: " + result.Detail;
                    IsPreflighting = false;
                    return;
                }
            }
            catch (Exception ex)
            {
                PreflightSummary = "Push error: " + ex.Message;
                IsPreflighting = false;
                return;
            }
            IsPreflighting = false;
            await RunPreflightAsync();
        }

        public void Cancel()
        {
            try { _cts?.Cancel(); } catch { }
        }

        private RelocateRequest BuildRequest()
        {
            var store = _selectedTargetStore;
            return new RelocateRequest
            {
                ProjectId = _source.Id,
                DisplayName = _source.DisplayName,
                Summary = _source.Summary,
                LifecycleState = string.IsNullOrWhiteSpace(_source.LifecycleState) ? "active" : _source.LifecycleState,
                GitHubUrl = _source.GitHubUrl ?? string.Empty,
                AdoUrl = _source.AdoUrl ?? string.Empty,
                RemoteUrl = _source.RemoteUrl ?? string.Empty,
                SourceStoreId = _sourceStoreId,
                // Filter OverviewComposer's "Not configured" / "Not available"
                // sentinels so the service never sees them as real values.
                SourceLocalPath = IsConfigured(_source.SshTarget)
                    ? string.Empty
                    : (IsConfigured(_source.LocalPath) ? _source.LocalPath : string.Empty),
                SourceSshTarget = IsConfigured(_source.SshTarget) ? _source.SshTarget : string.Empty,
                TargetStoreId = store?.Id ?? string.Empty,
                TargetFolder = (_targetFolder ?? string.Empty).Trim(),
                CopyIgnoredFiles = _copyIgnoredFiles,
                DeleteSourceAfterSuccess = _deleteSourceAfterSuccess
            };
        }

        private void MarshalStepUpdate(RelocateStepUpdate u)
        {
            if (_dispatcher.CheckAccess())
            {
                ApplyStepUpdate(u);
            }
            else
            {
                _dispatcher.BeginInvoke(new Action(() => ApplyStepUpdate(u)));
            }
        }

        private void ApplyStepUpdate(RelocateStepUpdate u)
        {
            var existing = Steps.FirstOrDefault(s => s.Step == u.Step);
            if (existing == null)
            {
                Steps.Add(new RelocateStepRow
                {
                    Step = u.Step,
                    StepName = HumanName(u.Step),
                    State = u.State,
                    StateIcon = StateIcon(u.State),
                    Detail = u.Detail ?? string.Empty
                });
            }
            else
            {
                existing.State = u.State;
                existing.StateIcon = StateIcon(u.State);
                existing.Detail = u.Detail ?? string.Empty;
            }
        }

        private void RefreshResolvedTargetPath()
        {
            var store = _selectedTargetStore;
            var folder = (_targetFolder ?? string.Empty).Trim();
            if (store == null || string.IsNullOrEmpty(folder))
            {
                ResolvedTargetPathPreview = "(pick a store and folder)";
                return;
            }
            var preview = store.IsSsh
                ? $"{(string.IsNullOrEmpty(store.User) ? string.Empty : store.User + "@")}{store.Host}:{store.Root.TrimEnd('/', '\\')}\\{folder}"
                : System.IO.Path.GetFullPath(System.IO.Path.Combine(store.Root, folder));
            ResolvedTargetPathPreview = SafeFolderRegex.IsMatch(folder)
                ? preview
                : preview + "  (invalid folder name)";
            OnPropertyChanged(nameof(CanPreflight));
        }

        private void ResetPreflightFlags()
        {
            if (_isPreflightGreen || _isPreflightYellow || HasNeedsPush)
            {
                IsPreflightGreen = false;
                IsPreflightYellow = false;
                HasNeedsPush = false;
                PreflightSummary = "Settings changed — re-run preflight.";
            }
        }

        private static string DeriveSourceFolderName(ProjectOverview source)
        {
            var path = IsConfigured(source.SshTarget) ? source.SshTarget
                : IsConfigured(source.LocalPath) ? source.LocalPath
                : source.SourcePath ?? string.Empty;
            if (string.IsNullOrWhiteSpace(path)) return source.Id ?? string.Empty;

            // Strip user@host: prefix if present.
            var colonIdx = path.LastIndexOf(':');
            if (colonIdx > 1) path = path.Substring(colonIdx + 1);

            path = path.TrimEnd('/', '\\');
            var sepIdx = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
            return sepIdx >= 0 ? path.Substring(sepIdx + 1) : path;
        }

        private static string InferSourceStoreId(ProjectOverview source, IReadOnlyList<RepoStore> stores)
        {
            // For SSH sources: match against host (and user when present).
            if (IsConfigured(source.SshTarget))
            {
                var hostPart = source.SshTarget;
                var colonIdx = hostPart.IndexOf(':');
                if (colonIdx > 0) hostPart = hostPart.Substring(0, colonIdx);
                var atIdx = hostPart.IndexOf('@');
                var user = atIdx > 0 ? hostPart.Substring(0, atIdx) : string.Empty;
                var host = atIdx > 0 ? hostPart.Substring(atIdx + 1) : hostPart;
                var match = stores.FirstOrDefault(s =>
                    s.IsSsh && string.Equals(s.Host, host, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(user) || string.Equals(s.User, user, StringComparison.OrdinalIgnoreCase)));
                return match?.Id ?? string.Empty;
            }

            // For local sources: longest matching root prefix wins.
            if (IsConfigured(source.LocalPath))
            {
                try
                {
                    var full = Path.GetFullPath(source.LocalPath);
                    var match = stores
                        .Where(s => !s.IsSsh && !string.IsNullOrEmpty(s.Root))
                        .OrderByDescending(s => s.Root.Length)
                        .FirstOrDefault(s => full.StartsWith(
                            Path.GetFullPath(s.Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                            StringComparison.OrdinalIgnoreCase));
                    return match?.Id ?? string.Empty;
                }
                catch
                {
                    return string.Empty;
                }
            }
            return string.Empty;
        }

        private static string HumanName(RelocateStep step) => step switch
        {
            RelocateStep.Preflight => "Preflight",
            RelocateStep.CreateDestFolder => "Create destination folder",
            RelocateStep.CloneOrigin => "Clone origin",
            RelocateStep.MigrateMetadata => "Migrate .controltower metadata",
            RelocateStep.CopyIgnoredFiles => "Copy ignored files",
            RelocateStep.VerifyDestination => "Verify destination",
            RelocateStep.RebindPortfolio => "Rebind portfolio",
            RelocateStep.DeleteSource => "Delete source",
            _ => step.ToString(),
        };

        private static string StateIcon(RelocateStepState state) => state switch
        {
            RelocateStepState.Pending => "○",
            RelocateStepState.Running => "●",
            RelocateStepState.Done => "✓",
            RelocateStepState.Failed => "✗",
            RelocateStepState.Skipped => "⤼",
            RelocateStepState.Cancelled => "⨯",
            RelocateStepState.Warning => "⚠",
            _ => "•",
        };

        private static string FormatBytes(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
            if (bytes < 1024L * 1024 * 1024) return (bytes / (1024.0 * 1024)).ToString("F1") + " MB";
            return (bytes / (1024.0 * 1024 * 1024)).ToString("F2") + " GB";
        }
    }

    public sealed class RelocateStepRow : ObservableObject
    {
        private RelocateStepState _state;
        private string _stateIcon = "○";
        private string _detail = string.Empty;

        public RelocateStep Step { get; set; }
        public string StepName { get; set; } = string.Empty;
        public RelocateStepState State
        {
            get => _state;
            set => Set(ref _state, value);
        }
        public string StateIcon
        {
            get => _stateIcon;
            set => Set(ref _stateIcon, value);
        }
        public string Detail
        {
            get => _detail;
            set => Set(ref _detail, value);
        }
    }
}
