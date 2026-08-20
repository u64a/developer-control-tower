using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Desktop.Bootstrap;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Credentials;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.Launch;
using ControlTower.Infrastructure.Ssh;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Desktop
{
    public partial class SettingsWindow : Window
    {
        private readonly ICredentialStore _credentialStore;
        private readonly ISshService _sshService;
        private readonly IShellLauncher _shellLauncher;
        private readonly string _configLocation;
        private readonly DesktopSession _restoreServices;
        private readonly IUpdateService _updateService;
        private readonly IApplicationUninstallService _uninstallService;
        private readonly string _legacyInstallRoot;
        private readonly UpdateOptions _initialUpdateOptions;
        private readonly CancellationTokenSource _updateCheckCts = new CancellationTokenSource();

        private readonly ObservableCollection<StoreEntry> _stores = new();
        private bool _isSshSelected;
        private bool _loadingAppearance;

        public SettingsWindow(
            IReadOnlyList<RepoStore> currentStores,
            ICredentialStore credentialStore,
            ISshService sshService,
            string configLocation,
            string libraryPath = null,
            IShellLauncher shellLauncher = null,
            DesktopSession restoreServices = null,
            IUpdateService updateService = null,
            UpdateOptions updateOptions = null,
            IApplicationUninstallService uninstallService = null,
            string legacyInstallRoot = null)
        {
            InitializeComponent();

            _credentialStore = credentialStore;
            _sshService = sshService;
            _shellLauncher = shellLauncher ?? new WindowsShellLauncher();
            _configLocation = configLocation;
            _restoreServices = restoreServices;
            _updateService = updateService;
            _uninstallService = uninstallService;
            _legacyInstallRoot = legacyInstallRoot ?? string.Empty;
            _initialUpdateOptions = updateOptions ?? UpdateOptions.Defaults();

            SettingsSubtitleText.Text = SettingsSubtitleFormatter.Format(configLocation);
            SettingsSubtitleText.ToolTip = string.IsNullOrWhiteSpace(configLocation)
                ? "Settings file location not resolved."
                : configLocation;
            ConfigLocationText.Text = configLocation;
            LibraryPathTextBox.Text = libraryPath ?? string.Empty;
            LogFolderText.Text = AppLogger.LogFolder;

            // Hydrate the Appearance section from the live accent preference.
            _loadingAppearance = true;
            AccentCombo.SelectedIndex =
                (Application.Current as App)?.AccentPreference == AccentPreference.WindowsAccent ? 1 : 0;
            _loadingAppearance = false;

            // Hydrate the Updates section from the bound UpdateOptions.
            UpdateBranchTextBox.Text = _initialUpdateOptions.Branch ?? string.Empty;
            UpdateAutoCheckBox.IsChecked = _initialUpdateOptions.AutoCheckOnLaunch;
            UpdateRepoRootTextBox.Text = _initialUpdateOptions.RepoRootOverride ?? string.Empty;

            if (_updateService == null)
            {
                UpdateModeText.Text = "No update provider is available in this session.";
                CheckForUpdatesButton.IsEnabled = false;
                UpdateStatusText.Text = "Update service unavailable in this session.";
            }
            else if (_updateService.ProviderKind == UpdateProviderKind.PackagedRelease)
            {
                UpdateModeText.Text =
                    "Installed releases update from their packaged GitHub channel. " +
                    "Branch and source-clone settings do not apply.";
                UpdateBranchLabel.Visibility = Visibility.Collapsed;
                UpdateBranchTextBox.Visibility = Visibility.Collapsed;
                UpdateRepoRootLabel.Visibility = Visibility.Collapsed;
                UpdateRepoRootPanel.Visibility = Visibility.Collapsed;
                UpdateRepoHint.Visibility = Visibility.Collapsed;
            }
            else
            {
                UpdateModeText.Text =
                    "Developer installations update by pulling and publishing the configured source clone.";
            }

            if (_uninstallService == null || !_uninstallService.IsAvailable)
            {
                AppManagementPanel.Visibility = Visibility.Collapsed;
            }

            if (currentStores != null)
            {
                foreach (var store in currentStores)
                {
                    _stores.Add(StoreEntry.FromModel(store));
                }
            }

            StoreListBox.ItemsSource = _stores;
            StoreListBox.DisplayMemberPath = "DisplayLabel";

            // Restore section: visible only when the host wired restore services.
            if (_restoreServices == null)
            {
                RestoreFromGitButton.IsEnabled = false;
                ScanFoldersButton.IsEnabled = false;
                RestoreUnavailableText.Visibility = Visibility.Visible;
            }

            Closed += (_, _) => { try { _updateCheckCts.Cancel(); } catch { } };
        }

        private void AccentComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loadingAppearance)
            {
                return;
            }

            var preference = AccentCombo.SelectedIndex == 1
                ? AccentPreference.WindowsAccent
                : AccentPreference.TowerCyan;
            (Application.Current as App)?.SetAccentPreference(preference);
        }

        private void RestoreFromGitClick(object sender, RoutedEventArgs e)
        {
            if (_restoreServices == null) return;
            try
            {
                var dialog = new RestoreProjectsDialog(
                    _restoreServices.PortfolioProvider,
                    _restoreServices.ProjectProvider,
                    _restoreServices.StoreProvider,
                    _restoreServices.MissingProjectScanner,
                    _restoreServices.RestoreOrchestrator,
                    _restoreServices.ProfileState.ActiveProfile)
                {
                    Owner = this
                };
                dialog.ShowDialog();
                if (dialog.PortfolioMutated)
                {
                    PortfolioMutated = true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Settings", "RestoreFromGit launch failed: " + ex.Message);
                MessageBox.Show(this,
                    "Could not open the Restore dialog: " + ex.Message,
                    "Restore from Git", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ScanFoldersClick(object sender, RoutedEventArgs e)
        {
            if (_restoreServices == null) return;
            try
            {
                // Pre-seed the scan dialog with the user's currently-configured
                // local Repo Store roots (the live list in Settings, not just
                // the saved snapshot, so unsaved edits are respected). Most
                // users keep every repo under one or two parent folders, so
                // defaulting those in means a fresh clone is one click —
                // open dialog → Scan — instead of Add folder × N → Scan.
                // SSH stores are excluded because their "root" is a remote
                // path that this scanner can't walk.
                var initialRoots = _stores
                    .Where(s => !string.Equals(s.Type, "ssh", StringComparison.OrdinalIgnoreCase))
                    .Select(s => s.Root)
                    .Where(r => !string.IsNullOrWhiteSpace(r) && System.IO.Directory.Exists(r))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var dialog = new ScanFoldersDialog(
                    _restoreServices.RepoScanService,
                    _restoreServices.ProjectRegistrationService,
                    _restoreServices.ProfileManager,
                    _restoreServices.ProfileState.ActiveProfile,
                    initialRoots)
                {
                    Owner = this
                };
                dialog.ShowDialog();
                if (dialog.PortfolioMutated)
                {
                    PortfolioMutated = true;
                }
                if (dialog.ProfileStateMutated)
                {
                    ProfileStateMutated = true;
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Settings", "ScanFolders launch failed: " + ex.Message);
                MessageBox.Show(this,
                    "Could not open the Scan dialog: " + ex.Message,
                    "Scan folders for repos", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>The resulting store list after the user saves.</summary>
        public IReadOnlyList<RepoStore> ResultStores { get; private set; }

        /// <summary>The resulting library path after the user saves (empty = default).</summary>
        public string ResultLibraryPath { get; private set; } = string.Empty;

        /// <summary>
        /// Update options as edited by the user. Always populated when the
        /// dialog closes (Save or Cancel) so the caller can persist them
        /// alongside the rest of the settings.
        /// </summary>
        public UpdateOptions ResultUpdateOptions { get; private set; }

        /// <summary>
        /// True when the user confirmed a self-update from inside the
        /// Settings dialog. The host (MainWindow) is responsible for
        /// writing settings and then calling Application.Shutdown so the
        /// background updater console can take over.
        /// </summary>
        public bool UpdateLaunched { get; private set; }

        public bool UninstallLaunched { get; private set; }

        /// <summary>True if the user clicked Save.</summary>
        public bool Saved { get; private set; }

        /// <summary>
        /// True if any portfolio-mutating sub-dialog (Restore from Git, Scan
        /// folders) registered or cloned a project during this Settings
        /// session. The host (MainWindow) checks this to decide whether to
        /// reload the portfolio after Settings closes, independent of
        /// <see cref="Saved"/>.
        /// </summary>
        public bool PortfolioMutated { get; private set; }

        /// <summary>
        /// True when a sub-dialog changed synced profile membership or the
        /// machine-local active profile selection. The host must rebuild the
        /// settings-dependent session rather than only reload its current
        /// filtered service.
        /// </summary>
        public bool ProfileStateMutated { get; private set; }

        private void AddLocalStoreClick(object sender, RoutedEventArgs e)
        {
            var id = GenerateStoreId("local");
            _stores.Add(StoreEntry.FromModel(RepoStoreDefaults.NewLocal(id)));
            StoreListBox.SelectedIndex = _stores.Count - 1;
        }

        private void AddSshStoreClick(object sender, RoutedEventArgs e)
        {
            var id = GenerateStoreId("ssh");
            _stores.Add(StoreEntry.FromModel(RepoStoreDefaults.NewSsh(id)));
            StoreListBox.SelectedIndex = _stores.Count - 1;
        }

        private void RemoveStoreClick(object sender, RoutedEventArgs e)
        {
            if (StoreListBox.SelectedIndex < 0)
            {
                return;
            }

            var selected = _stores[StoreListBox.SelectedIndex];
            var label = string.IsNullOrWhiteSpace(selected.Id) ? "this store" : "'" + selected.Id + "'";

            // ADR-005-shaped destructive confirm: clear copy, default = Cancel
            // (MessageBoxResult.No). Same pattern as MainWindow.DeleteProjectClick.
            var result = MessageBox.Show(
                this,
                "Remove store " + label + " from settings?\n\n" +
                "Projects registered under this store will no longer be locatable until it is re-added. " +
                "This does not delete any files on disk or any saved SSH password.",
                "Remove store",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                return;
            }

            _stores.RemoveAt(StoreListBox.SelectedIndex);
            StoreDetailPanel.Visibility = Visibility.Collapsed;
        }

        private void BrowseStoreRootClick(object sender, RoutedEventArgs e)
        {
            var selected = StoreListBox.SelectedItem as StoreEntry;
            if (selected == null || _isSshSelected)
            {
                return;
            }

            var initial = (StoreRootTextBox.Text ?? string.Empty).Trim();
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select repo store root folder",
                InitialDirectory = string.IsNullOrWhiteSpace(initial)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : initial,
            };
            if (dlg.ShowDialog(this) == true)
            {
                StoreRootTextBox.Text = dlg.FolderName;
            }
        }

        private void StoreListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Save current fields back to the previously selected store
            SaveCurrentFieldsToStore();

            var selected = StoreListBox.SelectedItem as StoreEntry;
            if (selected == null)
            {
                StoreDetailPanel.Visibility = Visibility.Collapsed;
                return;
            }

            StoreDetailPanel.Visibility = Visibility.Visible;
            StoreIdTextBox.Text = selected.Id;
            StoreRootTextBox.Text = selected.Root;

            _isSshSelected = string.Equals(selected.Type, "ssh", StringComparison.OrdinalIgnoreCase);
            var sshVisibility = _isSshSelected ? Visibility.Visible : Visibility.Collapsed;

            HostLabel.Visibility = sshVisibility;
            StoreHostTextBox.Visibility = sshVisibility;
            UserLabel.Visibility = sshVisibility;
            StoreUserTextBox.Visibility = sshVisibility;
            PortLabel.Visibility = sshVisibility;
            StorePortTextBox.Visibility = sshVisibility;
            CredentialPanel.Visibility = sshVisibility;
            // Local-store-only browse affordance.
            BrowseStoreRootButton.Visibility = _isSshSelected ? Visibility.Collapsed : Visibility.Visible;

            if (_isSshSelected)
            {
                StoreHostTextBox.Text = selected.Host;
                StoreUserTextBox.Text = selected.User;
                StorePortTextBox.Text = selected.Port > 0 ? selected.Port.ToString() : "22";
                SshPasswordBox.Password = string.Empty;
                HideRevealedPassword();

                // Show credential status
                var credTarget = selected.CredentialTarget;
                if (string.IsNullOrWhiteSpace(credTarget))
                {
                    credTarget = $"DCT-SSH-{selected.Id}";
                }
                var existing = _credentialStore.GetPassword(credTarget);
                CredentialStatusText.Text = string.IsNullOrEmpty(existing)
                    ? "No password stored."
                    : "Password is saved in Windows Credential Manager.";
            }
        }

        private void SavePasswordClick(object sender, RoutedEventArgs e)
        {
            var selected = StoreListBox.SelectedItem as StoreEntry;
            if (selected == null || !_isSshSelected)
            {
                return;
            }

            var password = SshPasswordBox.Password;
            if (string.IsNullOrEmpty(password))
            {
                MessageBox.Show(this, "Enter a password first.", "Save Password",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var credTarget = !string.IsNullOrWhiteSpace(selected.CredentialTarget)
                ? selected.CredentialTarget
                : $"DCT-SSH-{selected.Id}";

            selected.CredentialTarget = credTarget;
            _credentialStore.SetPassword(credTarget, password);
            CredentialStatusText.Text = "Password saved to Windows Credential Manager.";
            CredentialStatusText.Foreground = FindResource("PositiveBrush") as System.Windows.Media.Brush
                ?? CredentialStatusText.Foreground;
        }

        private async void TestConnectionClick(object sender, RoutedEventArgs e)
        {
            var selected = StoreListBox.SelectedItem as StoreEntry;
            if (selected == null || !_isSshSelected)
            {
                return;
            }

            SaveCurrentFieldsToStore();

            var host = selected.Host;
            var user = selected.User;
            int port = selected.Port > 0 ? selected.Port : 22;
            var credTarget = !string.IsNullOrWhiteSpace(selected.CredentialTarget)
                ? selected.CredentialTarget
                : $"DCT-SSH-{selected.Id}";

            var password = _credentialStore.GetPassword(credTarget);
            if (string.IsNullOrEmpty(password))
            {
                // Try the password box directly
                password = SshPasswordBox.Password;
            }

            if (string.IsNullOrEmpty(password))
            {
                CredentialStatusText.Text = "No password available. Save a password first.";
                CredentialStatusText.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush
                    ?? CredentialStatusText.Foreground;
                return;
            }

            TestConnectionButton.IsEnabled = false;
            TestConnectionProgress.Visibility = Visibility.Visible;
            CredentialStatusText.Text = "Testing connection...";
            CredentialStatusText.Foreground = FindResource("SecondaryTextBrush") as System.Windows.Media.Brush
                ?? CredentialStatusText.Foreground;

            try
            {
                var result = await Task.Run(() => _sshService.TestConnection(host, port, user, password))
                    .ConfigureAwait(true);

                if (result.Success)
                {
                    ShowConnectionSuccess();
                    return;
                }

                // ADR-005: first-time host. Offer explicit user confirmation
                // and persist the fingerprint via the host-key catalog only
                // after the user agrees to trust it.
                if (string.Equals(result.Code, "ssh/host-key-unconfirmed", StringComparison.Ordinal))
                {
                    var fingerprint = await Task.Run(
                        () => CaptureHostFingerprint(host, port, user, password)).ConfigureAwait(true);

                    if (await PromptTrustHostAsync(host, port, fingerprint))
                    {
                        if (PersistTrustedHostKey(host, port, fingerprint, out var persistError))
                        {
                            var retry = await Task.Run(
                                () => _sshService.TestConnection(host, port, user, password))
                                .ConfigureAwait(true);

                            if (retry.Success)
                            {
                                ShowConnectionSuccess();
                            }
                            else
                            {
                                ShowConnectionFailure(retry);
                            }
                        }
                        else
                        {
                            ShowConnectionFailure(SshResult.Fail("ssh/host-key-persist-failed", persistError));
                        }
                    }
                    else
                    {
                        CredentialStatusText.Text = "Connection cancelled. Host key not trusted.";
                        CredentialStatusText.Foreground = FindResource("SecondaryTextBrush") as System.Windows.Media.Brush
                            ?? CredentialStatusText.Foreground;
                    }
                    return;
                }

                ShowConnectionFailure(result);
            }
            finally
            {
                TestConnectionProgress.Visibility = Visibility.Collapsed;
                TestConnectionButton.IsEnabled = true;
            }
        }

        private void ShowConnectionSuccess()
        {
            CredentialStatusText.Text = "Connection successful!";
            CredentialStatusText.Foreground = FindResource("PositiveBrush") as System.Windows.Media.Brush
                ?? CredentialStatusText.Foreground;
        }

        private void ShowConnectionFailure(SshResult result)
        {
            // Display the structured code + message verbatim — don't invent
            // friendlier strings (per ADR-005 SSH error contract).
            var code = string.IsNullOrWhiteSpace(result.Code) ? string.Empty : "[" + result.Code + "] ";
            var message = string.IsNullOrWhiteSpace(result.Error) ? "Connection failed." : result.Error;
            CredentialStatusText.Text = "Connection failed: " + code + message;

            // Security-critical SSH failures (host-key mismatch in particular)
            // get the CriticalBrush so they don't read as routine warnings.
            // V-14: conflating Caution with Critical for security signals is
            // explicitly bad on the ADR-005 surface.
            var isCritical = !string.IsNullOrWhiteSpace(result.Code) &&
                (result.Code.StartsWith("ssh/host-key-mismatch", System.StringComparison.Ordinal) ||
                 result.Code.StartsWith("ssh/host-key-persist-failed", System.StringComparison.Ordinal));
            var brushKey = isCritical ? "CriticalBrush" : "WarningBrush";
            CredentialStatusText.Foreground = FindResource(brushKey) as System.Windows.Media.Brush
                ?? CredentialStatusText.Foreground;
        }

        /// <summary>
        /// Runs a side connection attempt against an in-memory capturing
        /// policy so the UI can show the user the actual SHA-256 fingerprint
        /// when offering trust. The real <see cref="_sshService"/> stays
        /// strict; only this throwaway probe sees the fingerprint.
        /// </summary>
        private static string CaptureHostFingerprint(string host, int port, string user, string password)
        {
            var capture = new CapturingHostKeyPolicy();
            var probe = new SshNetService(capture);
            // We expect this to fail (FirstSeen). We just want the fingerprint.
            probe.TestConnection(host, port, user, password);
            return capture.Fingerprint;
        }

        private async Task<bool> PromptTrustHostAsync(string host, int port, string fingerprint)
        {
            await Task.Yield();
            var title = "Trust SSH host?";
            var body =
                $"First-time connection to {host}:{port}.\n\n" +
                "Verify the host key fingerprint matches what the server administrator " +
                "published. If you do not recognize it, do NOT trust this host.\n\n" +
                "Fingerprint:\n" +
                (string.IsNullOrWhiteSpace(fingerprint) ? "(unavailable — connection failed before host key was received)" : fingerprint) +
                "\n\nTrust this host and save the fingerprint?";

            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                // Without a fingerprint we cannot meet ADR-005's explicit-confirmation
                // requirement, so refuse to persist anything.
                MessageBox.Show(this, body, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                return false;
            }

            var answer = MessageBox.Show(
                this, body, title, MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            return answer == MessageBoxResult.Yes;
        }

        private bool PersistTrustedHostKey(string host, int port, string fingerprint, out string error)
        {
            error = string.Empty;
            if (string.IsNullOrWhiteSpace(fingerprint))
            {
                error = "Cannot persist an empty fingerprint.";
                return false;
            }

            try
            {
                // TrustOnFirstUseHostKeyPolicy.Evaluate writes the fingerprint
                // into the same credential-store target StrictHostKeyPolicy
                // reads on the next attempt.
                var policy = new TrustOnFirstUseHostKeyPolicy(_credentialStore);
                var decision = policy.Evaluate(host, port, fingerprint);
                if (!decision.Accept)
                {
                    error = string.IsNullOrWhiteSpace(decision.Message)
                        ? "Host-key policy refused to persist the fingerprint."
                        : decision.Message;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private sealed class CapturingHostKeyPolicy : IHostKeyPolicy
        {
            public string Fingerprint { get; private set; } = string.Empty;

            public HostKeyDecision Evaluate(string host, int port, string fingerprint)
            {
                Fingerprint = fingerprint ?? string.Empty;
                // Always refuse so the probe never actually establishes a
                // session — the UI is responsible for prompting the user
                // before any real connection is made.
                return new HostKeyDecision(
                    HostKeyVerdict.FirstSeen,
                    accept: false,
                    "ssh/host-key-unconfirmed",
                    $"Captured fingerprint for {host}:{port}.");
            }
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            SaveCurrentFieldsToStore();

            // Validate
            foreach (var store in _stores)
            {
                if (string.IsNullOrWhiteSpace(store.Id))
                {
                    MessageBox.Show(this, "All stores must have an ID.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (string.IsNullOrWhiteSpace(store.Root))
                {
                    MessageBox.Show(this, $"Store '{store.Id}' must have a root path.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (store.Type == "ssh" && string.IsNullOrWhiteSpace(store.Host))
                {
                    MessageBox.Show(this, $"SSH store '{store.Id}' must have a host.", "Validation",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }

            // Check for duplicate IDs
            var ids = _stores.Select(s => s.Id.ToLowerInvariant()).ToList();
            if (ids.Distinct().Count() != ids.Count)
            {
                MessageBox.Show(this, "Store IDs must be unique.", "Validation",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            ResultStores = _stores.Select(s => s.ToModel()).ToList();
            ResultLibraryPath = (LibraryPathTextBox.Text ?? string.Empty).Trim();
            ResultUpdateOptions = CaptureUpdateOptions();
            Saved = true;
            DialogResult = true;
            Close();
        }

        private UpdateOptions CaptureUpdateOptions()
        {
            var branchRaw = (UpdateBranchTextBox.Text ?? string.Empty).Trim();
            var branch = string.IsNullOrWhiteSpace(branchRaw) ? "main" : branchRaw;
            var autoCheck = UpdateAutoCheckBox.IsChecked == true;
            var repoRootOverride = (UpdateRepoRootTextBox.Text ?? string.Empty).Trim();
            return new UpdateOptions(branch, autoCheck, repoRootOverride);
        }

        private void BrowseUpdateRepoRootClick(object sender, RoutedEventArgs e)
        {
            var initial = (UpdateRepoRootTextBox.Text ?? string.Empty).Trim();
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Developer Control Tower repo root",
                InitialDirectory = string.IsNullOrWhiteSpace(initial)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                    : initial,
            };
            if (dlg.ShowDialog(this) == true)
            {
                UpdateRepoRootTextBox.Text = dlg.FolderName;
            }
        }

        private async void CheckForUpdatesNowClick(object sender, RoutedEventArgs e)
        {
            if (_updateService == null) return;

            var options = CaptureUpdateOptions();
            CheckForUpdatesButton.IsEnabled = false;
            UpdateStatusText.Text =
                _updateService.ProviderKind == UpdateProviderKind.PackagedRelease
                    ? "Checking GitHub Releases..."
                    : "Checking source repository...";
            UpdateStatusText.Foreground = TryFindResource("SecondaryTextBrush") as Brush ?? UpdateStatusText.Foreground;

            UpdateCheckResult result = null;
            try
            {
                result = await _updateService.CheckForUpdatesAsync(options, _updateCheckCts.Token).ConfigureAwait(true);
            }

            catch (Exception ex)
            {
                AppLogger.Warn("Update", "Manual check failed: " + ex.Message);
                UpdateStatusText.Text = "Check failed: " + ex.Message;
                UpdateStatusText.Foreground = TryFindResource("WarningBrush") as Brush ?? UpdateStatusText.Foreground;
                CheckForUpdatesButton.IsEnabled = true;
                return;
            }

            var current = result.Provider == UpdateProviderKind.PackagedRelease
                ? (string.IsNullOrWhiteSpace(result.CurrentVersion)
                    ? "(unknown)"
                    : result.CurrentVersion)
                : (string.IsNullOrWhiteSpace(result.CurrentSha)
                    ? "(unknown)"
                    : (result.CurrentSha.Length >= 8
                        ? result.CurrentSha.Substring(0, 8)
                        : result.CurrentSha));
            UpdateStatusText.Text =
                $"Last check: {DateTime.Now:HH:mm:ss}. Current build: {current}. {result.Message}";
            UpdateStatusText.Foreground = TryFindResource("SecondaryTextBrush") as Brush ?? UpdateStatusText.Foreground;

            // Always pop the confirmation dialog so the user can see the
            // current state — even when there's nothing to update.
            try
            {
                var dlg = new UpdateConfirmDialog(_updateService, result) { Owner = this };
                dlg.ShowDialog();
                if (dlg.Confirmed)
                {
                    // Persist the user's freshly edited options through Save
                    // so the relaunched app uses them, then let the host
                    // shut us down to release the exe for the updater.
                    ResultStores = _stores.Select(s => s.ToModel()).ToList();
                    ResultLibraryPath = (LibraryPathTextBox.Text ?? string.Empty).Trim();
                    ResultUpdateOptions = options;
                    Saved = true;
                    UpdateLaunched = true;
                    DialogResult = true;
                    Close();
                }
            }
            catch (Exception ex)
            {
                AppLogger.Error("Update", "Open update dialog failed: " + ex.Message, ex);
            }
            finally
            {
                CheckForUpdatesButton.IsEnabled = true;
            }
        }

        private void UninstallAppClick(object sender, RoutedEventArgs e)
        {
            if (_uninstallService == null || !_uninstallService.IsAvailable)
            {
                return;
            }

            var configRoot = System.IO.Path.GetDirectoryName(_configLocation) ??
                string.Empty;
            var dialog = new UninstallDialog(
                configRoot,
                (LibraryPathTextBox.Text ?? string.Empty).Trim(),
                _legacyInstallRoot)
            {
                Owner = this
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            var result = _uninstallService.Launch(
                dialog.SelectedMode,
                System.Diagnostics.Process.GetCurrentProcess().Id);
            if (!result.Started)
            {
                MessageBox.Show(
                    this,
                    result.Message,
                    "Uninstall Developer Control Tower",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                return;
            }

            UninstallLaunched = true;
            DialogResult = true;
            Close();
        }

        private void BrowseLibraryClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select asset library folder",
                InitialDirectory = string.IsNullOrWhiteSpace(LibraryPathTextBox.Text)
                    ? AppContext.BaseDirectory
                    : LibraryPathTextBox.Text,
            };
            if (dlg.ShowDialog(this) == true)
            {
                LibraryPathTextBox.Text = dlg.FolderName;
            }
        }

        private void OpenLogFolderClick(object sender, RoutedEventArgs e)
        {
            var folder = AppLogger.LogFolder;
            try
            {
                System.IO.Directory.CreateDirectory(folder);
                _shellLauncher.Open(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Could not open log folder: " + ex.Message, "Logs",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // L-06: press-and-hold reveal. Default hidden; while the user
        // holds the eye button down we mirror the password into a sibling
        // TextBox. Releasing the mouse (or leaving the button) hides it.
        // No click-to-toggle — peeking is always explicit.
        private void RevealPasswordButton_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            ShowRevealedPassword();
            RevealPasswordButton.CaptureMouse();
        }

        private void RevealPasswordButton_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            RevealPasswordButton.ReleaseMouseCapture();
            HideRevealedPassword();
        }

        private void RevealPasswordButton_MouseLeave(object sender, MouseEventArgs e)
        {
            if (RevealPasswordButton.IsMouseCaptured)
            {
                return;
            }
            HideRevealedPassword();
        }

        private void ShowRevealedPassword()
        {
            SshPasswordRevealBox.Text = SshPasswordBox.Password ?? string.Empty;
            SshPasswordRevealBox.Visibility = Visibility.Visible;
            SshPasswordBox.Visibility = Visibility.Collapsed;
        }

        private void HideRevealedPassword()
        {
            SshPasswordRevealBox.Visibility = Visibility.Collapsed;
            SshPasswordRevealBox.Text = string.Empty;
            SshPasswordBox.Visibility = Visibility.Visible;
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void SaveCurrentFieldsToStore()
        {
            var selected = StoreListBox.SelectedItem as StoreEntry;
            if (selected == null)
            {
                return;
            }

            var oldId = selected.Id;
            selected.Id = (StoreIdTextBox.Text ?? string.Empty).Trim();
            selected.Root = (StoreRootTextBox.Text ?? string.Empty).Trim();

            if (_isSshSelected)
            {
                selected.Host = (StoreHostTextBox.Text ?? string.Empty).Trim();
                selected.User = (StoreUserTextBox.Text ?? string.Empty).Trim();
                if (int.TryParse(StorePortTextBox.Text, out int port))
                {
                    selected.Port = port;
                }
                if (string.IsNullOrWhiteSpace(selected.CredentialTarget))
                {
                    selected.CredentialTarget = $"DCT-SSH-{selected.Id}";
                }
            }

            // Refresh display label in listbox
            if (oldId != selected.Id)
            {
                var idx = StoreListBox.SelectedIndex;
                StoreListBox.ItemsSource = null;
                StoreListBox.ItemsSource = _stores;
                StoreListBox.DisplayMemberPath = "DisplayLabel";
                if (idx >= 0 && idx < _stores.Count)
                {
                    StoreListBox.SelectedIndex = idx;
                }
            }
        }

        private string GenerateStoreId(string prefix)
        {
            var existing = _stores.Select(s => s.Id.ToLowerInvariant()).ToHashSet();
            if (!existing.Contains(prefix))
            {
                return prefix;
            }

            for (int i = 2; i < 100; i++)
            {
                var candidate = $"{prefix}{i}";
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }

            return $"{prefix}-{Guid.NewGuid().ToString("N").Substring(0, 4)}";
        }

        private sealed class StoreEntry
        {
            public string Id { get; set; } = string.Empty;
            public string Type { get; set; } = "local";
            public string Root { get; set; } = string.Empty;
            public string Host { get; set; } = string.Empty;
            public string User { get; set; } = string.Empty;
            public int Port { get; set; }
            public string CredentialTarget { get; set; } = string.Empty;

            public string DisplayLabel => $"[{Type}] {Id}  —  {Root}";

            public static StoreEntry FromModel(RepoStore store)
            {
                return new StoreEntry
                {
                    Id = store.Id,
                    Type = store.Type,
                    Root = store.Root,
                    Host = store.Host,
                    User = store.User,
                    Port = store.Port,
                    CredentialTarget = store.CredentialTarget
                };
            }

            public RepoStore ToModel()
            {
                return new RepoStore
                {
                    Id = Id,
                    Type = Type,
                    Root = Root,
                    Host = Host,
                    User = User,
                    Port = Port,
                    CredentialTarget = CredentialTarget
                };
            }
        }
    }
}
