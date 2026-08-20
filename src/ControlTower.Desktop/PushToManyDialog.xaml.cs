using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;

namespace ControlTower.Desktop
{
    public partial class PushToManyDialog : Window
    {
        private readonly LibraryAsset _asset;
        private readonly AssetType _assetType;
        private readonly string _libraryRoot;
        private readonly IAssetTransferService _transferService;
        private readonly IAuditLogger _auditLogger;
        private readonly ControlTowerService _controlTowerService;
        private readonly HashSet<string> _preselectedProjectIds;

        private readonly ObservableCollection<TargetRow> _rows = new();

        public PushToManyDialog(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            IAssetTransferService transferService,
            IAuditLogger auditLogger,
            ControlTowerService controlTowerService,
            IReadOnlyCollection<string> preselectedProjectIds = null)
        {
            InitializeComponent();

            _asset = asset;
            _assetType = assetType;
            _libraryRoot = libraryRoot;
            _transferService = transferService;
            _auditLogger = auditLogger;
            _controlTowerService = controlTowerService;
            _preselectedProjectIds = preselectedProjectIds == null
                ? null
                : new HashSet<string>(preselectedProjectIds, StringComparer.OrdinalIgnoreCase);

            AssetSubtitleText.Text = $"{asset.Id}  ·  {asset.TypeId}" +
                (string.IsNullOrWhiteSpace(asset.Version) ? string.Empty : $"  ·  v{asset.Version}");

            ProjectListBox.ItemsSource = _rows;
            PopulateProjects();
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

                var detail = isSsh
                    ? "SSH (SFTP)"
                    : localExists
                        ? "Local"
                        : "(no local path)";

                _rows.Add(new TargetRow
                {
                    Project = def,
                    DisplayName = overview.DisplayName,
                    Detail = detail,
                    Enabled = usable,
                    Selected = usable && _preselectedProjectIds != null && _preselectedProjectIds.Contains(overview.Id),
                });
            }
        }

        private void ApplyClick(object sender, RoutedEventArgs e)
        {
            var targets = _rows.Where(r => r.Enabled && r.Selected).ToList();
            if (targets.Count == 0)
            {
                ShowStatus("Select at least one project.", true);
                return;
            }

            ApplyButton.IsEnabled = false;
            CancelButton.IsEnabled = false;
            ShowStatus($"Pushing to {targets.Count} project(s)...", false);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Render);

            int success = 0, failed = 0;
            foreach (var row in targets)
            {
                row.SetStatus("Pushing...", System.Windows.Media.Brushes.Gray);
                System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                    () => { }, System.Windows.Threading.DispatcherPriority.Render);

                try
                {
                    var targetRoot = !string.IsNullOrWhiteSpace(row.Project.Locations.SshTarget)
                        ? row.Project.Locations.SshTarget
                        : row.Project.Locations.LocalPath;

                    var plan = _transferService.PreparePush(_asset, _assetType, _libraryRoot, targetRoot);

                    // Multi-target push: only apply New files (safer default —
                    // never touch modified content across many projects in one go).
                    foreach (var change in plan.Changes)
                    {
                        change.Apply = change.Kind == FileChangeKind.New;
                    }

                    var result = _transferService.ApplyPush(plan);
                    if (result.Success)
                    {
                        _auditLogger.RecordPush(_libraryRoot, new AuditEntry
                        {
                            Asset = _asset.Id,
                            AssetVersion = _asset.Version,
                            Action = "push-many",
                            TargetProject = row.Project.Id,
                            TargetPath = plan.ResolvedTargetPath,
                            OnUtc = DateTime.UtcNow,
                            FilesWritten = result.FilesWritten,
                            FilesSkipped = result.FilesSkipped,
                        });
                        row.SetStatus($"OK · +{result.FilesWritten} new, {result.FilesSkipped} mod skipped", Brushes.LightGreen);
                        success++;
                    }
                    else
                    {
                        row.SetStatus("Failed: " + result.Message, Brushes.Orange);
                        failed++;
                    }
                }
                catch (Exception ex)
                {
                    row.SetStatus("Error: " + ex.Message, Brushes.Orange);
                    failed++;
                }
            }

            ShowStatus($"Done. {success} succeeded, {failed} failed.", false);
            CancelButton.IsEnabled = true;
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ShowStatus(string text, bool warn)
        {
            StatusText.Text = text;
            StatusText.Foreground = (warn
                ? FindResource("WarningBrush")
                : FindResource("SecondaryTextBrush")) as Brush
                ?? StatusText.Foreground;
        }

        private sealed class TargetRow : INotifyPropertyChanged
        {
            public ProjectDefinition Project { get; init; }
            public string DisplayName { get; init; } = string.Empty;
            public string Detail { get; init; } = string.Empty;
            public bool Enabled { get; init; }

            private bool _selected;
            public bool Selected
            {
                get => _selected;
                set { _selected = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected))); }
            }

            private string _resultStatus = string.Empty;
            public string ResultStatus
            {
                get => _resultStatus;
                private set { _resultStatus = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultStatus))); }
            }

            private Brush _resultStatusBrush = Brushes.Gray;
            public Brush ResultStatusBrush
            {
                get => _resultStatusBrush;
                private set { _resultStatusBrush = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ResultStatusBrush))); }
            }

            public void SetStatus(string text, Brush brush)
            {
                ResultStatus = text;
                ResultStatusBrush = brush;
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }
    }
}
