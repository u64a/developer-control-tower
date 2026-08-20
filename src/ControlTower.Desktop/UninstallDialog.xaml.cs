using System;
using System.Windows;
using ControlTower.Core.Models;

namespace ControlTower.Desktop
{
    public partial class UninstallDialog : Window
    {
        public UninstallDialog(
            string configRoot,
            string libraryPath,
            string legacyInstallRoot)
        {
            InitializeComponent();
            ConfigRootText.Text = string.IsNullOrWhiteSpace(configRoot)
                ? "(unresolved)"
                : configRoot;
            LibraryPathText.Text = string.IsNullOrWhiteSpace(libraryPath)
                ? "(unresolved)"
                : libraryPath;
            if (!string.IsNullOrWhiteSpace(legacyInstallRoot))
            {
                LegacyInstallPathText.Text = legacyInstallRoot;
                LegacyInstallWarning.Visibility = Visibility.Visible;
            }
            UpdateConfirmationState();
        }

        public UninstallDataMode SelectedMode
        {
            get
            {
                if (RemoveAllRadio.IsChecked == true)
                {
                    return UninstallDataMode.RemoveAllPortableData;
                }

                if (RemoveConfigRadio.IsChecked == true)
                {
                    return UninstallDataMode.RemovePortableConfigurationKeepLibrary;
                }

                return UninstallDataMode.KeepPortableData;
            }
        }

        private void ModeChanged(object sender, RoutedEventArgs e)
        {
            if (!IsLoaded)
            {
                return;
            }

            SharedDataAcknowledge.IsChecked = false;
            UpdateConfirmationState();
        }

        private void AcknowledgementChanged(object sender, RoutedEventArgs e)
        {
            UpdateConfirmationState();
        }

        private void UpdateConfirmationState()
        {
            var removesSharedData =
                SelectedMode != UninstallDataMode.KeepPortableData;
            SharedDataWarning.Visibility = removesSharedData
                ? Visibility.Visible
                : Visibility.Collapsed;
            UninstallButton.IsEnabled =
                !removesSharedData ||
                SharedDataAcknowledge.IsChecked == true;
        }

        private void UninstallClick(object sender, RoutedEventArgs e)
        {
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
