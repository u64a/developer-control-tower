using System;
using System.Windows;
using ControlTower.Core.Models;
using ControlTower.Desktop.Bootstrap;
using ControlTower.Desktop.ViewModels;

namespace ControlTower.Desktop
{
    /// <summary>
    /// Modal dialog hosting the Phase B Relocate workflow. Threads the
    /// VM's <see cref="RelocateProjectViewModel.PortfolioMutated"/> flag
    /// out to the caller (MainWindow) so the project list refreshes on
    /// dialog close.
    /// </summary>
    public partial class RelocateProjectDialog : Window
    {
        private readonly RelocateProjectViewModel _vm;

        public RelocateProjectDialog(ProjectOverview source, DesktopSession session)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            if (session == null) throw new ArgumentNullException(nameof(session));
            InitializeComponent();

            _vm = new RelocateProjectViewModel(source, session.CurrentStores, session.RelocateService);
            DataContext = _vm;

            Closing += OnClosing;
        }

        public bool PortfolioMutated => _vm != null && _vm.PortfolioMutated;

        private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_vm.IsRelocating)
            {
                e.Cancel = true;
            }
        }

        private async void PreflightClick(object sender, RoutedEventArgs e)
        {
            await _vm.RunPreflightAsync();
        }

        private async void RelocateClick(object sender, RoutedEventArgs e)
        {
            await _vm.RunRelocateAsync();
        }

        private async void PushClick(object sender, RoutedEventArgs e)
        {
            await _vm.PushSourceAsync();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            _vm.Cancel();
        }

        private void CloseClick(object sender, RoutedEventArgs e)
        {
            if (_vm.CanClose) Close();
        }
    }
}
