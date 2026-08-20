using System;
using System.Windows;
using System.Windows.Controls;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Desktop.ViewModels;

namespace ControlTower.Desktop
{
    /// <summary>
    /// Modal dialog launched from Settings → "Restore from Git…".
    /// Scans the portfolio for missing local clones, lets the user
    /// resolve conflicts (Skip vs Quarantine &amp; clone), then drives
    /// <see cref="IRestoreOrchestrator"/> with live per-row progress.
    /// </summary>
    public partial class RestoreProjectsDialog : Window
    {
        private readonly RestoreProjectsViewModel _vm;

        /// <summary>
        /// True if at least one portfolio-mutating action occurred while the
        /// dialog was open (cache writeback on scan, or a successful clone).
        /// Settings reads this to know whether to refresh the host portfolio
        /// after the dialog closes.
        /// </summary>
        public bool PortfolioMutated => _vm != null && _vm.PortfolioMutated;

        public RestoreProjectsDialog(
            IPortfolioProvider portfolioProvider,
            IProjectProvider projectProvider,
            IStoreProvider storeProvider,
            IMissingProjectScanner scanner,
            IRestoreOrchestrator orchestrator,
            WorkspaceProfile activeProfile = null)
        {
            InitializeComponent();
            _vm = new RestoreProjectsViewModel(
                portfolioProvider, projectProvider, storeProvider, scanner, orchestrator, activeProfile);
            DataContext = _vm;
            Loaded += OnLoaded;
            Closing += OnClosing;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            await _vm.ScanAsync();
        }

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_vm.IsRestoring)
            {
                e.Cancel = true; // require explicit Cancel first
            }
        }

        private async void RescanClick(object sender, RoutedEventArgs e)
        {
            await _vm.ScanAsync();
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

        private async void RestoreClick(object sender, RoutedEventArgs e)
        {
            await _vm.RestoreSelectedAsync();
        }

        private void CancelRestoreClick(object sender, RoutedEventArgs e)
        {
            _vm.CancelRestore();
        }

        private void CloseClick(object sender, RoutedEventArgs e)
        {
            if (_vm.IsRestoring) return;
            Close();
        }
    }
}
