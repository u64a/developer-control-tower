using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Desktop
{
    public partial class UpdateConfirmDialog : Window
    {
        private readonly IUpdateService _updateService;
        private readonly UpdateCheckResult _result;
        private readonly CancellationTokenSource _cts = new CancellationTokenSource();

        public UpdateConfirmDialog(IUpdateService updateService, UpdateCheckResult result)
        {
            InitializeComponent();

            _updateService = updateService ?? throw new ArgumentNullException(nameof(updateService));
            _result = result ?? throw new ArgumentNullException(nameof(result));

            ApplyResult();
            Closed += (_, _) => _cts.Cancel();
        }

        public bool Confirmed { get; private set; }

        private void ApplyResult()
        {
            if (_result.Provider == UpdateProviderKind.PackagedRelease)
            {
                SourceLabel.Text = "Channel";
                CurrentLabel.Text = "Current version";
                TargetLabel.Text = "Target version";
                DifferenceLabel.Text = "Package";
                LocationLabel.Text = "Release source";
                BranchText.Text = string.IsNullOrWhiteSpace(_result.Channel)
                    ? "(package default)"
                    : _result.Channel;
                CurrentShaText.Text = ValueOrUnknown(_result.CurrentVersion);
                TargetShaText.Text = ValueOrUnknown(_result.TargetVersion);
                BehindText.Text = string.IsNullOrWhiteSpace(_result.Artifact)
                    ? "(resolved by channel)"
                    : _result.Artifact;
                RepoRootText.Text = ValueOrUnknown(_result.Source);
                SubtitleText.Text =
                    "Download the release package, verify its feed checksum, and restart the app. " +
                    "Preview packages are currently unsigned and may trigger Windows reputation warnings.";
            }
            else
            {
                BranchText.Text = string.IsNullOrWhiteSpace(_result.ConfiguredBranch)
                    ? "(not configured)"
                    : _result.ConfiguredBranch;
                CurrentShaText.Text = ShortSha(_result.CurrentSha);
                TargetShaText.Text = ShortSha(_result.RemoteSha);
                BehindText.Text = _result.CommitsBehind > 0
                    ? _result.CommitsBehind + " commit" + (_result.CommitsBehind == 1 ? "" : "s")
                    : "0 commits";
                RepoRootText.Text = string.IsNullOrWhiteSpace(_result.RepoRoot)
                    ? "(unresolved)"
                    : _result.RepoRoot;
                SubtitleText.Text =
                    "Pull the latest, publish a fresh Release build, and install it over the running app " +
                    "(elevates only if the install location requires it).";
            }

            ApplyStatusBanner();
            ApplyDevLocationWarning();

            UpdateButton.IsEnabled = _result.Status == UpdateStatus.UpdateAvailable;
        }

        // Surfaces a single, calm warning when the running .exe lives
        // inside the source clone (e.g. <repo>\src\...\bin\Debug\... or
        // bin\Release\...). In that case the updater will overwrite that
        // build folder rather than a proper install location — almost
        // never what the user wants on a second update. Tested by Joel's
        // own setup: install lives at C:\Program Files\Development Tower,
        // which is OUTSIDE the repo, so this banner stays hidden.
        private void ApplyDevLocationWarning()
        {
            if (_result.Provider == UpdateProviderKind.PackagedRelease ||
                string.IsNullOrWhiteSpace(_result.ExecutablePath) ||
                string.IsNullOrWhiteSpace(_result.RepoRoot))
            {
                DevLocationBanner.Visibility = Visibility.Collapsed;
                return;
            }

            string installDir;
            try
            {
                installDir = Path.GetDirectoryName(_result.ExecutablePath) ?? string.Empty;
            }
            catch
            {
                DevLocationBanner.Visibility = Visibility.Collapsed;
                return;
            }

            if (string.IsNullOrWhiteSpace(installDir) || !IsWithin(installDir, _result.RepoRoot))
            {
                DevLocationBanner.Visibility = Visibility.Collapsed;
                return;
            }

            DevLocationText.Text =
                "You're running from a build folder inside the source clone (" + installDir + "). " +
                "Updates will refresh that folder, not a proper install. Run " +
                "Install-DeveloperControlTower.ps1 once to install to " +
                @"C:\Program Files\Development Tower (or your own -InstallDir), then launch from there.";
            DevLocationBanner.Visibility = Visibility.Visible;
        }

        private static bool IsWithin(string child, string parent)
        {
            try
            {
                var c = Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                var p = Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
                return c.StartsWith(p, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private void ApplyStatusBanner()
        {
            string glyph;
            string message;
            string brushKey;

            switch (_result.Status)
            {
                case UpdateStatus.UpdateAvailable:
                    glyph = "\uE73E"; // CheckMark
                    message = "Ready to update.";
                    brushKey = "PositiveBrush";
                    break;
                case UpdateStatus.UpToDate:
                    glyph = "\uE73E";
                    message = _result.Provider == UpdateProviderKind.PackagedRelease
                        ? "Already running the latest release."
                        : "Already up to date — nothing to pull.";
                    brushKey = "PositiveBrush";
                    break;
                case UpdateStatus.DirtyTree:
                    glyph = "\uE783"; // ErrorBadge
                    message = "Commit or stash uncommitted changes first.";
                    brushKey = "CriticalBrush";
                    break;
                case UpdateStatus.AheadOfOrigin:
                    glyph = "\uE7BA"; // Warning
                    message = _result.CommitsAhead > 0
                        ? $"You have {_result.CommitsAhead} local commit(s) not yet pushed. Push first."
                        : "Local branch is ahead of origin. Push first.";
                    brushKey = "WarningBrush";
                    break;
                case UpdateStatus.Diverged:
                    glyph = "\uE783";
                    message = "Local and origin have diverged. Resolve manually in git.";
                    brushKey = "CriticalBrush";
                    break;
                case UpdateStatus.NoUpstream:
                    glyph = "\uE783";
                    message = "No upstream tracking branch configured.";
                    brushKey = "CriticalBrush";
                    break;
                case UpdateStatus.WrongBranch:
                    glyph = "\uE7BA";
                    message = $"Not on the update branch ({_result.ConfiguredBranch}). Switch branch to update.";
                    brushKey = "WarningBrush";
                    break;
                case UpdateStatus.FetchFailed:
                    glyph = "\uE7BA";
                    message = "Could not fetch from origin (offline or auth issue).";
                    brushKey = "WarningBrush";
                    break;
                case UpdateStatus.RepoNotFound:
                case UpdateStatus.InvalidRepoRoot:
                case UpdateStatus.NotEligible:
                    glyph = "\uE783";
                    message = string.IsNullOrWhiteSpace(_result.Message)
                        ? "Update flow is not available from this build."
                        : _result.Message;
                    brushKey = "CriticalBrush";
                    break;
                default:
                    glyph = "\uE946"; // Info
                    message = string.IsNullOrWhiteSpace(_result.Message) ? "Unknown state." : _result.Message;
                    brushKey = "SecondaryTextBrush";
                    break;
            }

            StatusGlyph.Text = glyph;
            StatusText.Text = message;
            var brush = TryFindResource(brushKey) as Brush;
            if (brush != null)
            {
                StatusGlyph.Foreground = brush;
                StatusText.Foreground = brush;
            }
        }

        private async void UpdateNowClick(object sender, RoutedEventArgs e)
        {
            if (_result.Status != UpdateStatus.UpdateAvailable)
            {
                return;
            }

            UpdateButton.IsEnabled = false;
            StatusText.Text = _result.Provider == UpdateProviderKind.PackagedRelease
                ? "Preparing download..."
                : "Preparing update...";

            try
            {
                var progress = new Progress<int>(value =>
                {
                    if (_result.Provider == UpdateProviderKind.PackagedRelease)
                    {
                        StatusText.Text = $"Downloading update... {value}%";
                    }
                });
                var launchResult = await _updateService
                    .LaunchUpdateAsync(_result, _cts.Token, progress)
                    .ConfigureAwait(true);
                if (launchResult.Spawned)
                {
                    Confirmed = true;
                    DialogResult = true;
                    Close();
                    return;
                }

                AppLogger.Info("Update", "LaunchUpdate refused: " + launchResult.Message);
                StatusText.Text = launchResult.Message;
                var critical = TryFindResource("CriticalBrush") as Brush;
                if (critical != null)
                {
                    StatusText.Foreground = critical;
                    StatusGlyph.Foreground = critical;
                }
                StatusGlyph.Text = "\uE783";
            }
            catch (Exception ex)
            {
                AppLogger.Error("Update", "LaunchUpdate threw: " + ex.Message, ex);
                StatusText.Text = "Could not start the updater: " + ex.Message;
            }
            finally
            {
                if (!Confirmed)
                {
                    UpdateButton.IsEnabled = _result.Status == UpdateStatus.UpdateAvailable;
                }
            }
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private static string ShortSha(string sha)
        {
            if (string.IsNullOrWhiteSpace(sha)) return "(unknown)";
            return sha.Length >= 8 ? sha.Substring(0, 8) : sha;
        }

        private static string ValueOrUnknown(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? "(unknown)" : value;
        }
    }
}
