using System;
using System.Windows;

namespace ControlTower.Desktop
{
    public partial class SetArchivePathDialog : Window
    {
        public SetArchivePathDialog(string currentPath)
        {
            InitializeComponent();
            ArchivePathBox.Text = currentPath ?? string.Empty;
            Loaded += (_, _) =>
            {
                ArchivePathBox.Focus();
                ArchivePathBox.CaretIndex = ArchivePathBox.Text.Length;
            };
        }

        public string ArchivePath { get; private set; } = string.Empty;

        private void BrowseClick(object sender, RoutedEventArgs e)
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select an archive storage root",
                InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            };
            if (dlg.ShowDialog(this) == true)
            {
                ArchivePathBox.Text = dlg.FolderName;
            }
        }

        private void OkClick(object sender, RoutedEventArgs e)
        {
            ArchivePath = (ArchivePathBox.Text ?? string.Empty).Trim();
            DialogResult = true;
            Close();
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
