using System.Windows;
using ControlTower.Core.Contracts;
using ControlTower.Core.UseCases;
using ControlTower.Desktop.ViewModels;

namespace ControlTower.Desktop
{
    /// <summary>
    /// Modal dialog launched from Settings → "Restore from Git…".
    /// Scans the portfolio for missing local clones, lets the user fix
    /// origin URLs and resolve conflicts (Skip vs Quarantine &amp; clone),
    /// then drives <see cref="IRestoreOrchestrator"/> with live per-row
    /// progress.
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
            RestoreInputBuilder inputBuilder,
            IMissingProjectScanner scanner,
            IRestoreOrchestrator orchestrator)
        {
            InitializeComponent();
            _vm = new RestoreProjectsViewModel(portfolioProvider, inputBuilder, scanner, orchestrator)
            {
                ConfirmQuarantine = message =>
                    MessageBox.Show(this, message, "Restore from Git",
                        MessageBoxButton.OKCancel, MessageBoxImage.Warning) == MessageBoxResult.OK
            };
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
                // Closing mid-clone would orphan the batch; make the user
                // cancel explicitly, and say so instead of ignoring them.
                e.Cancel = true;
                _vm.NotifyCloseBlocked();
            }
        }

        private async void RescanClick(object sender, RoutedEventArgs e)
        {
            await _vm.ScanAsync();
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
            Close();
        }
    }
}
