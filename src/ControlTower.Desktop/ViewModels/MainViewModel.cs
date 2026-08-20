using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Launch;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Desktop.ViewModels
{
    public sealed class MainViewModel : ObservableObject
    {
        private readonly ControlTowerService _service;
        private readonly IShellLauncher _shellLauncher;
        private readonly IUpdateService _updateService;
        private readonly UpdateOptions _updateOptions;
        private readonly IList<ProjectOverview> _allProjects;
        private ProjectOverview _selectedProject;
        private string _statusMessage;
        private bool _isBusy;
        private string _selectedSortMode;
        private string _searchText;
        private bool _showAttentionOnly;
        private bool _isUpdateAvailable;
        private string _updateChipTooltip;
        private UpdateCheckResult _lastUpdateCheckResult;
        private int _updateCommitsBehind;

        // Tracks the most recent selection a refresh was kicked off for. When a
        // background refresh completes we only apply its result if the user is
        // still looking at that same project (otherwise a slow SSH refresh from
        // a previous click could clobber a newer selection).
        private int _refreshGeneration;

        // Latches true the first time the user invokes "Open Code RunAs Admin"
        // in this app session. Windows / VS Code will refuse a subsequent
        // non-elevated launch when an elevated VS Code instance is already
        // running ("It is not possible to run this software with elevated
        // privileges mixed..."), so once the user has chosen the admin path
        // we disable the regular Open Code button for the rest of the session
        // and force them to keep using Open Code RunAs Admin. This is a known
        // VS Code limitation, not something we can work around per-project.
        private bool _codeAdminLatched;

        public MainViewModel(ControlTowerService service)
            : this(service, null, null, null)
        {
        }

        public MainViewModel(ControlTowerService service, IShellLauncher shellLauncher)
            : this(service, shellLauncher, null, null)
        {
        }

        public MainViewModel(
            ControlTowerService service,
            IShellLauncher shellLauncher,
            IUpdateService updateService,
            UpdateOptions updateOptions)
        {
            _service = service;
            _shellLauncher = shellLauncher ?? new WindowsShellLauncher();
            _updateService = updateService;
            _updateOptions = updateOptions ?? UpdateOptions.Defaults();
            _allProjects = new List<ProjectOverview>();
            Projects = new ObservableCollection<ProjectRow>();
            _selectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Tally = new ObservableCollection<PortfolioTallyItem>();
            SortModes = new ObservableCollection<string>
            {
                ProjectSortModes.Name,
                ProjectSortModes.NeedsAttention,
                ProjectSortModes.RecentActivity
            };
            _selectedSortMode = ProjectSortModes.Default;
            _statusMessage = "Loading...";
            AppVersion = ResolveAppVersion();

            RefreshCommand = new DelegateCommand(RefreshSelected);
            RefreshAllCommand = new DelegateCommand(() => { _ = SeedLocalStatesAsync(); });
            OpenCodeForCommand = new DelegateCommand<ProjectOverview>(p => Launch(p, LaunchTargetKind.Code), p => RowHasLocal(p) || RowHasSsh(p));
            OpenRemoteForCommand = new DelegateCommand<ProjectOverview>(p => Launch(p, LaunchTargetKind.RemoteCode), RowHasSsh);
            OpenGitHubForCommand = new DelegateCommand<ProjectOverview>(p => Launch(p, LaunchTargetKind.GitHub), p => p != null && !string.IsNullOrWhiteSpace(p.GitHubUrl));
            OpenAdoForCommand = new DelegateCommand<ProjectOverview>(p => Launch(p, LaunchTargetKind.Ado), p => p != null && !string.IsNullOrWhiteSpace(p.AdoUrl));
            FilterByStateCommand = new DelegateCommand<PortfolioTallyItem>(FilterByState);
            RefreshSelectedManyCommand = new DelegateCommand(() => { _ = RefreshSelectedManyAsync(); }, () => HasSelection);
            OpenAllSelectedCommand = new DelegateCommand(OpenAllSelected, () => HasSelection);
            ClearSelectionCommand = new DelegateCommand(ClearSelection, () => HasSelection);
            OpenCodeCommand = new DelegateCommand(OpenCode);
            OpenCodeAdminCommand = new DelegateCommand(OpenCodeAdmin);
            OpenRemoteCommand = new DelegateCommand(OpenRemote);
            OpenGitHubCommand = new DelegateCommand(OpenGitHub);
            OpenAdoCommand = new DelegateCommand(OpenAdo);
            OpenPlanCommand = new DelegateCommand(OpenPlan);
            ViewLogCommand = new DelegateCommand(ViewLog);
        }

        public string AppVersion { get; }

        private static string ResolveAppVersion()
        {
            var asm = Assembly.GetExecutingAssembly();
            var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(info))
            {
                // Strip any trailing build metadata noise from SourceLink
                var plus = info.IndexOf('+');
                if (plus >= 0 && info.Length - plus > 12)
                {
                    info = info.Substring(0, plus + 8);
                }
                return "v" + info;
            }
            return "v" + (asm.GetName().Version?.ToString(3) ?? "0.0.0");
        }

        public ObservableCollection<ProjectRow> Projects { get; private set; }

        private readonly HashSet<string> _selectedIds;

        private ProjectRow _selectedRow;

        /// <summary>
        /// The focused row (drives the detail rail). Single-selection; the bulk
        /// action set is tracked separately via the per-row checkboxes.
        /// </summary>
        public ProjectRow SelectedRow
        {
            get { return _selectedRow; }
            set
            {
                if (ReferenceEquals(_selectedRow, value))
                {
                    return;
                }

                _selectedRow = value;
                OnPropertyChanged();
                SelectedProject = value == null ? null : value.Project;
            }
        }

        /// <summary>Number of projects checked for a bulk action (across all states).</summary>
        public int SelectedCount
        {
            get { return _selectedIds.Count; }
        }

        public bool HasSelection
        {
            get { return _selectedIds.Count > 0; }
        }

        /// <summary>Snapshot of the checked project ids, for bulk push/library flows.</summary>
        public IReadOnlyCollection<string> SelectedProjectIds
        {
            get { return _selectedIds.ToList(); }
        }

        /// <summary>
        /// Footer status line, e.g. "Loaded 41 projects · 2 need attention ·
        /// last scan 16:24". Recomputed whenever the portfolio or scan state
        /// changes.
        /// </summary>
        public string PortfolioSummaryLine
        {
            get
            {
                var total = _allProjects.Count;
                var attention = _allProjects.Count(p => p.RepoState == RepoState.Attention);
                var line = "Loaded " + total + " project" + (total == 1 ? string.Empty : "s") +
                           "  ·  " + attention + " need attention";
                if (!string.IsNullOrEmpty(_lastScanText))
                {
                    line += "  ·  " + _lastScanText;
                }
                return line;
            }
        }

        /// <summary>
        /// Human summary for the bulk bar, e.g. "3 selected" or
        /// "5 selected · 2 hidden by filter" when some checked rows are filtered out.
        /// </summary>
        public string SelectionSummary
        {
            get
            {
                var total = _selectedIds.Count;
                if (total == 0)
                {
                    return string.Empty;
                }

                var visible = Projects.Count(r => r.IsSelected);
                var hidden = total - visible;
                var text = total + " selected";
                if (hidden > 0)
                {
                    text += "  ·  " + hidden + " hidden by filter";
                }
                return text;
            }
        }

        /// <summary>Header "select all" — reflects/sets the checked state of every visible row.</summary>
        public bool AllVisibleSelected
        {
            get { return Projects.Count > 0 && Projects.All(r => r.IsSelected); }
            set
            {
                foreach (var row in Projects)
                {
                    row.IsSelected = value;
                }
                OnPropertyChanged();
            }
        }

        /// <summary>
        /// At-a-glance portfolio summary: count of projects per repo state,
        /// computed across the whole portfolio (not the filtered view).
        /// </summary>
        public ObservableCollection<PortfolioTallyItem> Tally { get; private set; }

        public ObservableCollection<string> SortModes { get; private set; }

        public ProjectOverview SelectedProject
        {
            get { return _selectedProject; }
            set
            {
                if (ReferenceEquals(_selectedProject, value))
                {
                    return;
                }

                _selectedProject = value;
                OnPropertyChanged();
                OnPropertyChanged("CanOpenCode");
                OnPropertyChanged("CanOpenCodeNormal");
                OnPropertyChanged("CanOpenCodeAdmin");
                OnPropertyChanged("OpenCodeDisabledTooltip");
                OnPropertyChanged("CanOpenRemote");
                OnPropertyChanged("CanOpenGitHub");
                OnPropertyChanged("CanOpenAdo");
                OnPropertyChanged("CanOpenPlan");
                OnPropertyChanged("CanManageProject");
                if (_selectedProject != null)
                {
                    // M-01: selection no longer auto-triggers a refresh.
                    // We show the cached overview immediately; the user
                    // hits the explicit Refresh button when they want a
                    // fresh git/SSH scan. Avoids per-click SSH round-trips
                    // and keeps the orientation loop fast (spec §1/§8).
                    StatusMessage = _selectedProject.StatusLine;
                }
            }
        }

        public string SelectedSortMode
        {
            get { return _selectedSortMode; }
            set
            {
                if (_selectedSortMode == value)
                {
                    return;
                }

                _selectedSortMode = value;
                OnPropertyChanged();
                ApplyProjectView();
            }
        }

        public bool ShowAttentionOnly
        {
            get { return _showAttentionOnly; }
            set
            {
                if (_showAttentionOnly == value)
                {
                    return;
                }

                _showAttentionOnly = value;
                OnPropertyChanged();
                ApplyProjectView();
            }
        }

        public string SearchText
        {
            get { return _searchText; }
            set
            {
                if (_searchText == value)
                {
                    return;
                }

                _searchText = value;
                OnPropertyChanged();
                ApplyProjectView();
            }
        }

        public string StatusMessage
        {
            get { return _statusMessage; }
            set
            {
                _statusMessage = value;
                OnPropertyChanged();
            }
        }

        public bool IsBusy
        {
            get { return _isBusy; }
            set
            {
                if (_isBusy == value)
                {
                    return;
                }

                _isBusy = value;
                OnPropertyChanged();
            }
        }

        public bool IsUpdateAvailable
        {
            get { return _isUpdateAvailable; }
            private set
            {
                if (_isUpdateAvailable == value)
                {
                    return;
                }

                _isUpdateAvailable = value;
                OnPropertyChanged();
            }
        }

        public int UpdateCommitsBehind
        {
            get { return _updateCommitsBehind; }
            private set
            {
                if (_updateCommitsBehind == value)
                {
                    return;
                }

                _updateCommitsBehind = value;
                OnPropertyChanged();
            }
        }

        public string UpdateChipTooltip
        {
            get { return _updateChipTooltip; }
            private set
            {
                if (_updateChipTooltip == value)
                {
                    return;
                }

                _updateChipTooltip = value;
                OnPropertyChanged();
            }
        }

        public UpdateCheckResult LastUpdateCheckResult
        {
            get { return _lastUpdateCheckResult; }
            private set
            {
                _lastUpdateCheckResult = value;
                OnPropertyChanged();
            }
        }

        public UpdateOptions UpdateOptions
        {
            get { return _updateOptions; }
        }

        /// <summary>
        /// Project id the shell would like restored after the next portfolio
        /// load completes. Used when the service graph rebuilds (e.g. after
        /// Settings save) so the user's prior selection survives.
        /// </summary>
        public string PendingSelectionId { get; set; }

        public bool CanOpenCode
        {
            get
            {
                // Local "Open Code" needs a real local clone. SSH/remote-only
                // projects (LocalPath "Not available") use Open Remote instead,
                // so they must not enable the local code button.
                return SelectedProject != null &&
                       !string.IsNullOrWhiteSpace(SelectedProject.LocalPath) &&
                       !string.Equals(SelectedProject.LocalPath, "Not available", StringComparison.OrdinalIgnoreCase);
            }
        }

        // Drives the non-elevated "Open Code" button. Once the user has used
        // "Open Code RunAs Admin" in this session we keep this disabled, because
        // VS Code refuses to launch a non-elevated instance while an elevated
        // one is already running on the same desktop.
        public bool CanOpenCodeNormal
        {
            get { return CanOpenCode && !_codeAdminLatched; }
        }

        // Tooltip text for the regular Open Code button. Stays informational
        // until the admin button has been used, then explains why the button
        // is greyed out so the user knows to keep using RunAs Admin.
        public string OpenCodeDisabledTooltip
        {
            get
            {
                if (_codeAdminLatched)
                {
                    return "Disabled for this session: VS Code was launched as Administrator. " +
                           "Use 'Open Code RunAs Admin' for any further projects until you close all elevated VS Code windows and restart Developer Control Tower.";
                }
                return "Open this project in VS Code.";
            }
        }

        public bool CanOpenRemote
        {
            get
            {
                return SelectedProject != null &&
                       !string.IsNullOrWhiteSpace(SelectedProject.SshTarget) &&
                       SelectedProject.SshTarget != "Not configured";
            }
        }

        public bool CanOpenGitHub
        {
            get
            {
                return SelectedProject != null &&
                       !string.IsNullOrWhiteSpace(SelectedProject.GitHubUrl);
            }
        }

        public bool CanOpenAdo
        {
            get
            {
                return SelectedProject != null &&
                       !string.IsNullOrWhiteSpace(SelectedProject.AdoUrl);
            }
        }

        public bool CanOpenPlan
        {
            get
            {
                return SelectedProject != null &&
                       (!string.IsNullOrWhiteSpace(SelectedProject.PlanningPath) ||
                        !string.IsNullOrWhiteSpace(SelectedProject.PlanningSource));
            }
        }

        public bool CanManageProject
        {
            get { return SelectedProject != null; }
        }

        public DelegateCommand RefreshCommand { get; private set; }

        public DelegateCommand OpenCodeCommand { get; private set; }

        public DelegateCommand RefreshAllCommand { get; private set; }

        public DelegateCommand<ProjectOverview> OpenCodeForCommand { get; private set; }

        public DelegateCommand<ProjectOverview> OpenRemoteForCommand { get; private set; }

        public DelegateCommand<ProjectOverview> OpenGitHubForCommand { get; private set; }

        public DelegateCommand<ProjectOverview> OpenAdoForCommand { get; private set; }

        public DelegateCommand<PortfolioTallyItem> FilterByStateCommand { get; private set; }

        public DelegateCommand RefreshSelectedManyCommand { get; private set; }

        public DelegateCommand OpenAllSelectedCommand { get; private set; }

        public DelegateCommand ClearSelectionCommand { get; private set; }

        public DelegateCommand OpenCodeAdminCommand { get; private set; }

        public DelegateCommand OpenRemoteCommand { get; private set; }

        public DelegateCommand OpenGitHubCommand { get; private set; }

        public DelegateCommand OpenAdoCommand { get; private set; }

        public DelegateCommand OpenPlanCommand { get; private set; }

        public DelegateCommand ViewLogCommand { get; private set; }

        public void Load()
        {
            _allProjects.Clear();
            var items = _service.LoadPortfolio();
            foreach (var item in items)
            {
                _allProjects.Add(item);
            }

            var preferred = PendingSelectionId;
            PendingSelectionId = null;
            ApplyProjectView(preferred);

            if (Projects.Count == 0)
            {
                StatusMessage = "No projects found in portfolio";
            }
        }

        /// <summary>
        /// Asynchronous portfolio load. Runs the IO-heavy
        /// <see cref="ControlTowerService.LoadPortfolio"/> off the UI thread
        /// so window startup never blocks. Surfaces validation/load failures
        /// in <see cref="StatusMessage"/>.
        /// </summary>
        public async Task LoadAsync()
        {
            IsBusy = true;
            StatusMessage = "Loading portfolio...";

            try
            {
                var items = await Task.Run(() =>
                {
                    try
                    {
                        return (Items: (IReadOnlyList<ProjectOverview>)_service.LoadPortfolio(), Error: (string)null);
                    }
                    catch (Exception ex)
                    {
                        return (Items: (IReadOnlyList<ProjectOverview>)Array.Empty<ProjectOverview>(), Error: ex.Message);
                    }
                }).ConfigureAwait(true);

                _allProjects.Clear();
                foreach (var item in items.Items)
                {
                    _allProjects.Add(item);
                }

                var preferred = PendingSelectionId;
                PendingSelectionId = null;
                ApplyProjectView(preferred);

                if (items.Error != null)
                {
                    StatusMessage = "Portfolio load failed: " + items.Error;
                }
                else if (Projects.Count == 0)
                {
                    StatusMessage = "No projects found in portfolio";
                }
                else
                {
                    StatusMessage = "Loaded " + Projects.Count + " project" +
                        (Projects.Count == 1 ? string.Empty : "s") + ".";
                }
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Background self-update probe. Calls the update service, marshals
        /// the result back to the UI thread, and only flips
        /// <see cref="IsUpdateAvailable"/> when there's a real, clean
        /// fast-forward available. All exceptions are swallowed (Debug-level
        /// logged) so an offline laptop never sees a noisy error toast.
        /// </summary>
        public async Task RunBackgroundUpdateCheckAsync(CancellationToken ct)
        {
            if (_updateService == null) return;

            try
            {
                var result = await _updateService.CheckForUpdatesAsync(_updateOptions, ct).ConfigureAwait(true);
                LastUpdateCheckResult = result;

                if (result.Status == UpdateStatus.UpdateAvailable)
                {
                    UpdateCommitsBehind = result.CommitsBehind;
                    if (result.Provider == UpdateProviderKind.PackagedRelease)
                    {
                        var channel = string.IsNullOrWhiteSpace(result.Channel)
                            ? "the installed channel"
                            : result.Channel;
                        UpdateChipTooltip =
                            $"Version {result.TargetVersion} is available on {channel}.";
                    }
                    else
                    {
                        var branch = string.IsNullOrWhiteSpace(result.ConfiguredBranch)
                            ? "origin"
                            : result.ConfiguredBranch;
                        UpdateChipTooltip =
                            $"You are {result.CommitsBehind} commit(s) behind {branch}.";
                    }
                    IsUpdateAvailable = true;
                }
                else
                {
                    UpdateCommitsBehind = 0;
                    UpdateChipTooltip = result.Message;
                    IsUpdateAvailable = false;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Debug("Update", "Background update check failed: " + ex.Message);
                IsUpdateAvailable = false;
            }
        }

        public void RegisterProject(ProjectRegistrationRequest request)
        {
            var result = _service.RegisterProject(request);
            StatusMessage = result.Message;
            if (!result.Success)
            {
                return;
            }

            Load();
            ApplyProjectView(result.ProjectId);
        }

        public void RemoveSelectedProject()
        {
            var result = _service.RemoveProject(GetSelectedRef());
            StatusMessage = result.Message;
            if (result.Success)
            {
                Load();
            }
        }

        public ProjectRegistrationRequest BuildEditRequest()
        {
            var projectRef = GetSelectedRef();
            if (projectRef == null)
            {
                return null;
            }

            var project = _service.GetProjectDefinition(projectRef);
            if (project == null)
            {
                return null;
            }

            return new ProjectRegistrationRequest
            {
                ProjectId = project.Id,
                SourcePath = projectRef.Path,
                DisplayName = project.DisplayName,
                Summary = project.Summary,
                LifecycleState = project.LifecycleState,
                LocalPath = project.Locations == null ? string.Empty : project.Locations.LocalPath,
                SshTarget = project.Locations == null ? string.Empty : project.Locations.SshTarget,
                GitHubUrl = project.Launch == null ? string.Empty : project.Launch.GitHub,
                AdoUrl = project.Launch == null ? string.Empty : project.Launch.Ado,
                RemoteUrl = project.Locations == null ? string.Empty : project.Locations.RemoteUrl,
                Group = project.Group,
                // Editing an existing project legitimately updates the portfolio
                // entry — allow overwrite so the duplicate-id guard doesn't
                // reject the round-trip.
                AllowOverwrite = true
            };
        }

        /// <summary>
        /// Reassigns a project to a group (folder) by rewriting its project.yml,
        /// preserving all other metadata. Empty/"Ungrouped" clears the folder.
        /// Reloads the portfolio so the grouped view updates.
        /// </summary>
        public void MoveProjectToGroup(ProjectOverview project, string group)
        {
            if (project == null) return;

            var def = _service.GetProjectDefinition(new ProjectRef { Id = project.Id, Path = project.SourcePath });
            if (def == null)
            {
                StatusMessage = "Could not load project to move.";
                return;
            }

            var normalized = string.IsNullOrWhiteSpace(group) || string.Equals(group, "Ungrouped", StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : group.Trim();

            var request = new ProjectRegistrationRequest
            {
                ProjectId = def.Id,
                SourcePath = project.SourcePath,
                DisplayName = def.DisplayName,
                Summary = def.Summary,
                LifecycleState = def.LifecycleState,
                LocalPath = def.Locations == null ? string.Empty : def.Locations.LocalPath,
                SshTarget = def.Locations == null ? string.Empty : def.Locations.SshTarget,
                GitHubUrl = def.Launch == null ? string.Empty : def.Launch.GitHub,
                AdoUrl = def.Launch == null ? string.Empty : def.Launch.Ado,
                RemoteUrl = def.Locations == null ? string.Empty : def.Locations.RemoteUrl,
                Group = normalized,
                AllowOverwrite = true
            };

            var result = _service.RegisterProject(request);
            StatusMessage = result.Success
                ? project.DisplayName + " → " + (normalized.Length == 0 ? "Ungrouped" : normalized)
                : result.Message;
            if (result.Success)
            {
                Load();
                ApplyProjectView(def.Id);
            }
        }

        private CancellationTokenSource _seedCts;
        private RepoState? _stateFilter;
        private readonly GroupCollapseStore _groupCollapse = new GroupCollapseStore();
        private HashSet<string> _collapsedGroups;

        /// <summary>Distinct group names in the portfolio (excludes Ungrouped), sorted, for the editor combo.</summary>
        public IReadOnlyList<string> KnownGroups
        {
            get
            {
                return _allProjects
                    .Select(p => p.Group)
                    .Where(g => !string.IsNullOrWhiteSpace(g) && !string.Equals(g, "Ungrouped", StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
        }

        // Ungrouped sorts last; everything else alphabetical.
        private static string GroupSortKey(string group)
        {
            return string.Equals(group, "Ungrouped", StringComparison.OrdinalIgnoreCase)
                ? "\uFFFFUngrouped"
                : (group ?? string.Empty);
        }

        public bool IsGroupCollapsed(string group)
        {
            _collapsedGroups ??= _groupCollapse.Read();
            return group != null && _collapsedGroups.Contains(group);
        }

        public void SetGroupCollapsed(string group, bool collapsed)
        {
            if (string.IsNullOrWhiteSpace(group)) return;
            _collapsedGroups ??= _groupCollapse.Read();
            if (collapsed) _collapsedGroups.Add(group); else _collapsedGroups.Remove(group);
            _groupCollapse.Write(_collapsedGroups);
        }
        private string _lastScanText;

        /// <summary>
        /// One-shot background seed of repo state for LOCAL clones only (never
        /// probes SSH). Runs after first paint; fills in the status lamps
        /// progressively and is cancelled on app exit. SSH/hosted repos stay
        /// strictly on-demand so startup never depends on network reach.
        /// </summary>
        public async Task SeedLocalStatesAsync()
        {
            _seedCts?.Cancel();
            var cts = new CancellationTokenSource();
            _seedCts = cts;
            var ct = cts.Token;

            var targets = _allProjects
                .Where(p => !string.IsNullOrWhiteSpace(p.LocalPath) &&
                            !string.Equals(p.LocalPath, "Not available", StringComparison.OrdinalIgnoreCase))
                .Select(ToProjectRef)
                .ToList();

            foreach (var projectRef in targets)
            {
                if (ct.IsCancellationRequested)
                {
                    return;
                }

                ProjectOverview refreshed;
                try
                {
                    refreshed = await Task.Run(() => _service.LoadProject(projectRef, ScanPolicy.LocalOnly), ct);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // One repo failing to scan must not abort the whole seed.
                    continue;
                }

                if (ct.IsCancellationRequested)
                {
                    return;
                }

                if (refreshed != null)
                {
                    ApplySeededProject(refreshed);
                }
            }

            _lastScanText = "last scan " + DateTime.Now.ToString("HH:mm");
            OnPropertyChanged("PortfolioSummaryLine");
        }

        private void ApplySeededProject(ProjectOverview refreshed)
        {
            for (var i = 0; i < _allProjects.Count; i++)
            {
                if (string.Equals(_allProjects[i].Id, refreshed.Id, StringComparison.OrdinalIgnoreCase))
                {
                    _allProjects[i] = refreshed;
                    break;
                }
            }

            for (var i = 0; i < Projects.Count; i++)
            {
                if (string.Equals(Projects[i].Id, refreshed.Id, StringComparison.OrdinalIgnoreCase))
                {
                    var wasSelected = SelectedProject != null && SelectedProject.Id == refreshed.Id;
                    // Swap the wrapped overview in place so the row keeps its
                    // checkbox/selection state while the lamp and metadata update.
                    Projects[i].Project = refreshed;
                    if (wasSelected)
                    {
                        SelectedProject = refreshed;
                    }
                    break;
                }
            }

            RebuildTally();
        }

        /// <summary>Cancels any in-flight background seed (called on app exit).</summary>
        public void CancelBackgroundWork()
        {
            _seedCts?.Cancel();
        }

        private void RefreshSelected()
        {
            var projectRef = GetSelectedRef();
            if (projectRef == null)
            {
                StatusMessage = "No project selected";
                return;
            }

            // Soft cache: show whatever we already have for this project from
            // the cheap initial scan immediately, then fetch fresh data on a
            // background thread. The user clicks → sees the page with cached
            // git/origin/branch instantly. The pane then quietly updates a
            // moment later when the SSH/git scan completes.
            var generation = Interlocked.Increment(ref _refreshGeneration);
            var projectId = projectRef.Id;

            StatusMessage = "Refreshing " + projectId + "...";

            var dispatcher = Application.Current?.Dispatcher;
            Task.Run(() =>
            {
                try
                {
                    var refreshed = _service.LoadProject(projectRef, true);

                    void Apply()
                    {
                        // Drop stale results — user clicked another project after
                        // we started, or hit Refresh again with a newer generation.
                        if (generation != Volatile.Read(ref _refreshGeneration))
                        {
                            return;
                        }

                        var idx = -1;
                        for (var i = 0; i < _allProjects.Count; i++)
                        {
                            if (string.Equals(_allProjects[i].Id, refreshed.Id, StringComparison.OrdinalIgnoreCase))
                            {
                                idx = i;
                                break;
                            }
                        }
                        if (idx >= 0)
                        {
                            _allProjects[idx] = refreshed;
                        }
                        // If not found, do NOT add — a refresh should always
                        // find the tracked entry; adding would create a phantom.

                        ApplyProjectView(refreshed.Id);

                        StatusMessage = string.IsNullOrWhiteSpace(refreshed.StatusLine)
                            ? "Ready"
                            : refreshed.StatusLine;
                    }

                    if (dispatcher != null && !dispatcher.CheckAccess())
                    {
                        dispatcher.Invoke(Apply);
                    }
                    else
                    {
                        Apply();
                    }
                }
                catch (Exception ex)
                {
                    var msg = "Refresh failed: " + ex.Message;
                    if (dispatcher != null && !dispatcher.CheckAccess())
                    {
                        dispatcher.Invoke(() => StatusMessage = msg);
                    }
                    else
                    {
                        StatusMessage = msg;
                    }
                }
            });
        }

        private void OpenCode()
        {
            Launch(LaunchTargetKind.Code);
        }

        private void OpenCodeAdmin()
        {
            // Latch before launching so the regular Open Code button is greyed
            // out even if the user dismisses the UAC prompt — the moment we
            // attempt an elevated launch this session is committed to the
            // RunAs Admin path (VS Code will refuse a mixed-elevation start
            // once any elevated instance comes up). This sidesteps the
            // confusing "It is not possible to run this software with
            // elevated privileges" error users would otherwise hit on the
            // second project.
            if (!_codeAdminLatched)
            {
                _codeAdminLatched = true;
                OnPropertyChanged("CanOpenCodeNormal");
                OnPropertyChanged("OpenCodeDisabledTooltip");
            }

            Launch(LaunchTargetKind.CodeAdmin);
        }

        private void OpenGitHub()
        {
            Launch(LaunchTargetKind.GitHub);
        }

        private void OpenRemote()
        {
            Launch(LaunchTargetKind.RemoteCode);
        }

        private void OpenAdo()
        {
            Launch(LaunchTargetKind.Ado);
        }

        private void OpenPlan()
        {
            Launch(LaunchTargetKind.Plan);
        }

        // M-03: View-log surface in the status bar. Opens today's log file
        // in the user's default text viewer, or the log folder when the
        // file hasn't been written yet. Routes through IShellLauncher so
        // we never reach Process.Start directly (test-seam pattern).
        private void ViewLog()
        {
            try
            {
                var target = LogOpenTarget.Resolve();
                if (string.IsNullOrWhiteSpace(target))
                {
                    StatusMessage = "Log location unavailable.";
                    return;
                }

                // Ensure the folder exists even when the file does not — opening
                // a missing path otherwise surfaces a confusing shell error.
                try
                {
                    System.IO.Directory.CreateDirectory(AppLogger.LogFolder);
                }
                catch
                {
                    // Best-effort; the shell error (if any) is still preferable
                    // to crashing here.
                }

                _shellLauncher.Open(target);
            }
            catch (Exception ex)
            {
                StatusMessage = "Could not open log: " + ex.Message;
            }
        }

        private void Launch(ProjectOverview project, LaunchTargetKind kind)
        {
            if (project == null)
            {
                StatusMessage = "No project";
                return;
            }

            var projectRef = new ProjectRef { Id = project.Id, Path = project.SourcePath };
            var result = _service.Launch(projectRef, kind);
            StatusMessage = (!result.Success && result.Issue != null && !string.IsNullOrWhiteSpace(result.Issue.Code))
                ? "[" + result.Issue.Code + "] " + result.Message
                : result.Message;
        }

        private static bool RowHasLocal(ProjectOverview p)
        {
            return p != null && !string.IsNullOrWhiteSpace(p.LocalPath) &&
                   !string.Equals(p.LocalPath, "Not available", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RowHasSsh(ProjectOverview p)
        {
            return p != null && !string.IsNullOrWhiteSpace(p.SshTarget) &&
                   !string.Equals(p.SshTarget, "Not configured", StringComparison.OrdinalIgnoreCase);
        }

        private void Launch(LaunchTargetKind kind)
        {
            var projectRef = GetSelectedRef();
            if (projectRef == null)
            {
                StatusMessage = "No project selected";
                return;
            }

            var result = _service.Launch(projectRef, kind);
            if (!result.Success && result.Issue != null && !string.IsNullOrWhiteSpace(result.Issue.Code))
            {
                // Show structured code + message verbatim per ADR-004 so the
                // user sees exactly why a launch was rejected.
                StatusMessage = "[" + result.Issue.Code + "] " + result.Message;
            }
            else
            {
                StatusMessage = result.Message;
            }
        }

        private ProjectRef GetSelectedRef()
        {
            if (SelectedProject == null)
            {
                return null;
            }

            return ToProjectRef(SelectedProject);
        }

        /// <summary>
        /// Reconstructs a <see cref="ProjectRef"/> from a <see cref="ProjectOverview"/>,
        /// preserving canonical portfolio identity (StoreId and Folder) so that subsequent
        /// <c>LoadProject</c> calls stamp the correct identity back on the returned overview.
        /// For derived identity (IsStoreIdentityDerived), StoreId/Folder are cleared so every
        /// refresh re-runs resolution against current project.yml and current stores.
        /// </summary>
        private static ProjectRef ToProjectRef(ProjectOverview overview) =>
            new ProjectRef
            {
                Id = overview.Id,
                Path = overview.SourcePath,
                StoreId = overview.IsStoreIdentityDerived ? string.Empty : (overview.StoreId ?? string.Empty),
                Folder = overview.IsStoreIdentityDerived ? string.Empty : (overview.Folder ?? string.Empty)
            };

        private void ApplyProjectView()
        {
            ApplyProjectView(null);
        }

        private void ApplyProjectView(string preferredProjectId)
        {
            var items = _allProjects.ToList();

            if (!string.IsNullOrWhiteSpace(SearchText))
            {
                var search = SearchText.Trim().ToLowerInvariant();
                items = items.Where(project =>
                        (!string.IsNullOrWhiteSpace(project.DisplayName) && project.DisplayName.ToLowerInvariant().Contains(search)) ||
                        (!string.IsNullOrWhiteSpace(project.RepoLocation) && project.RepoLocation.ToLowerInvariant().Contains(search)) ||
                        (!string.IsNullOrWhiteSpace(project.LocalPath) && project.LocalPath.ToLowerInvariant().Contains(search)) ||
                        (!string.IsNullOrWhiteSpace(project.Branch) && project.Branch.ToLowerInvariant().Contains(search)) ||
                        (!string.IsNullOrWhiteSpace(project.Summary) && project.Summary.ToLowerInvariant().Contains(search)) ||
                        (!string.IsNullOrWhiteSpace(project.WorkspaceMode) && project.WorkspaceMode.ToLowerInvariant().Contains(search)) ||
                        (!string.IsNullOrWhiteSpace(project.RiskSummary) && project.RiskSummary.ToLowerInvariant().Contains(search)))
                    .ToList();
            }

            if (_stateFilter.HasValue)
            {
                items = items.Where(project => project.RepoState == _stateFilter.Value).ToList();
            }

            if (ShowAttentionOnly)
            {
                items = items.Where(NeedsAttention).ToList();
            }

            if (string.Equals(SelectedSortMode, ProjectSortModes.Name))
            {
                items = items.OrderBy(project => project.DisplayName).ToList();
            }
            else if (string.Equals(SelectedSortMode, ProjectSortModes.RecentActivity))
            {
                items = items
                    .OrderBy(project => RankActivity(project.ActivitySummary))
                    .ThenBy(project => RankRisk(project.RiskSummary))
                    .ThenBy(project => project.DisplayName)
                    .ToList();
            }
            else
            {
                items = items
                    .OrderBy(project => RankRisk(project.RiskSummary))
                    .ThenBy(project => RankActivity(project.ActivitySummary))
                    .ThenBy(project => project.DisplayName)
                    .ToList();
            }

            // Group folders: keep groups contiguous + ordered (Ungrouped last),
            // preserving the within-group sort above. OrderBy is stable.
            items = items.OrderBy(project => GroupSortKey(project.Group)).ToList();

            var selectedId = !string.IsNullOrWhiteSpace(preferredProjectId)
                ? preferredProjectId
                : (SelectedProject == null ? string.Empty : SelectedProject.Id);

            Projects.Clear();
            foreach (var item in items)
            {
                Projects.Add(new ProjectRow(item, _selectedIds.Contains(item.Id), OnRowSelectionChanged));
            }

            var match = Projects.FirstOrDefault(row => row.Id == selectedId);
            if (match != null)
            {
                SelectedRow = match;
            }
            else if (Projects.Count > 0)
            {
                SelectedRow = Projects[0];
            }
            else
            {
                SelectedRow = null;
            }

            RebuildTally();
            RaiseSelectionState();
        }

        private void OnRowSelectionChanged(ProjectRow row)
        {
            if (row == null)
            {
                return;
            }

            if (row.IsSelected)
            {
                _selectedIds.Add(row.Id);
            }
            else
            {
                _selectedIds.Remove(row.Id);
            }

            RaiseSelectionState();
        }

        private void RaiseSelectionState()
        {
            OnPropertyChanged("SelectedCount");
            OnPropertyChanged("HasSelection");
            OnPropertyChanged("SelectionSummary");
            OnPropertyChanged("AllVisibleSelected");
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }

        private void ClearSelection()
        {
            foreach (var row in Projects)
            {
                if (row.IsSelected)
                {
                    row.IsSelected = false;
                }
            }

            // Drop any ids that were checked while hidden by the active filter.
            _selectedIds.Clear();
            RaiseSelectionState();
        }

        private const int MaxBulkOpen = 8;

        private void OpenAllSelected()
        {
            var ids = _selectedIds.ToList();
            if (ids.Count == 0)
            {
                StatusMessage = "No projects selected.";
                return;
            }

            if (ids.Count > MaxBulkOpen)
            {
                StatusMessage = "Select " + MaxBulkOpen + " or fewer to open at once (you have " + ids.Count + ").";
                return;
            }

            var opened = 0;
            foreach (var id in ids)
            {
                var project = _allProjects.FirstOrDefault(p => p.Id == id);
                if (project == null)
                {
                    continue;
                }

                var kind = RowHasLocal(project)
                    ? LaunchTargetKind.Code
                    : (RowHasSsh(project) ? LaunchTargetKind.RemoteCode : LaunchTargetKind.Code);
                Launch(project, kind);
                opened++;
            }

            StatusMessage = "Opened " + opened + " project" + (opened == 1 ? string.Empty : "s") + ".";
        }

        /// <summary>
        /// Bulk-refresh the checked projects. Local-only by design (decision
        /// D-04): a bulk action must never fan out into a storm of SSH probes,
        /// so SSH/hosted repos stay strictly on-demand via the single-row Refresh.
        /// </summary>
        public async Task RefreshSelectedManyAsync()
        {
            var ids = _selectedIds.ToList();
            if (ids.Count == 0)
            {
                StatusMessage = "No projects selected.";
                return;
            }

            var targets = _allProjects
                .Where(p => ids.Contains(p.Id) && RowHasLocal(p))
                .Select(ToProjectRef)
                .ToList();

            if (targets.Count == 0)
            {
                StatusMessage = "No local clones among the selected projects.";
                return;
            }

            StatusMessage = "Refreshing " + targets.Count + " selected (local)...";
            foreach (var projectRef in targets)
            {
                ProjectOverview refreshed;
                try
                {
                    refreshed = await Task.Run(() => _service.LoadProject(projectRef, ScanPolicy.LocalOnly));
                }
                catch
                {
                    continue;
                }

                if (refreshed != null)
                {
                    ApplySeededProject(refreshed);
                }
            }

            StatusMessage = "Refreshed " + targets.Count + " selected project" +
                (targets.Count == 1 ? string.Empty : "s") + " (local).";
            _lastScanText = "last scan " + DateTime.Now.ToString("HH:mm");
            OnPropertyChanged("PortfolioSummaryLine");
        }

        // Display order for the portfolio tally: most-attention first.
        private static readonly RepoState[] TallyOrder =
        {
            RepoState.Attention,
            RepoState.Unavailable,
            RepoState.Diverged,
            RepoState.Behind,
            RepoState.Uncommitted,
            RepoState.Ahead,
            RepoState.Clean,
            RepoState.HostedOnly,
            RepoState.Unknown
        };

        private void FilterByState(PortfolioTallyItem item)
        {
            _stateFilter = (item == null || item.IsAll) ? (RepoState?)null : item.RepoState;
            ApplyProjectView();
        }

        private void RebuildTally()
        {
            var counts = new Dictionary<RepoState, int>();
            foreach (var project in _allProjects)
            {
                counts[project.RepoState] = counts.TryGetValue(project.RepoState, out var c) ? c + 1 : 1;
            }

            Tally.Clear();
            Tally.Add(new PortfolioTallyItem
            {
                IsAll = true,
                Label = "All",
                Count = _allProjects.Count,
                IsActive = !_stateFilter.HasValue
            });
            foreach (var state in TallyOrder)
            {
                if (counts.TryGetValue(state, out var n) && n > 0)
                {
                    Tally.Add(new PortfolioTallyItem
                    {
                        RepoState = state,
                        Count = n,
                        Label = TallyLabel(state),
                        IsActive = _stateFilter.HasValue && _stateFilter.Value == state
                    });
                }
            }

            OnPropertyChanged("PortfolioSummaryLine");
        }

        private static string TallyLabel(RepoState state)
        {
            switch (state)
            {
                case RepoState.Clean: return "Clean";
                case RepoState.Ahead: return "Ahead";
                case RepoState.Behind: return "Behind";
                case RepoState.Diverged: return "Diverged";
                case RepoState.Uncommitted: return "Uncommitted";
                case RepoState.Attention: return "Attention";
                case RepoState.HostedOnly: return "Hosted-only";
                case RepoState.Unavailable: return "Unavailable";
                default: return "Unknown";
            }
        }

        private static bool NeedsAttention(ProjectOverview project)
        {
            var risk = project == null ? string.Empty : project.RiskSummary;
            return !string.Equals(risk, "Healthy") &&
                   !string.Equals(risk, "Local only") &&
                   !string.Equals(risk, "Branch not published");
        }

        private static int RankRisk(string riskSummary)
        {
            if (string.IsNullOrWhiteSpace(riskSummary))
            {
                return 5;
            }

            var value = riskSummary.ToLowerInvariant();
            if (value.Contains("unavailable"))
            {
                return 0;
            }

            if (value.Contains("needs attention"))
            {
                return 1;
            }

            if (value.Contains("stale"))
            {
                return 2;
            }

            if (value.Contains("local only"))
            {
                return 3;
            }

            if (value.Contains("branch not published"))
            {
                return 3;
            }

            if (value.Contains("healthy"))
            {
                return 4;
            }

            return 5;
        }

        private static int RankActivity(string activitySummary)
        {
            if (string.IsNullOrWhiteSpace(activitySummary))
            {
                return 5;
            }

            var value = activitySummary.ToLowerInvariant();
            if (value.Contains("today"))
            {
                return 0;
            }

            if (value.Contains("updated "))
            {
                return 1;
            }

            if (value.Contains("stale"))
            {
                return 3;
            }

            return 2;
        }

    }
}
