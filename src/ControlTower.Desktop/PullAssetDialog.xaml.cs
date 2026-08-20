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
    public partial class PullAssetDialog : Window
    {
        private readonly LibraryAsset _asset;
        private readonly AssetType _assetType;
        private readonly string _libraryRoot;
        private readonly IAssetTransferService _transferService;
        private readonly ILibraryProvider _libraryProvider;
        private readonly IAuditLogger _auditLogger;
        private readonly ControlTowerService _controlTowerService;

        private AssetPushPlan _currentPlan;
        private readonly ObservableCollection<ChangeRow> _rows = new();

        /// <summary>True if the user successfully pulled at least one file.</summary>
        public bool Pulled { get; private set; }

        public PullAssetDialog(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            IAssetTransferService transferService,
            ILibraryProvider libraryProvider,
            IAuditLogger auditLogger,
            ControlTowerService controlTowerService)
        {
            InitializeComponent();

            _asset = asset;
            _assetType = assetType;
            _libraryRoot = libraryRoot;
            _transferService = transferService;
            _libraryProvider = libraryProvider;
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
            foreach (ComboBoxItem item in ProjectComboBox.Items)
            {
                if (item.IsEnabled)
                {
                    ProjectComboBox.SelectedItem = item;
                    break;
                }
            }
            UpdateResolvedSource();
        }

        private ProjectDefinition SelectedProject
            => (ProjectComboBox.SelectedItem as ComboBoxItem)?.Tag as ProjectDefinition;

        private string SelectedSourceRoot
        {
            get
            {
                var p = SelectedProject;
                if (p?.Locations == null) return null;
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
            UpdateResolvedSource();
            _rows.Clear();
            ApplyButton.IsEnabled = false;
            ChangeSummaryText.Text = string.Empty;
        }

        private void UpdateResolvedSource()
        {
            var sourceRoot = SelectedSourceRoot;
            if (string.IsNullOrWhiteSpace(sourceRoot))
            {
                ResolvedSourceText.Text = "Pick a project to resolve the source path.";
                PreviewButton.IsEnabled = false;
                return;
            }

            var targetRel = !string.IsNullOrWhiteSpace(_asset.DefaultTargetOverride)
                ? _asset.DefaultTargetOverride
                : _assetType.DefaultTarget;
            targetRel = (targetRel ?? string.Empty).Replace("{asset_id}", _asset.Id);

            ResolvedSourceText.Text = SelectedIsSsh
                ? sourceRoot + " → " + targetRel + "  (SFTP)"
                : Path.GetFullPath(Path.Combine(sourceRoot, targetRel));
            PreviewButton.IsEnabled = true;
        }

        private void PreviewClick(object sender, RoutedEventArgs e)
        {
            var sourceRoot = SelectedSourceRoot;
            if (string.IsNullOrWhiteSpace(sourceRoot)) return;

            try
            {
                StatusText.Text = SelectedIsSsh
                    ? "Connecting and downloading for diff over SFTP..."
                    : "Diffing...";
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Render);
                _currentPlan = _transferService.PreparePull(_asset, _assetType, _libraryRoot, sourceRoot);
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
            ChangeSummaryText.Text = $"{newCount} new in project · {modCount} modified · {idCount} identical";

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
            if (_currentPlan == null) return;

            foreach (var row in _rows)
            {
                row.Source.Apply = row.Apply;
            }

            ApplyButton.IsEnabled = false;
            StatusText.Text = "Pulling...";
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Render);

            // ApplyPush copies SourceAbsolutePath -> TargetAbsolutePath. For pull
            // the plan was built with project paths as Source and library paths
            // as Target, so this commits the pull correctly.
            var result = _transferService.ApplyPush(_currentPlan);
            if (result.Success)
            {
                if (result.FilesWritten > 0)
                {
                    // Stamp asset.yml with today's date so the Library window
                    // reflects that this asset was just synced.
                    try
                    {
                        _libraryProvider.TouchAsset(_libraryRoot, _asset.Id,
                            DateTime.UtcNow, SelectedProject?.Id);
                    }
                    catch
                    {
                        // Non-fatal — files are already in the library.
                    }
                    Pulled = true;
                }

                _auditLogger.RecordPush(_libraryRoot, new AuditEntry
                {
                    Asset = _asset.Id,
                    AssetVersion = _asset.Version,
                    Action = "pull",
                    TargetProject = SelectedProject?.Id ?? string.Empty,
                    TargetPath = _asset.AbsoluteRoot,
                    OnUtc = DateTime.UtcNow,
                    FilesWritten = result.FilesWritten,
                    FilesSkipped = result.FilesSkipped,
                });
                StatusText.Text = "Pulled " + result.FilesWritten + " file(s) into library; " +
                                  result.FilesSkipped + " skipped; " + result.FilesIdentical + " identical.";
                StatusText.Foreground = FindResource("PositiveBrush") as System.Windows.Media.Brush ?? StatusText.Foreground;
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
                FileChangeKind.New => "+ adopt",
                FileChangeKind.Modified => "~ overwrite",
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
