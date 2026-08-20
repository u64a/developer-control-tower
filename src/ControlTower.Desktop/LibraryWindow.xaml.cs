using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;

namespace ControlTower.Desktop
{
    public partial class LibraryWindow : Window
    {
        private const int FileBatchSize = 64;

        private readonly ILibraryProvider _libraryProvider;
        private readonly IAssetTransferService _transferService;
        private readonly IAssetCaptureService _captureService;
        private readonly IAuditLogger _auditLogger;
        private readonly ControlTowerService _controlTowerService;
        private readonly string _libraryRoot;
        private readonly IReadOnlyCollection<string> _preselectedProjectIds;
        private readonly ObservableCollection<string> _assetFiles = new();

        private LibraryIndex _index;
        private CancellationTokenSource _fileEnumerationCts;

        public LibraryWindow(
            ILibraryProvider libraryProvider,
            IAssetTransferService transferService,
            IAssetCaptureService captureService,
            IAuditLogger auditLogger,
            ControlTowerService controlTowerService,
            string libraryRoot,
            IReadOnlyCollection<string> preselectedProjectIds = null)
        {
            InitializeComponent();

            _libraryProvider = libraryProvider;
            _transferService = transferService;
            _captureService = captureService;
            _auditLogger = auditLogger;
            _controlTowerService = controlTowerService;
            _libraryRoot = libraryRoot;
            _preselectedProjectIds = preselectedProjectIds;

            LibraryRootText.Text = string.IsNullOrWhiteSpace(libraryRoot)
                ? "No library configured"
                : libraryRoot;

            AssetFilesList.ItemsSource = _assetFiles;
            Loaded += async (_, _) => await LoadLibraryAsync();
        }

        private async Task LoadLibraryAsync()
        {
            AssetTree.Items.Clear();

            if (string.IsNullOrWhiteSpace(_libraryRoot) || !Directory.Exists(_libraryRoot))
            {
                AssetTitleText.Text = "Library folder not found";
                AssetDescriptionText.Text = "Configure the library path in Settings.";
                return;
            }

            AssetTitleText.Text = "Loading library...";
            AssetDescriptionText.Text = string.Empty;

            // Read library index off the UI thread — disk IO over potentially
            // many asset folders.
            LibraryIndex index;
            try
            {
                index = await Task.Run(() => _libraryProvider.LoadLibrary(_libraryRoot)).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                AssetTitleText.Text = "Library load failed";
                AssetDescriptionText.Text = ex.Message;
                return;
            }

            _index = index;
            if (_index?.Issues.Count > 0)
            {
                LibraryRootText.Text =
                    $"{_libraryRoot} | {_index.Issues.Count} library issue{(_index.Issues.Count == 1 ? string.Empty : "s")}";
                LibraryRootText.ToolTip = string.Join(Environment.NewLine, _index.Issues);
            }
            else
            {
                LibraryRootText.Text = _libraryRoot;
                LibraryRootText.ToolTip = null;
            }

            if (_index == null || _index.Assets.Count == 0)
            {
                AssetTitleText.Text = "No assets";
                AssetDescriptionText.Text = "library.yml has no assets, or the file is missing.";
                if (_index?.Issues.Count > 0)
                {
                    EmptyState.Heading = "Library entries blocked";
                    EmptyState.Hint = _index.Issues[0];
                }
                else
                {
                    EmptyState.Heading = "No assets captured yet";
                    EmptyState.Hint = "Capture a folder from a project to add it to the library.";
                }
                EmptyState.Visibility = Visibility.Visible;
                MainContent.Visibility = Visibility.Collapsed;
                return;
            }

            EmptyState.Visibility = Visibility.Collapsed;
            MainContent.Visibility = Visibility.Visible;
            AssetTitleText.Text = "Select an asset";

            foreach (var grp in _index.Assets.GroupBy(a => a.TypeId, StringComparer.OrdinalIgnoreCase)
                                              .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var typeNode = new TreeViewItem
                {
                    Header = grp.Key + "  (" + grp.Count() + ")",
                    IsExpanded = true,
                    Tag = null,
                };
                foreach (var asset in grp.OrderBy(a => a.Id, StringComparer.OrdinalIgnoreCase))
                {
                    typeNode.Items.Add(new TreeViewItem { Header = asset.Id, Tag = asset });
                }
                AssetTree.Items.Add(typeNode);
            }
        }

        private LibraryAsset SelectedAsset
            => (AssetTree.SelectedItem as TreeViewItem)?.Tag as LibraryAsset;

        private AssetType ResolveType(LibraryAsset asset)
        {
            if (asset == null || _index == null)
            {
                return null;
            }
            return _index.AssetTypes.FirstOrDefault(
                t => string.Equals(t.Id, asset.TypeId, StringComparison.OrdinalIgnoreCase));
        }

        private void AssetTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            var asset = SelectedAsset;
            CancelPendingFileEnumeration();
            _assetFiles.Clear();

            if (asset == null)
            {
                AssetTitleText.Text = "Select an asset";
                AssetMetaText.Text = string.Empty;
                AssetDescriptionText.Text = string.Empty;
                PushButton.IsEnabled = false;
                PushManyButton.IsEnabled = false;
                PullButton.IsEnabled = false;
                return;
            }

            AssetTitleText.Text = asset.Id;
            var meta = new List<string> { asset.TypeId };
            if (!string.IsNullOrWhiteSpace(asset.Version)) meta.Add("v" + asset.Version);
            if (!string.IsNullOrWhiteSpace(asset.LastUpdated)) meta.Add("updated " + asset.LastUpdated);
            AssetMetaText.Text = string.Join("  ·  ", meta);

            AssetDescriptionText.Text = string.IsNullOrWhiteSpace(asset.Description)
                ? "(no description)"
                : asset.Description;

            PushButton.IsEnabled = true;
            PushManyButton.IsEnabled = true;
            PullButton.IsEnabled = true;

            // Files preview: enumerate off the UI thread and push results back
            // in batches so a large asset directory never freezes the window.
            BeginFileEnumeration(asset);
        }

        private void BeginFileEnumeration(LibraryAsset asset)
        {
            var cts = new CancellationTokenSource();
            _fileEnumerationCts = cts;
            var token = cts.Token;

            var root = asset.AbsoluteRoot;
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                _assetFiles.Add("(no files)");
                return;
            }

            var dispatcher = Dispatcher;

            Task.Run(() =>
            {
                var batch = new List<string>(FileBatchSize);
                int total = 0;
                try
                {
                    foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    {
                        if (token.IsCancellationRequested)
                        {
                            return;
                        }

                        if (string.Equals(Path.GetFileName(path), "asset.yml", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }

                        batch.Add(Path.GetRelativePath(root, path));
                        if (batch.Count >= FileBatchSize)
                        {
                            FlushBatch(dispatcher, batch, token);
                            total += batch.Count;
                            batch = new List<string>(FileBatchSize);
                        }
                    }

                    if (batch.Count > 0 && !token.IsCancellationRequested)
                    {
                        FlushBatch(dispatcher, batch, token);
                        total += batch.Count;
                    }

                    if (total == 0 && !token.IsCancellationRequested)
                    {
                        dispatcher.InvokeAsync(() =>
                        {
                            if (!token.IsCancellationRequested)
                            {
                                _assetFiles.Add("(no files)");
                            }
                        });
                    }
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested)
                    {
                        return;
                    }
                    dispatcher.InvokeAsync(() =>
                    {
                        if (!token.IsCancellationRequested)
                        {
                            _assetFiles.Add("Error: " + ex.Message);
                        }
                    });
                }
            }, token);
        }

        private void FlushBatch(Dispatcher dispatcher, List<string> batch, CancellationToken token)
        {
            // Sort within the batch so the UI shows a stable order without
            // having to hold every path in memory before paint.
            batch.Sort(StringComparer.OrdinalIgnoreCase);
            var snapshot = batch.ToArray();
            dispatcher.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }
                foreach (var item in snapshot)
                {
                    _assetFiles.Add(item);
                }
            });
        }

        private void CancelPendingFileEnumeration()
        {
            var existing = _fileEnumerationCts;
            if (existing != null)
            {
                existing.Cancel();
                existing.Dispose();
                _fileEnumerationCts = null;
            }
        }

        private void PushClick(object sender, RoutedEventArgs e)
        {
            var asset = SelectedAsset;
            var type = ResolveType(asset);
            if (asset == null || type == null)
            {
                return;
            }

            var dialog = new PushAssetDialog(
                asset, type, _libraryRoot, _transferService, _auditLogger, _controlTowerService);
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        private void PushManyClick(object sender, RoutedEventArgs e)
        {
            var asset = SelectedAsset;
            var type = ResolveType(asset);
            if (asset == null || type == null)
            {
                return;
            }

            var dialog = new PushToManyDialog(
                asset, type, _libraryRoot, _transferService, _auditLogger, _controlTowerService,
                _preselectedProjectIds);
            dialog.Owner = this;
            dialog.ShowDialog();
        }

        private async void PullClick(object sender, RoutedEventArgs e)
        {
            var asset = SelectedAsset;
            var type = ResolveType(asset);
            if (asset == null || type == null)
            {
                return;
            }
            var assetIdToReselect = asset.Id;

            var dialog = new PullAssetDialog(
                asset, type, _libraryRoot, _transferService, _libraryProvider, _auditLogger, _controlTowerService);
            dialog.Owner = this;
            dialog.ShowDialog();

            // Refresh so the updated last_updated and any new files surface.
            await LoadLibraryAsync();
            ReselectAssetById(assetIdToReselect);
        }

        private void ReselectAssetById(string assetId)
        {
            if (string.IsNullOrWhiteSpace(assetId)) return;
            foreach (var typeNode in AssetTree.Items)
            {
                if (typeNode is not System.Windows.Controls.TreeViewItem tn) continue;
                foreach (var assetNode in tn.Items)
                {
                    if (assetNode is System.Windows.Controls.TreeViewItem an &&
                        an.Tag is LibraryAsset la &&
                        string.Equals(la.Id, assetId, StringComparison.OrdinalIgnoreCase))
                    {
                        tn.IsExpanded = true;
                        an.IsSelected = true;
                        return;
                    }
                }
            }
        }

        private async void CaptureClick(object sender, RoutedEventArgs e)
        {
            var dialog = new CaptureAssetDialog(_libraryProvider, _captureService, _controlTowerService, _libraryRoot);
            dialog.Owner = this;
            if (dialog.ShowDialog() == true && dialog.Captured)
            {
                await LoadLibraryAsync();
            }
        }

        private void CloseClick(object sender, RoutedEventArgs e)
        {
            CancelPendingFileEnumeration();
            Close();
        }
    }
}
