using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;

namespace ControlTower.Desktop
{
    public partial class CaptureAssetDialog : Window
    {
        private readonly IAssetCaptureService _captureService;
        private readonly ILibraryProvider _libraryProvider;
        private readonly ControlTowerService _controlTowerService;
        private readonly string _libraryRoot;
        private LibraryIndex _index;

        public bool Captured { get; private set; }
        public string CapturedAssetId { get; private set; }

        public CaptureAssetDialog(
            ILibraryProvider libraryProvider,
            IAssetCaptureService captureService,
            ControlTowerService controlTowerService,
            string libraryRoot)
        {
            InitializeComponent();

            _libraryProvider = libraryProvider;
            _captureService = captureService;
            _controlTowerService = controlTowerService;
            _libraryRoot = libraryRoot;

            _index = libraryProvider.LoadLibrary(libraryRoot);

            // Populate types from library
            foreach (var t in _index.AssetTypes)
            {
                TypeComboBox.Items.Add(new ComboBoxItem { Content = t.Id, Tag = t });
            }
            if (TypeComboBox.Items.Count > 0)
            {
                TypeComboBox.SelectedIndex = 0;
            }

            // Populate projects (local and SSH).
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
        }

        private ProjectDefinition SelectedProject
            => (ProjectComboBox.SelectedItem as ComboBoxItem)?.Tag as ProjectDefinition;

        private AssetType SelectedType
            => (TypeComboBox.SelectedItem as ComboBoxItem)?.Tag as AssetType;

        private bool SelectedIsSsh
            => !string.IsNullOrWhiteSpace(SelectedProject?.Locations?.SshTarget);

        private void ProjectComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateSourceFieldForSelection();
        }

        private void UpdateSourceFieldForSelection()
        {
            var project = SelectedProject;
            if (project == null) return;

            if (SelectedIsSsh)
            {
                // Remote relative path mode — Browse not available.
                BrowseButton.IsEnabled = false;
                SourceFolderTextBox.Text = string.Empty;
                SourceHintText.Text =
                    "SSH project (" + project.Locations.SshTarget + "). " +
                    "Type the asset folder's path INSIDE the project root, " +
                    "e.g. '.github/skills/my-skill' or 'docs/templates'. " +
                    "Files will be downloaded via SFTP.";
            }
            else
            {
                BrowseButton.IsEnabled = true;
                SourceFolderTextBox.Text = project.Locations?.LocalPath ?? string.Empty;
                SourceHintText.Text = string.Empty;
            }
        }

        private void BrowseSourceClick(object sender, RoutedEventArgs e)
        {
            var initial = !string.IsNullOrWhiteSpace(SourceFolderTextBox.Text)
                ? SourceFolderTextBox.Text
                : SelectedProject?.Locations?.LocalPath;

            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select source folder to capture",
                InitialDirectory = !string.IsNullOrWhiteSpace(initial) && Directory.Exists(initial)
                    ? initial : AppContext.BaseDirectory,
            };
            if (dlg.ShowDialog(this) == true)
            {
                SourceFolderTextBox.Text = dlg.FolderName;
                if (string.IsNullOrWhiteSpace(AssetIdTextBox.Text))
                {
                    AssetIdTextBox.Text = Path.GetFileName(dlg.FolderName.TrimEnd(Path.DirectorySeparatorChar))
                        .ToLowerInvariant().Replace(' ', '-');
                }
            }
        }

        private void CaptureClick(object sender, RoutedEventArgs e)
        {
            var project = SelectedProject;
            var type = SelectedType;
            var sourceText = (SourceFolderTextBox.Text ?? string.Empty).Trim();
            var assetId = (AssetIdTextBox.Text ?? string.Empty).Trim();

            if (project == null) { ShowStatus("Pick a source project.", true); return; }
            if (type == null) { ShowStatus("Pick an asset type.", true); return; }
            if (string.IsNullOrWhiteSpace(sourceText))
            {
                ShowStatus(SelectedIsSsh
                    ? "Enter a remote path inside the project root."
                    : "Pick a source folder.", true);
                return;
            }
            if (string.IsNullOrWhiteSpace(assetId)) { ShowStatus("Asset id is required.", true); return; }

            CaptureButton.IsEnabled = false;
            ShowStatus(SelectedIsSsh ? "Downloading via SFTP..." : "Capturing...", false);
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(
                () => { }, System.Windows.Threading.DispatcherPriority.Render);

            AssetCaptureResult result;
            if (SelectedIsSsh)
            {
                result = _captureService.CaptureFromSsh(
                    _libraryRoot, _index, assetId, type.Id,
                    project.Locations.SshTarget, sourceText, project.Id);
            }
            else
            {
                // Defence-in-depth: source must live under the project root.
                var srcFull = Path.GetFullPath(sourceText);
                var rootFull = Path.GetFullPath(project.Locations.LocalPath)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!srcFull.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(srcFull, rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    ShowStatus("Source folder must be inside the project root.", true);
                    CaptureButton.IsEnabled = true;
                    return;
                }

                result = _captureService.CaptureFromLocal(
                    _libraryRoot, _index, assetId, type.Id, srcFull, project.Id);
            }

            if (result.Success)
            {
                Captured = true;
                CapturedAssetId = result.AssetId;
                DialogResult = true;
                Close();
            }
            else
            {
                ShowStatus(result.Message, true);
                CaptureButton.IsEnabled = true;
            }
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void ShowStatus(string text, bool warn)
        {
            StatusText.Text = text;
            StatusText.Foreground = (warn
                ? FindResource("WarningBrush")
                : FindResource("SecondaryTextBrush")) as System.Windows.Media.Brush
                ?? StatusText.Foreground;
        }
    }
}
