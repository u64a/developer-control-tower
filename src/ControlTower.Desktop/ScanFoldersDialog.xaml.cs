using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Desktop.ViewModels;

namespace ControlTower.Desktop
{
    /// <summary>
    /// Modal dialog launched from Settings → "Scan folders for repos…".
    /// Walks user-picked root folders looking for git repositories and
    /// lets the user bulk-register the discovered entries into
    /// <c>portfolio.yml</c>.
    /// </summary>
    public partial class ScanFoldersDialog : Window
    {
        private readonly ScanFoldersViewModel _vm;

        /// <summary>
        /// True if at least one project was successfully registered while
        /// the dialog was open. Settings reads this to know whether the
        /// host should refresh the portfolio after the dialog closes.
        /// </summary>
        public bool PortfolioMutated => _vm != null && _vm.PortfolioMutated;

        public bool ProfileStateMutated => _vm != null && _vm.ProfileStateMutated;

        public ScanFoldersDialog(
            IRepoScanService scanService,
            IProjectRegistrationService registrationService,
            WorkspaceProfileManager profileManager = null,
            WorkspaceProfile activeProfile = null,
            IEnumerable<string> initialRoots = null)
        {
            InitializeComponent();
            _vm = new ScanFoldersViewModel(
                scanService,
                registrationService,
                profileManager,
                activeProfile);
            DataContext = _vm;
            Closing += OnClosing;

            // Pre-seed the root list from the caller (typically Settings →
            // local Repo Stores) so the common "scan all my known repo
            // parents" flow is one click instead of an Add-folder ritual.
            // AddRoot already enforces the MaxRoots cap and de-dups by path.
            if (initialRoots != null)
            {
                foreach (var root in initialRoots)
                {
                    _vm.AddRoot(root);
                }
            }
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_vm.IsScanning || _vm.IsRegistering)
            {
                e.Cancel = true; // require explicit Cancel scan first
            }
        }

        private void AddRootClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select a folder to scan",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (dlg.ShowDialog(this) == true)
            {
                _vm.AddRoot(dlg.FolderName);
            }
        }

        private void RemoveRootClick(object sender, RoutedEventArgs e)
        {
            if (RootsList.SelectedItem is ScanFolderRootViewModel root)
            {
                _vm.RemoveRoot(root);
            }
            else if (_vm.Roots.Count > 0)
            {
                _vm.RemoveRoot(_vm.Roots[_vm.Roots.Count - 1]);
            }
        }

        private async void ScanClick(object sender, RoutedEventArgs e)
        {
            await _vm.ScanAsync();
        }

        private void CancelScanClick(object sender, RoutedEventArgs e)
        {
            _vm.CancelScan();
        }

        private void SelectAllClick(object sender, RoutedEventArgs e)
        {
            _vm.SelectAllSelectable();
        }

        private void SelectNoneClick(object sender, RoutedEventArgs e)
        {
            _vm.SelectNone();
        }

        private void HeaderSelectAllClick(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox cb && cb.IsChecked == true)
            {
                _vm.SelectAllSelectable();
            }
            else
            {
                _vm.SelectNone();
            }
        }

        private async void RegisterClick(object sender, RoutedEventArgs e)
        {
            await _vm.RegisterSelectedAsync();
        }

        private void CloseClick(object sender, RoutedEventArgs e)
        {
            if (_vm.IsScanning || _vm.IsRegistering) return;
            Close();
        }
    }
}
