using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;

namespace ControlTower.Desktop
{
    public partial class PushAssetDialog : Window
    {
        private readonly LibraryAsset _asset;
        private readonly AssetType _assetType;
        private readonly string _libraryRoot;
        private readonly IAssetTransferService _transferService;
        private readonly IAuditLogger _auditLogger;
        private readonly ControlTowerService _controlTowerService;

        private AssetPushPlan _currentPlan;
        private readonly ObservableCollection<ChangeRow> _rows = new();

        public PushAssetDialog(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            IAssetTransferService transferService,
            IAuditLogger auditLogger,
            ControlTowerService controlTowerService)
        {
            InitializeComponent();

            _asset = asset;
            _assetType = assetType;
            _libraryRoot = libraryRoot;
            _transferService = transferService;
            _auditLogger = auditLogger;
            _controlTowerService = controlTowerService;

            AssetSubtitleText.Text = $"{asset.Id}  ·  {asset.TypeId}" +
                (string.IsNullOrWhiteSpace(asset.Version) ? string.Empty : $"  ·  v{asset.Version}");

            ChangesGrid.ItemsSource = _rows;
            UpdateEmptyPreviewVisibility();

            PopulateProjects();
        }

        private void UpdateEmptyPreviewVisibility()
        {
            var empty = _rows.Count == 0;
            if (EmptyPreview != null)
            {
                EmptyPreview.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
            }
            if (ChangesGrid != null)
            {
                ChangesGrid.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
            }
        }

        private void PopulateProjects()
        {
            ProjectComboBox.Items.Clear();

            // Capture full project info up-front so we can detect SSH targets.
            foreach (var overview in _controlTowerService.LoadPortfolio())
            {
                var def = _controlTowerService.GetProjectDefinition(
                    new ProjectRef { Id = overview.Id, Path = overview.SourcePath });

                bool isSsh = def?.Locations != null
                    && !string.IsNullOrWhiteSpace(def.Locations.SshTarget);

                bool localExists = def?.Locations != null
                    && !string.IsNullOrWhiteSpace(def.Locations.LocalPath)
                    && Directory.Exists(def.Locations.LocalPath);

                bool usable = isSsh || localExists;

                var item = new ComboBoxItem
                {
                    Tag = def,
                    Content = overview.DisplayName +
                              (isSsh ? "  —  SSH" : string.Empty) +
                              (!usable ? "  —  (no local path)" : string.Empty),
                    IsEnabled = usable,
                };
                ProjectComboBox.Items.Add(item);
            }

            if (ProjectComboBox.Items.Count > 0)
            {
                foreach (ComboBoxItem item in ProjectComboBox.Items)
                {
                    if (item.IsEnabled)
                    {
                        ProjectComboBox.SelectedItem = item;
                        break;
                    }
                }
            }

            UpdateResolvedTarget();
        }

        private ProjectDefinition SelectedProject
            => (ProjectComboBox.SelectedItem as ComboBoxItem)?.Tag as ProjectDefinition;

        private string SelectedTargetRoot
        {
            get
            {
                var p = SelectedProject;
                if (p?.Locations == null) return null;
                // Prefer SshTarget when present so SSH-hosted projects route via SFTP.
                if (!string.IsNullOrWhiteSpace(p.Locations.SshTarget))
                {
                    return p.Locations.SshTarget;
                }
                return p.Locations.LocalPath;
            }
        }

        private bool SelectedIsSsh
            => !string.IsNullOrWhiteSpace(SelectedProject?.Locations?.SshTarget);

        private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateResolvedTarget();
            _rows.Clear();
            ApplyButton.IsEnabled = false;
            ChangeSummaryText.Text = string.Empty;
        }

        private void UpdateResolvedTarget()
        {
            var targetRoot = SelectedTargetRoot;
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                ResolvedTargetText.Text = "Pick a project to resolve the target path.";
                PreviewButton.IsEnabled = false;
                return;
            }

            var targetRel = !string.IsNullOrWhiteSpace(_asset.DefaultTargetOverride)
                ? _asset.DefaultTargetOverride
                : _assetType.DefaultTarget;
            targetRel = (targetRel ?? string.Empty).Replace("{asset_id}", _asset.Id);

            // For SSH targets show the host:remotepath shape; for local show the
            // resolved absolute path.
            if (SelectedIsSsh)
            {
                ResolvedTargetText.Text = targetRoot + " → " + targetRel + "  (SFTP)";
            }
            else
            {
                ResolvedTargetText.Text = Path.GetFullPath(Path.Combine(targetRoot, targetRel));
            }
            PreviewButton.IsEnabled = true;
        }

        private void PreviewClick(object sender, RoutedEventArgs e)
        {
            var targetRoot = SelectedTargetRoot;
            if (string.IsNullOrWhiteSpace(targetRoot))
            {
                return;
            }

            try
            {
                StatusText.Text = SelectedIsSsh
                    ? "Connecting and diffing over SFTP..."
                    : "Diffing...";
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Render);
                _currentPlan = _transferService.PreparePush(_asset, _assetType, _libraryRoot, targetRoot);
            }
            catch (Exception ex)
            {
                StatusText.Text = "Preview failed: " + ex.Message;
                StatusText.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush ?? StatusText.Foreground;
                return;
            }

            _rows.Clear();
            foreach (var change in _currentPlan.Changes.OrderBy(c => c.Kind).ThenBy(c => c.RelativePath))
            {
                _rows.Add(new ChangeRow(change));
            }
            UpdateEmptyPreviewVisibility();

            int newCount = _currentPlan.Changes.Count(c => c.Kind == FileChangeKind.New);
            int modCount = _currentPlan.Changes.Count(c => c.Kind == FileChangeKind.Modified);
            int idCount = _currentPlan.Changes.Count(c => c.Kind == FileChangeKind.Identical);

            ChangeSummaryText.Text = $"{newCount} new · {modCount} modified · {idCount} identical";

            if (_currentPlan.Warnings.Count > 0)
            {
                StatusText.Text = "Warnings: " + string.Join(" | ", _currentPlan.Warnings);
                StatusText.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush ?? StatusText.Foreground;
            }
            else
            {
                StatusText.Text = string.Empty;
            }

            ApplyButton.IsEnabled = _currentPlan.Changes.Count > 0;
        }

        private void SelectAllClick(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.Apply = true;
        }

        private void SelectNoneClick(object sender, RoutedEventArgs e)
        {
            foreach (var row in _rows) row.Apply = false;
        }

        private void ApplyClick(object sender, RoutedEventArgs e)
        {
            if (_currentPlan == null)
            {
                return;
            }

            // Sync row Apply state back into the plan.
            foreach (var row in _rows)
            {
                row.Source.Apply = row.Apply;
            }

            ApplyButton.IsEnabled = false;
            StatusText.Text = SelectedIsSsh ? "Uploading over SFTP..." : "Applying...";
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Render);

            var result = _transferService.ApplyPush(_currentPlan);
            if (result.Success)
            {
                _auditLogger.RecordPush(_libraryRoot, new AuditEntry
                {
                    Asset = _asset.Id,
                    AssetVersion = _asset.Version,
                    Action = "push",
                    TargetProject = SelectedProject?.Id ?? string.Empty,
                    TargetPath = _currentPlan.ResolvedTargetPath,
                    OnUtc = DateTime.UtcNow,
                    FilesWritten = result.FilesWritten,
                    FilesSkipped = result.FilesSkipped,
                });

                StatusText.Text = result.Message;
                StatusText.Foreground = FindResource("PositiveBrush") as System.Windows.Media.Brush ?? StatusText.Foreground;
                ApplyButton.IsEnabled = false;
            }
            else
            {
                StatusText.Text = result.Message;
                StatusText.Foreground = FindResource("WarningBrush") as System.Windows.Media.Brush ?? StatusText.Foreground;
            }
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private sealed class ChangeRow : INotifyPropertyChanged
        {
            public ChangeRow(FileChange source)
            {
                Source = source;
                _apply = source.Apply;
            }

            public FileChange Source { get; }
            public string RelativePath => Source.RelativePath;
            public string KindLabel => Source.Kind switch
            {
                FileChangeKind.New => "+ new",
                FileChangeKind.Modified => "~ modified",
                FileChangeKind.Identical => "  identical",
                _ => Source.Kind.ToString(),
            };
            public string SourceSizeLabel => FormatSize(Source.SourceSize);
            public string TargetSizeLabel => Source.TargetSize.HasValue ? FormatSize(Source.TargetSize.Value) : "-";

            private bool _apply;
            public bool Apply
            {
                get => _apply;
                set
                {
                    if (_apply != value)
                    {
                        _apply = value;
                        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Apply)));
                    }
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private static string FormatSize(long bytes)
            {
                if (bytes < 1024) return bytes + " B";
                if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.#") + " KB";
                return (bytes / (1024.0 * 1024)).ToString("0.#") + " MB";
            }
        }
    }
}
