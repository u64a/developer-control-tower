using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;

namespace ControlTower.Desktop
{
    public partial class AddProjectWindow : Window
    {
        private readonly IProjectCreationService _creationService;
        private readonly IReadOnlyList<RepoStore> _stores;
        private readonly bool _hasUnresolvedSsh;
        private readonly string _missingStoreId;

        public AddProjectWindow(
            IProjectCreationService creationService,
            IReadOnlyList<RepoStore> stores,
            ProjectCreationRequest existingRequest = null,
            IReadOnlyList<string> knownGroups = null,
            string sshTargetHint = null)
        {
            InitializeComponent();

            _creationService = creationService;
            _stores = stores ?? Array.Empty<RepoStore>();

            // Detect unresolved SSH edit: StoreId is empty but the project has SSH identity.
            // sshTargetHint is set by the caller only when the service layer failed to derive
            // a StoreId from the SSH target (i.e. no configured store matched). In that state
            // the dialog must NOT silently default to the Local store.
            _hasUnresolvedSsh = existingRequest != null
                && string.IsNullOrWhiteSpace(existingRequest.StoreId)
                && !string.IsNullOrWhiteSpace(sshTargetHint);

            // Detect missing configured store: StoreId is nonempty but no current store matches.
            _missingStoreId = null;
            if (existingRequest != null && !string.IsNullOrWhiteSpace(existingRequest.StoreId))
            {
                bool found = false;
                foreach (var s in _stores)
                {
                    if (string.Equals(s.Id, existingRequest.StoreId, StringComparison.OrdinalIgnoreCase))
                    {
                        found = true;
                        break;
                    }
                }
                if (!found)
                    _missingStoreId = existingRequest.StoreId;
            }

            if (knownGroups != null)
            {
                foreach (var grp in knownGroups)
                {
                    GroupComboBox.Items.Add(grp);
                }
            }
            // Populate store dropdown
            foreach (var store in _stores)
            {
                var label = store.IsSsh
                    ? $"{store.Id}  (SSH — {store.Host}:{store.Root})"
                    : $"{store.Id}  ({store.Root})";
                StoreComboBox.Items.Add(new ComboBoxItem { Content = label, Tag = store.Id });
            }

            if (StoreComboBox.Items.Count > 0)
            {
                StoreComboBox.SelectedIndex = 0;
            }

            if (existingRequest != null)
            {
                CreationRequest = existingRequest;

                DialogTitleTextBlock.Text = "Edit project";
                DialogSubtitleTextBlock.Text = "Update the project metadata.";
                SaveButtonTextBlock.Text = "Update";

                DisplayNameTextBox.Text = existingRequest.DisplayName;
                SummaryTextBox.Text = existingRequest.Summary;
                FolderNameTextBox.Text = existingRequest.Folder;
                GitHubUrlTextBox.Text = existingRequest.GitHubUrl;
                AdoUrlTextBox.Text = existingRequest.AdoUrl;
                GroupComboBox.Text = existingRequest.Group ?? string.Empty;

                // Select the matching store
                for (int i = 0; i < StoreComboBox.Items.Count; i++)
                {
                    var item = StoreComboBox.Items[i] as ComboBoxItem;
                    if (item?.Tag is string storeId &&
                        string.Equals(storeId, existingRequest.StoreId, StringComparison.OrdinalIgnoreCase))
                    {
                        StoreComboBox.SelectedIndex = i;
                        break;
                    }
                }

                var lifecycle = string.IsNullOrWhiteSpace(existingRequest.LifecycleState)
                    ? "active"
                    : existingRequest.LifecycleState.Trim();
                for (int i = 0; i < LifecycleComboBox.Items.Count; i++)
                {
                    var item = LifecycleComboBox.Items[i] as ComboBoxItem;
                    if (item != null && string.Equals(item.Content?.ToString(), lifecycle, StringComparison.OrdinalIgnoreCase))
                    {
                        LifecycleComboBox.SelectedIndex = i;
                        break;
                    }
                }
            }

            DisplayNameTextBox.TextChanged += (_, _) => UpdateResolvedPath();
            FolderNameTextBox.TextChanged += (_, _) => UpdateResolvedPath();

            // When SSH identity is unresolved or the configured store is missing,
            // clear and lock the store selector so the default index-0 selection
            // does not leak through.
            if (_hasUnresolvedSsh || _missingStoreId != null)
            {
                StoreComboBox.SelectedIndex = -1;
                StoreComboBox.IsEnabled = false;
            }

            // Refresh resolved path after all fields and event handlers are wired.
            UpdateResolvedPath();
        }

        /// <summary>The creation request (set after successful save).</summary>
        public ProjectCreationRequest CreationRequest { get; private set; }

        /// <summary>True if the project was created/adopted successfully.</summary>
        public bool Created { get; private set; }

        private void StoreComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateResolvedPath();
        }

        private void UpdateResolvedPath()
        {
            if (_hasUnresolvedSsh)
            {
                StoreHintText.Text = "⚠ SSH store not matched — configure a matching store in Settings.";
                StoreHintText.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush
                    ?? StoreHintText.Foreground;
                ResolvedPathText.Text = "SSH location cannot be resolved. Please cancel.";
                return;
            }

            if (_missingStoreId != null)
            {
                StoreHintText.Text = $"⚠ Configured store '{_missingStoreId}' no longer exists.";
                StoreHintText.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush
                    ?? StoreHintText.Foreground;
                ResolvedPathText.Text = "Store missing — re-add it in Settings or cancel.";
                return;
            }

            var store = GetSelectedStore();
            if (store == null)
            {
                ResolvedPathText.Text = "No stores configured. Go to Settings to add one.";
                return;
            }

            var folder = (FolderNameTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(folder))
            {
                // Derive from display name
                folder = BuildProjectId((DisplayNameTextBox.Text ?? string.Empty).Trim());
            }

            if (string.IsNullOrWhiteSpace(folder))
            {
                ResolvedPathText.Text = $"Will be created in: {store.Root}";
                return;
            }

            if (store.IsSsh)
            {
                ResolvedPathText.Text = $"Will be at: {store.Host}:{store.Root}\\{folder}";
                StoreHintText.Text = $"SSH remote — {store.User}@{store.Host}";
            }
            else
            {
                ResolvedPathText.Text = $"Will be at: {store.Root}\\{folder}";
                StoreHintText.Text = "Local store";
            }
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            if (_hasUnresolvedSsh)
            {
                MessageBox.Show(this,
                    "This project's SSH location does not match any configured store.\n\n" +
                    "Saving now would silently move it to a local store, corrupting the project.\n\n" +
                    "Please cancel and configure a matching SSH store in Settings first.",
                    "SSH Store Not Matched",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (_missingStoreId != null)
            {
                MessageBox.Show(this,
                    $"The configured store '{_missingStoreId}' no longer exists.\n\n" +
                    "Re-add the store in Settings before saving, or cancel.",
                    "Store Not Found",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var displayName = (DisplayNameTextBox.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(displayName))
            {
                MessageBox.Show(this, "Display name is required.", "Add Project",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var store = GetSelectedStore();
            if (store == null)
            {
                MessageBox.Show(this, "No store selected. Go to Settings to configure stores first.", "Add Project",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var folder = (FolderNameTextBox.Text ?? string.Empty).Trim();
            var lifecycleItem = LifecycleComboBox.SelectedItem as ComboBoxItem;

            var lifecycle = lifecycleItem?.Content?.ToString();
            if (string.IsNullOrWhiteSpace(lifecycle))
            {
                lifecycle = "active";
            }

            var request = new ProjectCreationRequest
            {
                ProjectId = CreationRequest?.ProjectId ?? string.Empty,
                DisplayName = displayName,
                Summary = (SummaryTextBox.Text ?? string.Empty).Trim(),
                LifecycleState = lifecycle,
                StoreId = store.Id,
                Folder = folder,
                GitHubUrl = (GitHubUrlTextBox.Text ?? string.Empty).Trim(),
                AdoUrl = (AdoUrlTextBox.Text ?? string.Empty).Trim(),
                Group = (GroupComboBox.Text ?? string.Empty).Trim(),
                AdoptExisting = true
            };

            StatusText.Text = store.IsSsh
                ? "Connecting to SSH remote..."
                : "Creating project...";
            StatusText.Foreground = FindResource("SecondaryTextBrush") as System.Windows.Media.Brush
                ?? StatusText.Foreground;

            // Force UI update
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Render);

            var result = _creationService.CreateProject(request);

            if (result.Success)
            {
                CreationRequest = request;
                CreationRequest.ProjectId = result.ProjectId;
                Created = true;
                DialogResult = true;
                Close();
            }
            else
            {
                StatusText.Text = result.Message;
                StatusText.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush
                    ?? StatusText.Foreground;
            }
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private RepoStore GetSelectedStore()
        {
            var selected = StoreComboBox.SelectedItem as ComboBoxItem;
            if (selected?.Tag is not string storeId)
            {
                return null;
            }

            return _stores.FirstOrDefault(s =>
                string.Equals(s.Id, storeId, StringComparison.OrdinalIgnoreCase));
        }

        private static string BuildProjectId(string displayName)
        {
            if (string.IsNullOrWhiteSpace(displayName))
            {
                return string.Empty;
            }

            var builder = new System.Text.StringBuilder();
            foreach (var c in displayName.Trim().ToLowerInvariant())
            {
                if ((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9'))
                {
                    builder.Append(c);
                }
                else if (builder.Length == 0 || builder[builder.Length - 1] != '-')
                {
                    builder.Append('-');
                }
            }

            return builder.ToString().Trim('-');
        }
    }
}
