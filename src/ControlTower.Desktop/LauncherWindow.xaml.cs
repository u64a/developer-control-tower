using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ControlTower.Desktop.ViewModels;

namespace ControlTower.Desktop
{
    /// <summary>
    /// In-app launcher overlay (the "flick" surface). Shares the live
    /// <see cref="MainViewModel"/> with <see cref="MainWindow"/> so the
    /// selected project, search text, and Open Code command are the same
    /// state on both surfaces. Summoned from inside the running app (Ctrl+K
    /// or the title-bar chip) — there is no global OS hotkey and no resident
    /// process, so the short-lived lifecycle is preserved.
    /// </summary>
    public partial class LauncherWindow : Window
    {
        private readonly MainViewModel _viewModel;
        private readonly System.Action _openLibrary;

        public LauncherWindow(MainViewModel viewModel, System.Action openLibrary = null)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _openLibrary = openLibrary;
            DataContext = viewModel;

            Loaded += OnLoaded;
            Closed += OnClosed;
            PreviewKeyDown += OnPreviewKeyDown;
            SearchBox.TextChanged += (_, _) => UpdateWatermark();
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            UpdateWatermark();
            SearchBox.Focus();
            SearchBox.SelectAll();
        }

        private void OnClosed(object sender, System.EventArgs e)
        {
            // Don't leave the console filtered behind us; the shared selection
            // stays highlighted because it is the same object.
            if (_viewModel != null)
            {
                _viewModel.SearchText = string.Empty;
            }
        }

        private void UpdateWatermark()
        {
            Watermark.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Close();
                    e.Handled = true;
                    break;
                case Key.Down:
                    MoveSelection(1);
                    e.Handled = true;
                    break;
                case Key.Up:
                    MoveSelection(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        ExpandToConsole();
                    }
                    else
                    {
                        var invert = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
                        LaunchSelected(invert);
                    }
                    e.Handled = true;
                    break;
                case Key.L:
                    if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
                    {
                        OpenLibrary();
                        e.Handled = true;
                    }
                    break;
            }
        }

        private void OpenLibrary()
        {
            var callback = _openLibrary;
            Close();
            callback?.Invoke();
        }

        private void MoveSelection(int delta)
        {
            var count = ResultsList.Items.Count;
            if (count == 0)
            {
                return;
            }

            var index = ResultsList.SelectedIndex + delta;
            if (index < 0)
            {
                index = 0;
            }
            else if (index >= count)
            {
                index = count - 1;
            }

            ResultsList.SelectedIndex = index;
            if (ResultsList.SelectedItem != null)
            {
                ResultsList.ScrollIntoView(ResultsList.SelectedItem);
            }
        }

        private void ResultsDoubleClick(object sender, MouseButtonEventArgs e)
        {
            LaunchSelected(invert: false);
        }

        // Workspace-aware open: SSH-only and Hybrid repos open the SSH remote
        // (where the user actually works); local-only repos open local VS Code.
        // Shift inverts, so on a Hybrid repo Shift+Enter opens the local copy.
        private void LaunchSelected(bool invert)
        {
            var vm = _viewModel;
            if (vm == null || vm.SelectedProject == null)
            {
                Close();
                return;
            }

            var preferRemote = vm.CanOpenRemote;
            if (invert)
            {
                preferRemote = !preferRemote;
            }

            var command = (preferRemote && vm.CanOpenRemote)
                ? (System.Windows.Input.ICommand)vm.OpenRemoteCommand
                : vm.OpenCodeCommand;

            if (command != null && command.CanExecute(null))
            {
                command.Execute(null);
            }

            Close();
        }

        private void ExpandToConsole()
        {
            var owner = Owner;
            Close();
            owner?.Activate();
        }
    }
}
