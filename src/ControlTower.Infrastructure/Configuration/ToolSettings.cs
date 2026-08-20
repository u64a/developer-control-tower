using System.Collections.Generic;
using ControlTower.Core.Models;
using ControlTower.Core.Validation;

namespace ControlTower.Infrastructure.Configuration
{
    public sealed class ToolSettings
    {
        public ToolSettings()
        {
            SettingsSource = "defaults";
            VsCodeCommand = "code";
            GitCommand = "git";
            SshCommand = "ssh";
            SshConfigPath = string.Empty;
            AllowHttpLinks = false;
            GitHubCredentialTarget = string.Empty;
            AdoCredentialTarget = string.Empty;
            LibraryPath = string.Empty;
            Stores = new List<RepoStore>();
            Issues = new List<ValidationIssue>();
            UpdateOptions = UpdateOptions.Defaults();
        }

        public string SettingsSource { get; set; }

        public string VsCodeCommand { get; set; }

        public string GitCommand { get; set; }

        public string SshCommand { get; set; }

        public string SshConfigPath { get; set; }

        public bool AllowHttpLinks { get; set; }

        public string GitHubCredentialTarget { get; set; }

        public string AdoCredentialTarget { get; set; }

        /// <summary>
        /// Path to the asset library root. When empty, the app uses the
        /// user-writable library under the resolved portable config root.
        /// </summary>
        public string LibraryPath { get; set; }

        public List<RepoStore> Stores { get; set; }

        /// <summary>
        /// Validation issues raised while loading settings (e.g. paths that
        /// resolved outside the allowed roots and were ignored).
        /// </summary>
        public List<ValidationIssue> Issues { get; set; }

        /// <summary>
        /// Self-update options. Defaults to <see cref="UpdateOptions.Defaults"/>
        /// when the settings file pre-dates the updates section.
        /// </summary>
        public UpdateOptions UpdateOptions { get; set; }
    }
}
