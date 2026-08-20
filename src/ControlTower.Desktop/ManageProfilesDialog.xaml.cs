using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Diagnostics;

namespace ControlTower.Desktop
{
    public partial class ManageProfilesDialog : Window
    {
        private readonly WorkspaceProfileManager _profileManager;
        private readonly ObservableCollection<ProfileEditorItem> _profiles;
        private readonly IReadOnlyList<ProfileProjectOption> _canonicalProjects;
        private readonly IReadOnlyList<string> _canonicalProjectIds;
        private readonly HashSet<string> _canonicalProjectIdSet;
        private readonly Guid? _initialActiveProfileId;

        public ManageProfilesDialog(
            WorkspaceProfileManager profileManager,
            WorkspaceProfileState profileState,
            PortfolioIndex canonicalPortfolio,
            ControlTowerService controlTowerService)
        {
            InitializeComponent();

            _profileManager = profileManager ?? throw new ArgumentNullException(nameof(profileManager));
            if (profileState == null)
            {
                throw new ArgumentNullException(nameof(profileState));
            }
            if (canonicalPortfolio == null)
            {
                throw new ArgumentNullException(nameof(canonicalPortfolio));
            }
            if (controlTowerService == null)
            {
                throw new ArgumentNullException(nameof(controlTowerService));
            }

            _initialActiveProfileId = profileState.ActiveProfile.Id;

            var projectLoadIssues = new List<string>();
            _canonicalProjects = canonicalPortfolio.Projects
                .Where(project => project != null && !ProjectIdentity.IsUnstable(project.Id))
                .GroupBy(project => project.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => BuildProjectOption(
                    group.First(),
                    controlTowerService,
                    projectLoadIssues))
                .ToList();
            _canonicalProjectIds = _canonicalProjects
                .Select(project => project.ProjectId)
                .ToList();
            _canonicalProjectIdSet = new HashSet<string>(
                _canonicalProjectIds,
                StringComparer.OrdinalIgnoreCase);

            _profiles = new ObservableCollection<ProfileEditorItem>(
                profileState.PersistedProfiles.Select(profile => new ProfileEditorItem(profile)));
            ProfileList.ItemsSource = _profiles;

            var issues = profileState.Issues
                .Concat(canonicalPortfolio.Issues)
                .Select(issue => issue.Message)
                .Concat(projectLoadIssues)
                .Where(message => !string.IsNullOrWhiteSpace(message))
                .Distinct(StringComparer.Ordinal)
                .ToList();
            if (issues.Count > 0)
            {
                ValidationText.Text = string.Join(Environment.NewLine, issues);
                ValidationPanel.Visibility = Visibility.Visible;
            }

            if (_profiles.Count > 0)
            {
                ProfileList.SelectedItem = _initialActiveProfileId.HasValue
                    ? _profiles.FirstOrDefault(profile => profile.Id == _initialActiveProfileId.Value)
                        ?? _profiles[0]
                    : _profiles[0];
            }

            Loaded += (_, _) =>
            {
                if (_profiles.Count > 0)
                {
                    ProfileList.Focus();
                }
            };
        }

        public bool Saved { get; private set; }

        public Guid SavedActiveProfileId { get; private set; }

        private void AddProfileClick(object sender, RoutedEventArgs e)
        {
            var profile = new ProfileEditorItem(
                Guid.NewGuid(),
                BuildUniqueProfileName(),
                _canonicalProjectIds);
            _profiles.Add(profile);
            ProfileList.SelectedItem = profile;
            ProfileList.ScrollIntoView(profile);
            ProfileNameBox.Focus();
            ProfileNameBox.SelectAll();
            DialogStatusText.Text = string.Empty;
        }

        private void DeleteProfileClick(object sender, RoutedEventArgs e)
        {
            if (ProfileList.SelectedItem is not ProfileEditorItem selected)
            {
                return;
            }

            if (_profiles.Count <= 1)
            {
                DialogStatusText.Text = "The last persisted profile cannot be deleted.";
                return;
            }

            var index = ProfileList.SelectedIndex;
            _profiles.Remove(selected);
            ProfileList.SelectedIndex = Math.Min(index, _profiles.Count - 1);
            DialogStatusText.Text = string.Empty;
        }

        private void ProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ProfileList.SelectedItem is not ProfileEditorItem selected)
            {
                MembershipList.ItemsSource = null;
                ProfileNameBox.IsEnabled = false;
                StaleMembersText.Text = _profiles.Count == 0
                    ? "Add a profile to materialize synced profile definitions."
                    : string.Empty;
                return;
            }

            ProfileNameBox.IsEnabled = true;
            MembershipList.ItemsSource = _canonicalProjects
                .Select(project => new ProfileMembershipRow(
                    project,
                    selected.ContainsMember(project.ProjectId),
                    included => selected.SetMember(project.ProjectId, included)))
                .GroupBy(project => project.Group, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ProfileMembershipGroup(
                    group.Key,
                    group.OrderBy(
                        project => project.DisplayName,
                        StringComparer.OrdinalIgnoreCase)))
                .OrderBy(
                    group => string.Equals(
                        group.Name,
                        "Ungrouped",
                        StringComparison.OrdinalIgnoreCase)
                        ? 1
                        : 0)
                .ThenBy(group => group.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var staleCount = selected.Members.Count(member =>
                !_canonicalProjectIdSet.Contains(member));
            StaleMembersText.Text = staleCount == 0
                ? "Every saved member currently exists in the canonical portfolio."
                : staleCount + " stale member id" + (staleCount == 1 ? string.Empty : "s") +
                  " preserved non-destructively.";
        }

        private void MembershipGroupClick(object sender, RoutedEventArgs e)
        {
            if (sender is CheckBox checkBox &&
                checkBox.DataContext is ProfileMembershipGroup group)
            {
                group.ToggleAll();
                e.Handled = true;
            }
        }

        private void SaveClick(object sender, RoutedEventArgs e)
        {
            if (_profiles.Count == 0)
            {
                DialogStatusText.Text = "Add at least one profile before saving.";
                return;
            }

            try
            {
                var definitions = _profiles
                    .Select(profile => profile.ToWorkspaceProfile())
                    .ToList();
                SavedActiveProfileId = _profileManager.SaveProfiles(
                    definitions,
                    _initialActiveProfileId);
                Saved = true;
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                DialogStatusText.Text = ex.Message;
            }
        }

        private void CancelClick(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private string BuildUniqueProfileName()
        {
            const string baseName = "New profile";
            var existing = new HashSet<string>(
                _profiles.Select(profile => profile.Name),
                StringComparer.OrdinalIgnoreCase);
            if (!existing.Contains(baseName))
            {
                return baseName;
            }

            for (var suffix = 2; ; suffix++)
            {
                var candidate = baseName + " " + suffix;
                if (!existing.Contains(candidate))
                {
                    return candidate;
                }
            }
        }

        private static ProfileProjectOption BuildProjectOption(
            ProjectRef projectRef,
            ControlTowerService controlTowerService,
            ICollection<string> issues)
        {
            try
            {
                var overview = controlTowerService.LoadProject(
                    projectRef,
                    includeRepoScan: false);
                return new ProfileProjectOption(
                    projectRef.Id,
                    ValueOr(overview?.DisplayName, projectRef.Id),
                    ValueOr(overview?.Group, "Ungrouped"),
                    ResolveLocation(projectRef, overview));
            }
            catch (Exception ex)
            {
                var message = "Could not load project details for '" +
                    projectRef.Id + "': " + ex.Message;
                issues.Add(message);
                AppLogger.Warn("WorkspaceProfiles", message);
                return new ProfileProjectOption(
                    projectRef.Id,
                    projectRef.Id,
                    "Ungrouped",
                    ResolveLocation(projectRef, null));
            }
        }

        private static string ResolveLocation(
            ProjectRef projectRef,
            ProjectOverview overview)
        {
            if (!string.IsNullOrWhiteSpace(overview?.RepoLocation) &&
                !string.Equals(
                    overview.RepoLocation,
                    "Not available",
                    StringComparison.OrdinalIgnoreCase))
            {
                return overview.RepoLocation;
            }

            if (!string.IsNullOrWhiteSpace(projectRef.Path))
            {
                return projectRef.Path;
            }

            if (!string.IsNullOrWhiteSpace(projectRef.StoreId))
            {
                var folder = string.IsNullOrWhiteSpace(projectRef.Folder)
                    ? projectRef.Id
                    : projectRef.Folder;
                return projectRef.StoreId + " · " + folder;
            }

            return "Location not configured";
        }

        private static string ValueOr(string value, string fallback) =>
            string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

        private sealed class ProfileProjectOption
        {
            public ProfileProjectOption(
                string projectId,
                string displayName,
                string group,
                string repoLocation)
            {
                ProjectId = projectId;
                DisplayName = displayName;
                Group = group;
                RepoLocation = repoLocation;
            }

            public string ProjectId { get; }

            public string DisplayName { get; }

            public string Group { get; }

            public string RepoLocation { get; }
        }

        private sealed class ProfileEditorItem : INotifyPropertyChanged
        {
            private readonly HashSet<string> _members;
            private string _name;

            public ProfileEditorItem(WorkspaceProfile profile)
                : this(profile.Id, profile.Name, profile.Members)
            {
            }

            public ProfileEditorItem(Guid id, string name, IEnumerable<string> members)
            {
                Id = id;
                _name = name ?? string.Empty;
                _members = new HashSet<string>(
                    members ?? Enumerable.Empty<string>(),
                    StringComparer.OrdinalIgnoreCase);
            }

            public Guid Id { get; }

            public string Name
            {
                get => _name;
                set
                {
                    if (string.Equals(_name, value, StringComparison.Ordinal))
                    {
                        return;
                    }

                    _name = value ?? string.Empty;
                    OnPropertyChanged();
                }
            }

            public IReadOnlyCollection<string> Members => _members;

            public bool ContainsMember(string projectId) =>
                _members.Contains(projectId);

            public void SetMember(string projectId, bool included)
            {
                if (included)
                {
                    _members.Add(projectId);
                }
                else
                {
                    _members.Remove(projectId);
                }
            }

            public WorkspaceProfile ToWorkspaceProfile()
            {
                var profile = new WorkspaceProfile
                {
                    Id = Id,
                    Name = (Name ?? string.Empty).Trim()
                };
                foreach (var member in _members)
                {
                    profile.Members.Add(member);
                }

                return profile;
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }

        private sealed class ProfileMembershipRow : INotifyPropertyChanged
        {
            private readonly Action<bool> _onChanged;
            private bool _isIncluded;

            public ProfileMembershipRow(
                ProfileProjectOption project,
                bool isIncluded,
                Action<bool> onChanged)
            {
                if (project == null)
                {
                    throw new ArgumentNullException(nameof(project));
                }

                ProjectId = project.ProjectId;
                DisplayName = project.DisplayName;
                Group = project.Group;
                RepoLocation = project.RepoLocation;
                _isIncluded = isIncluded;
                _onChanged = onChanged;
            }

            public string ProjectId { get; }

            public string DisplayName { get; }

            public string Group { get; }

            public string RepoLocation { get; }

            public bool IsIncluded
            {
                get => _isIncluded;
                set
                {
                    if (_isIncluded == value)
                    {
                        return;
                    }

                    _isIncluded = value;
                    _onChanged(value);
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(IsIncluded)));
                }
            }

            public event PropertyChangedEventHandler PropertyChanged;
        }

        private sealed class ProfileMembershipGroup : INotifyPropertyChanged
        {
            private bool _updatingProjects;

            public ProfileMembershipGroup(
                string name,
                IEnumerable<ProfileMembershipRow> projects)
            {
                Name = ValueOr(name, "Ungrouped");
                Projects = new ObservableCollection<ProfileMembershipRow>(
                    projects ?? Enumerable.Empty<ProfileMembershipRow>());
                foreach (var project in Projects)
                {
                    project.PropertyChanged += ProjectPropertyChanged;
                }
            }

            public string Name { get; }

            public ObservableCollection<ProfileMembershipRow> Projects { get; }

            public int ProjectCount => Projects.Count;

            public bool? IsIncluded
            {
                get
                {
                    var includedCount = Projects.Count(project => project.IsIncluded);
                    if (includedCount == 0)
                    {
                        return false;
                    }

                    return includedCount == Projects.Count ? true : null;
                }
            }

            public void ToggleAll()
            {
                var include = IsIncluded != true;
                _updatingProjects = true;
                try
                {
                    foreach (var project in Projects)
                    {
                        project.IsIncluded = include;
                    }
                }
                finally
                {
                    _updatingProjects = false;
                }

                PropertyChanged?.Invoke(
                    this,
                    new PropertyChangedEventArgs(nameof(IsIncluded)));
            }

            public event PropertyChangedEventHandler PropertyChanged;

            private void ProjectPropertyChanged(
                object sender,
                PropertyChangedEventArgs e)
            {
                if (!_updatingProjects &&
                    e.PropertyName == nameof(ProfileMembershipRow.IsIncluded))
                {
                    PropertyChanged?.Invoke(
                        this,
                        new PropertyChangedEventArgs(nameof(IsIncluded)));
                }
            }
        }
    }
}
