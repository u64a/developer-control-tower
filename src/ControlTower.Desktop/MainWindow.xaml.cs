using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Desktop.Bootstrap;
using ControlTower.Desktop.Controls;
using ControlTower.Desktop.ViewModels;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Desktop
{
    public partial class MainWindow : Window
    {
        private readonly CompositionRoot _root;
        private readonly ICredentialStore _credentialStore;
        private readonly ISshService _sshService;
        private readonly string _settingsPath;
        private readonly CancellationTokenSource _ctsExit = new CancellationTokenSource();

        private DesktopSession _session;
        private MainViewModel _viewModel;
        private IProjectCreationService _creationService;
        private IReadOnlyList<RepoStore> _currentStores;
        private string _libraryPath;
        private ControlTowerService _controlTowerService;
        private ILibraryProvider _libraryProvider;
        private IAssetTransferService _assetTransferService;
        private IAssetCaptureService _assetCaptureService;
        private IAuditLogger _auditLogger;
        private IUpdateService _updateService;
        private UpdateOptions _updateOptions;
        private WindowTitleBar _titleBar;
        private LauncherWindow _launcher;
        private bool _isRebuildingSession;

        public MainWindow(CompositionRoot root)
        {
            InitializeComponent();

            _root = root ?? throw new ArgumentNullException(nameof(root));
            _credentialStore = root.CredentialStore;
            _sshService = root.SshService;
            _settingsPath = root.SettingsPath;

            // Ctrl+K summons the in-app launcher overlay (no global OS hotkey,
            // no resident process — the app stays short-lived).
            InputBindings.Add(new KeyBinding(
                new ViewModels.DelegateCommand(OpenLauncher),
                new KeyGesture(Key.K, ModifierKeys.Control)));

            // BuildSession() does file I/O (reads settings, writes SSH config)
            // and previously blocked first paint by ~1s. It is now built off
            // the UI thread in MainWindowLoaded so the window appears at once.
            Loaded += MainWindowLoaded;
            Closed += MainWindowClosed;
        }

        private void OpenLauncher()
        {
            if (_viewModel == null)
            {
                return;
            }

            if (_launcher != null)
            {
                _launcher.Activate();
                return;
            }

            _launcher = new LauncherWindow(_viewModel, OpenLibrary) { Owner = this };
            _launcher.Closed += (_, _) => _launcher = null;
            _launcher.Show();
        }

        private void ApplySession(DesktopSession session)
        {
            try { _viewModel?.CancelBackgroundWork(); } catch { }

            _session = session;
            _currentStores = session.CurrentStores;
            _libraryPath = session.LibraryPath;
            _creationService = session.CreationService;
            _controlTowerService = session.ControlTowerService;
            _libraryProvider = session.LibraryProvider;
            _assetTransferService = session.AssetTransferService;
            _assetCaptureService = session.AssetCaptureService;
            _auditLogger = session.AuditLogger;
            _updateService = session.UpdateService;
            _updateOptions = session.UpdateOptions ?? UpdateOptions.Defaults();

            var previousSelection = _viewModel?.SelectedProject?.Id;
            _viewModel = new MainViewModel(_controlTowerService, null, _updateService, _updateOptions);
            _viewModel.PendingSelectionId = previousSelection;
            DataContext = _viewModel;
            RefreshProfileMenu();
        }

        private async void MainWindowLoaded(object sender, RoutedEventArgs e)
        {
            // First paint is done. Build the settings-dependent service graph
            // off the UI thread, then wire it up. Guarded so a second Loaded
            // (e.g. window re-show) doesn't rebuild an existing session.
            if (_session == null)
            {
                var session = await Task.Run(() => _root.BuildSession());
                ApplySession(session);
            }

            UpdateThemeButton();
            HookUpdateChip();
            await _viewModel.LoadAsync();
            SurfaceProfileIssues();

            // One-shot background seed of LOCAL repo states after first paint —
            // fills in the status lamps without blocking the UI or touching SSH.
            _ = _viewModel.SeedLocalStatesAsync();

            if (_updateOptions.AutoCheckOnLaunch)
            {
                _ = _viewModel.RunBackgroundUpdateCheckAsync(_ctsExit.Token);
            }
        }

        private void ProfileButtonClick(object sender, RoutedEventArgs e)
        {
            if (_session == null || ProfileContextMenu == null)
            {
                return;
            }

            ProfileContextMenu.PlacementTarget = ProfileButton;
            ProfileContextMenu.Placement = PlacementMode.Bottom;
            ProfileContextMenu.IsOpen = true;
        }

        private void RefreshProfileMenu()
        {
            if (ProfileContextMenu == null || ActiveProfileNameText == null)
            {
                return;
            }

            ProfileContextMenu.Items.Clear();
            if (_session == null)
            {
                ActiveProfileNameText.Text = "Loading...";
                ProfileButton.IsEnabled = false;
                return;
            }

            var state = _session.ProfileState;
            ActiveProfileNameText.Text = state.ActiveProfile.Name;
            ProfileButton.IsEnabled = !_isRebuildingSession;

            var allProjectsItem = new MenuItem
            {
                Header = WorkspaceProfilePolicy.AllProjectsProfileName,
                IsCheckable = true,
                IsChecked = state.ActiveProfile.IsSynthetic,
                Tag = WorkspaceProfilePolicy.AllProjectsProfileId
            };
            allProjectsItem.Click += ProfileMenuItemClick;
            ProfileContextMenu.Items.Add(allProjectsItem);

            foreach (var profile in state.PersistedProfiles
                .OrderBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(profile => profile.Id))
            {
                var item = new MenuItem
                {
                    Header = new TextBlock
                    {
                        Text = profile.Name,
                        MaxWidth = 280,
                        TextTrimming = TextTrimming.CharacterEllipsis
                    },
                    IsCheckable = true,
                    IsChecked = !state.ActiveProfile.IsSynthetic &&
                        state.ActiveProfile.Id == profile.Id,
                    Tag = profile.Id
                };
                item.Click += ProfileMenuItemClick;
                ProfileContextMenu.Items.Add(item);
            }

            ProfileContextMenu.Items.Add(new Separator());
            var manageItem = new MenuItem { Header = "_Manage profiles..." };
            manageItem.Click += ManageProfilesClick;
            ProfileContextMenu.Items.Add(manageItem);

            var issueText = string.Join(
                Environment.NewLine,
                state.Issues.Select(issue => issue.Message));
            ProfileWarningGlyph.Visibility = state.Issues.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            ProfileButton.ToolTip = state.Issues.Count > 0
                ? issueText
                : "Switch or manage the active workspace profile.";
        }

        private async void ProfileMenuItemClick(object sender, RoutedEventArgs e)
        {
            if (_session == null ||
                sender is not MenuItem item ||
                item.Tag is not Guid profileId ||
                _session.ProfileState.ActiveProfile.Id == profileId)
            {
                return;
            }

            try
            {
                _session.ProfileManager.SwitchProfile(profileId);
                await RebuildSessionAsync();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Could not switch workspace profile: " + ex.Message,
                    "Workspace Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private async void ManageProfilesClick(object sender, RoutedEventArgs e)
        {
            if (_session == null)
            {
                return;
            }

            PortfolioIndex canonicalPortfolio;
            try
            {
                canonicalPortfolio = _session.PortfolioProvider.LoadPortfolio();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    this,
                    "Could not load the canonical portfolio: " + ex.Message,
                    "Manage Workspace Profiles",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            var dialog = new ManageProfilesDialog(
                _session.ProfileManager,
                _session.ProfileState,
                canonicalPortfolio,
                _session.ControlTowerService)
            {
                Owner = this
            };

            if (dialog.ShowDialog() == true && dialog.Saved)
            {
                await RebuildSessionAsync();
            }
        }

        private async Task RebuildSessionAsync(bool forceAllProjects = false)
        {
            if (_isRebuildingSession)
            {
                return;
            }

            _isRebuildingSession = true;
            ProfileButton.IsEnabled = false;
            try
            {
                try { _viewModel?.CancelBackgroundWork(); } catch { }
                var session = await Task.Run(
                    () => _root.BuildSession(forceAllProjects));
                ApplySession(session);
                HookUpdateChip();
                await _viewModel.LoadAsync();
                SurfaceProfileIssues();
                _ = _viewModel.SeedLocalStatesAsync();
            }
            finally
            {
                _isRebuildingSession = false;
                ProfileButton.IsEnabled = true;
            }
        }

        private void SurfaceProfileIssues()
        {
            if (_viewModel == null ||
                _session?.ProfileState == null ||
                _session.ProfileState.Issues.Count == 0)
            {
                return;
            }

            _viewModel.StatusMessage =
                "Workspace profiles: " + _session.ProfileState.Issues[0].Message;
        }

        private void MainWindowClosed(object sender, EventArgs e)
        {
            try { _viewModel?.CancelBackgroundWork(); } catch { }
            try { _ctsExit.Cancel(); } catch { }
            try { _ctsExit.Dispose(); } catch { }
        }

        private void HookUpdateChip()
        {
            // The chip lives in the shared WindowTitleBar UserControl so the
            // chrome strip stays the single owner of all title-bar widgets.
            // We attach once on Loaded and re-attach if the chrome rebuilds.
            var titleBar = FindVisualChild<WindowTitleBar>(this);
            if (titleBar == null) return;
            if (ReferenceEquals(_titleBar, titleBar)) return;

            if (_titleBar != null)
            {
                _titleBar.UpdateChipClicked -= TitleBarOnUpdateChipClicked;
                _titleBar.LauncherRequested -= TitleBarOnLauncherRequested;
            }
            _titleBar = titleBar;
            _titleBar.UpdateChipClicked += TitleBarOnUpdateChipClicked;
            _titleBar.LauncherRequested += TitleBarOnLauncherRequested;
        }

        private void TitleBarOnLauncherRequested(object sender, EventArgs e)
        {
            OpenLauncher();
        }

        private void TitleBarOnUpdateChipClicked(object sender, EventArgs e)
        {
            UpdateChipClick();
        }

        private void UpdateChipClick()
        {
            var lastCheck = _viewModel.LastUpdateCheckResult;
            if (lastCheck == null || lastCheck.Status != UpdateStatus.UpdateAvailable || _updateService == null)
            {
                return;
            }

            var dlg = new UpdateConfirmDialog(_updateService, lastCheck) { Owner = this };
            if (dlg.ShowDialog() == true && dlg.Confirmed)
            {
                AppLogger.Info("Update", "User confirmed update — shutting down app for relaunch.");
                Application.Current.Shutdown();
            }
        }

        private static T FindVisualChild<T>(System.Windows.DependencyObject parent) where T : System.Windows.DependencyObject
        {
            if (parent == null) return null;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var deeper = FindVisualChild<T>(child);
                if (deeper != null) return deeper;
            }
            return null;
        }

        private async void SettingsClick(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var dialog = new SettingsWindow(
                _currentStores, _credentialStore, _sshService, _settingsPath, _libraryPath,
                shellLauncher: null,
                restoreServices: _session,
                updateService: _updateService,
                updateOptions: _updateOptions,
                uninstallService: _root.UninstallService,
                legacyInstallRoot: _root.LegacyInstallRoot);
            dialog.Owner = this;

            var result = dialog.ShowDialog();
            var sessionRebuilt = false;
            if (result == true && dialog.UninstallLaunched)
            {
                AppLogger.Info(
                    "Uninstall",
                    "User confirmed uninstall; shutting down for the uninstall handoff.");
                Application.Current.Shutdown();
                return;
            }

            if (result == true && dialog.Saved)
            {
                var writer = new SettingsWriter();
                writer.Write(_settingsPath, dialog.ResultStores, dialog.ResultLibraryPath, dialog.ResultUpdateOptions);

                if (dialog.UpdateLaunched)
                {
                    AppLogger.Info("Update", "Update launched from Settings — shutting down for relaunch.");
                    Application.Current.Shutdown();
                    return;
                }

                ApplySession(_root.BuildSession());
                HookUpdateChip();
                sessionRebuilt = true;
                if (_updateOptions.AutoCheckOnLaunch)
                {
                    _ = _viewModel.RunBackgroundUpdateCheckAsync(_ctsExit.Token);
                }
            }

            if (dialog.ProfileStateMutated && !sessionRebuilt)
            {
                ApplySession(_root.BuildSession());
                HookUpdateChip();
                sessionRebuilt = true;
            }

            // Refresh the main project list whenever Settings altered the
            // portfolio — either via Save (store/library changes) or via the
            // Restore / Scan sub-dialogs registering new projects.
            if ((result == true && dialog.Saved) ||
                dialog.PortfolioMutated ||
                dialog.ProfileStateMutated)
            {
                await _viewModel.LoadAsync();
            }
        }

        private async void AddProjectClick(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            if (_currentStores.Count == 0)
            {
                MessageBox.Show(this,
                    "No stores configured yet. Go to Settings first to add a local or SSH store.",
                    "Add Project", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new AddProjectWindow(_creationService, _currentStores, null, _viewModel.KnownGroups);
            dialog.Owner = this;

            var result = dialog.ShowDialog();
            if (result == true && dialog.Created)
            {
                var activeProfile = _session.ProfileState.ActiveProfile;
                bool membershipSaved;
                try
                {
                    membershipSaved = _session.ProfileManager.AppendProjectToActive(
                        activeProfile,
                        dialog.CreationRequest.ProjectId);
                }
                catch (Exception ex)
                {
                    var fallbackError = string.Empty;
                    try
                    {
                        _session.ProfileManager.SelectSyntheticFallback();
                    }
                    catch (Exception fallbackException)
                    {
                        fallbackError = Environment.NewLine + Environment.NewLine +
                            "The machine-local fallback selection could not be saved: " +
                            fallbackException.Message;
                    }

                    var showingAllProjects = false;
                    try
                    {
                        await RebuildSessionAsync(forceAllProjects: true);
                        showingAllProjects = true;
                    }
                    catch (Exception reloadException)
                    {
                        fallbackError += Environment.NewLine + Environment.NewLine +
                            "The portfolio could not be reloaded: " +
                            reloadException.Message;
                    }

                    MessageBox.Show(
                        this,
                        "The project was registered, but its active-profile membership " +
                        "could not be saved. " +
                        (showingAllProjects
                            ? "The current session has switched to All projects so the new project remains visible."
                            : "The fallback reload also failed; the project remains in the canonical portfolio and is available in Manage profiles.") +
                        Environment.NewLine + Environment.NewLine +
                        ex.Message +
                        fallbackError,
                        "Project Added - Profile Update Failed",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return;
                }

                try
                {
                    if (membershipSaved)
                    {
                        await RebuildSessionAsync();
                    }
                    else
                    {
                        await _viewModel.LoadAsync();
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(
                        this,
                        "The project and its profile membership were saved, but the portfolio " +
                        "could not be reloaded: " + ex.Message,
                        "Project Added",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }

        private async void EditProjectClick(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var selected = _viewModel.SelectedProject;
            if (selected == null)
            {
                MessageBox.Show(this, "Select a project to edit.", "Edit Project",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var editRequest = new ProjectCreationRequest
            {
                ProjectId = selected.Id,
                DisplayName = selected.DisplayName,
                Summary = selected.Summary,
                LifecycleState = string.IsNullOrWhiteSpace(selected.LifecycleState) ? "active" : selected.LifecycleState,
                StoreId = selected.StoreId ?? string.Empty,
                Folder = selected.Folder ?? string.Empty,
                GitHubUrl = selected.GitHubUrl ?? string.Empty,
                AdoUrl = selected.AdoUrl ?? string.Empty,
                Group = string.Equals(selected.Group, "Ungrouped", StringComparison.OrdinalIgnoreCase) ? string.Empty : (selected.Group ?? string.Empty)
            };

            var dialog = new AddProjectWindow(_creationService, _currentStores, editRequest, _viewModel.KnownGroups,
                // Pass the SSH target so AddProjectWindow can warn when the SSH location was
                // not matched to any configured store (service layer returned empty StoreId).
                // Only set when StoreId is empty and SshTarget is a real value — "Not configured"
                // is the sentinel for local/non-SSH projects and must not trigger the guard.
                sshTargetHint: string.IsNullOrWhiteSpace(editRequest.StoreId)
                    && !string.IsNullOrWhiteSpace(selected.SshTarget)
                    && !string.Equals(selected.SshTarget, "Not configured", StringComparison.OrdinalIgnoreCase)
                    ? selected.SshTarget
                    : null);
            dialog.Owner = this;

            var result = dialog.ShowDialog();
            if (result == true && dialog.Created)
            {
                await _viewModel.LoadAsync();
            }
        }

        private void DeleteProjectClick(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            if (!_viewModel.CanManageProject || _viewModel.SelectedProject == null)
            {
                MessageBox.Show(this, "Select a project to remove.", "Remove Project", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var result = MessageBox.Show(
                this,
                "Remove '" + _viewModel.SelectedProject.DisplayName + "' from Developer Control Tower?\n\nThis only removes it from the app. It does not delete the repo or files.",
                "Remove project",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.Yes)
            {
                _viewModel.RemoveSelectedProject();
            }
        }

        private async void RelocateProjectClick(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            if (!_viewModel.CanManageProject || _viewModel.SelectedProject == null)
            {
                MessageBox.Show(this, "Select a project to relocate.", "Relocate Project",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            if (_currentStores == null || _currentStores.Count == 0)
            {
                MessageBox.Show(this, "No stores configured. Add a store in Settings first.",
                    "Relocate Project", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var dialog = new RelocateProjectDialog(_viewModel.SelectedProject, _session)
            {
                Owner = this
            };
            dialog.ShowDialog();
            if (dialog.PortfolioMutated)
            {
                await _viewModel.LoadAsync();
            }
        }

        private void ToggleThemeClick(object sender, RoutedEventArgs e)
        {
            var app = Application.Current as App;
            if (app != null)
            {
                app.ToggleTheme();
                UpdateThemeButton();
            }
        }

        private void UpdateThemeButton()
        {
            var app = Application.Current as App;
            if (app == null)
            {
                return;
            }

            ThemeToggleText.Text = app.IsDarkMode ? "Light mode" : "Dark mode";
        }

        private void CopyOriginClick(object sender, RoutedEventArgs e)
        {
            if (_session == null) return;

            var origin = _viewModel?.SelectedProject?.OriginUrl;
            if (string.IsNullOrWhiteSpace(origin))
            {
                _viewModel.StatusMessage = "No origin URL to copy.";
                return;
            }

            try
            {
                Clipboard.SetText(origin);
                _viewModel.StatusMessage = "Origin URL copied to clipboard.";
            }
            catch
            {
                _viewModel.StatusMessage = "Could not access the clipboard.";
            }
        }

        private void CopyRepoLinkClick(object sender, RoutedEventArgs e)
        {
            var url = _viewModel?.SelectedProject?.PrimaryRepoUrl;
            if (string.IsNullOrWhiteSpace(url))
            {
                if (_viewModel != null) _viewModel.StatusMessage = "No repo link to copy.";
                return;
            }

            try
            {
                Clipboard.SetText(url);
                _viewModel.StatusMessage = "Repo link copied: " + url;
            }
            catch
            {
                _viewModel.StatusMessage = "Could not access the clipboard.";
            }
        }

        private void ProjectRowDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_viewModel == null) return;

            // Double-click a row opens the workspace: local Code if a clone
            // exists, otherwise the SSH remote. Ignore double-clicks on the
            // checkbox / inline action buttons so they keep their own behaviour.
            if (e.OriginalSource is System.Windows.Controls.Primitives.ToggleButton ||
                e.OriginalSource is System.Windows.Controls.Button)
            {
                return;
            }

            if (_viewModel.CanOpenCodeNormal && _viewModel.OpenCodeCommand.CanExecute(null))
            {
                _viewModel.OpenCodeCommand.Execute(null);
            }
            else if (_viewModel.CanOpenRemote && _viewModel.OpenRemoteCommand.CanExecute(null))
            {
                _viewModel.OpenRemoteCommand.Execute(null);
            }
        }

        private static string GroupNameOf(object sender)
        {
            return (sender as System.Windows.Controls.Expander)?.DataContext
                is System.Windows.Data.CollectionViewGroup g ? g.Name as string : null;
        }

        // Apply the persisted collapse state when each group header is realised.
        private void GroupExpanderLoaded(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            var name = GroupNameOf(sender);
            if (name != null && sender is System.Windows.Controls.Expander exp)
            {
                exp.IsExpanded = !_viewModel.IsGroupCollapsed(name);
            }
        }

        // Persist when the user expands/collapses a group folder.
        private void GroupExpanderChanged(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null) return;
            var name = GroupNameOf(sender);
            if (name != null && sender is System.Windows.Controls.Expander exp)
            {
                _viewModel.SetGroupCollapsed(name, !exp.IsExpanded);
            }
        }

        // Builds the per-row right-click menu fresh each open so the group
        // list reflects current folders; supports moving to an existing group,
        // creating a new one, or clearing to Ungrouped.
        private void RowMenuOpened(object sender, RoutedEventArgs e)
        {
            if (_viewModel == null || sender is not System.Windows.Controls.ContextMenu menu) return;

            var row = (menu.PlacementTarget as System.Windows.FrameworkElement)?.DataContext as ViewModels.ProjectRow;
            var project = row?.Project;
            if (project == null) return;

            menu.Items.Clear();
            var header = new System.Windows.Controls.MenuItem { Header = "Move to group", IsEnabled = false };
            menu.Items.Add(header);
            menu.Items.Add(new System.Windows.Controls.Separator());

            foreach (var grp in _viewModel.KnownGroups)
            {
                var item = new System.Windows.Controls.MenuItem
                {
                    Header = grp,
                    IsChecked = string.Equals(project.Group, grp, System.StringComparison.OrdinalIgnoreCase)
                };
                item.Click += (_, _) => _viewModel.MoveProjectToGroup(project, grp);
                menu.Items.Add(item);
            }

            var ungrouped = new System.Windows.Controls.MenuItem
            {
                Header = "Ungrouped",
                IsChecked = string.Equals(project.Group, "Ungrouped", System.StringComparison.OrdinalIgnoreCase)
            };
            ungrouped.Click += (_, _) => _viewModel.MoveProjectToGroup(project, string.Empty);
            menu.Items.Add(ungrouped);

            menu.Items.Add(new System.Windows.Controls.Separator());
            var newGroup = new System.Windows.Controls.MenuItem { Header = "New group…" };
            newGroup.Click += (_, _) =>
            {
                var dlg = new NewGroupDialog { Owner = this };
                if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.GroupName))
                {
                    _viewModel.MoveProjectToGroup(project, dlg.GroupName);
                }
            };
            menu.Items.Add(newGroup);
        }

        private void LibraryClick(object sender, RoutedEventArgs e)
        {
            OpenLibrary();
        }

        private void OpenLibrary()
        {
            OpenLibrary(null);
        }

        private void OpenLibrary(IReadOnlyCollection<string> preselectedProjectIds)
        {
            if (_session == null) return;

            var window = new LibraryWindow(
                _libraryProvider, _assetTransferService, _assetCaptureService, _auditLogger,
                _controlTowerService, _libraryPath, preselectedProjectIds);
            window.Owner = this;
            window.ShowDialog();
        }

        private void BulkPushFromLibraryClick(object sender, RoutedEventArgs e)
        {
            if (_session == null || _viewModel == null) return;

            var ids = _viewModel.SelectedProjectIds;
            if (ids == null || ids.Count == 0)
            {
                MessageBox.Show(this, "Select one or more projects first.", "Push from Library",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            // Open the library with the checked projects pre-selected as push
            // targets — the user picks an asset and pushes to all of them.
            OpenLibrary(ids);
        }
    }
}
