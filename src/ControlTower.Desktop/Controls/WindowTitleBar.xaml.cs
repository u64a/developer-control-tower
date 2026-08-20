using System;
using System.Windows;
using System.Windows.Controls;

namespace ControlTower.Desktop.Controls
{
    public partial class WindowTitleBar : UserControl
    {
        /// <summary>
        /// Opt-in visibility for the launcher chip. The title bar is shared by
        /// every window (Library, dialogs, …) but only the main console wires
        /// the launcher, so the chip is hidden by default and MainWindow sets
        /// this true. Prevents a dead chip on secondary windows.
        /// </summary>
        public static readonly DependencyProperty ShowLauncherChipProperty =
            DependencyProperty.Register(
                nameof(ShowLauncherChip),
                typeof(bool),
                typeof(WindowTitleBar),
                new PropertyMetadata(false));

        public bool ShowLauncherChip
        {
            get { return (bool)GetValue(ShowLauncherChipProperty); }
            set { SetValue(ShowLauncherChipProperty, value); }
        }

        /// <summary>
        /// Raised when the user clicks the title-bar update chip. The host
        /// window subscribes and opens the update confirmation dialog —
        /// the title bar stays UI-only and never references the update
        /// service directly.
        /// </summary>
        public event EventHandler UpdateChipClicked;

        /// <summary>
        /// Raised when the user clicks the title-bar launcher chip. The host
        /// window opens the in-app launcher overlay; the title bar stays
        /// UI-only and never references view-models directly.
        /// </summary>
        public event EventHandler LauncherRequested;

        public WindowTitleBar()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;

            // Mirror the host window title into the chrome strip.
            TitleTextBlock.Text = window.Title;
            window.StateChanged += (_, _) => UpdateMaxGlyph(window);
            UpdateMaxGlyph(window);
        }

        private void UpdateMaxGlyph(Window window)
        {
            // E922 = maximize square, E923 = restore "two squares"
            MaxGlyph.Text = window.WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
            MaxButton.ToolTip = window.WindowState == WindowState.Maximized ? "Restore" : "Maximize";
        }

        private void MinClick(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;
            System.Windows.SystemCommands.MinimizeWindow(window);
        }

        private void MaxClick(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;
            if (window.ResizeMode == ResizeMode.NoResize || window.ResizeMode == ResizeMode.CanMinimize)
            {
                return;
            }
            window.WindowState = window.WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void CloseClick(object sender, RoutedEventArgs e)
        {
            var window = Window.GetWindow(this);
            if (window == null) return;
            System.Windows.SystemCommands.CloseWindow(window);
        }

        private void UpdateChipClick(object sender, RoutedEventArgs e)
        {
            var handler = UpdateChipClicked;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }

        private void LauncherChipClick(object sender, RoutedEventArgs e)
        {
            var handler = LauncherRequested;
            if (handler != null)
            {
                handler(this, EventArgs.Empty);
            }
        }
    }
}
