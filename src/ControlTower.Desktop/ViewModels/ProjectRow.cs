using System;
using ControlTower.Core.Models;

namespace ControlTower.Desktop.ViewModels
{
    /// <summary>
    /// Presentation wrapper around a <see cref="ProjectOverview"/> that adds a
    /// transient multi-select flag for the portfolio table. The underlying
    /// Core overview stays a pure DTO — selection state lives only here, and
    /// the canonical selection set (by project id) is owned by the view-model
    /// so it survives filtering and re-projection of the visible rows.
    /// </summary>
    public sealed class ProjectRow : ObservableObject
    {
        private readonly Action<ProjectRow> _onSelectionChanged;
        private readonly Func<ProjectOverview, LaunchEnvironmentDisplay> _launchResolver;
        private ProjectOverview _project;
        private bool _isSelected;

        public ProjectRow(ProjectOverview project, bool isSelected, Action<ProjectRow> onSelectionChanged)
            : this(project, isSelected, onSelectionChanged, null)
        {
        }

        public ProjectRow(
            ProjectOverview project,
            bool isSelected,
            Action<ProjectRow> onSelectionChanged,
            Func<ProjectOverview, LaunchEnvironmentDisplay> launchResolver)
        {
            _project = project;
            _isSelected = isSelected;
            _onSelectionChanged = onSelectionChanged;
            _launchResolver = launchResolver;
        }

        /// <summary>The environment this row opens in (icon + name for the table column).</summary>
        public LaunchEnvironmentDisplay Launch
        {
            get { return _launchResolver == null || _project == null ? null : _launchResolver(_project); }
        }

        /// <summary>
        /// The wrapped portfolio overview (swappable on in-place seed/refresh).
        /// Always re-notifies on assignment — including when the incoming
        /// reference is the very same, already-wrapped instance. <see cref="ProjectOverview"/>
        /// is a plain mutable DTO with no value equality, so a real sync
        /// result or a whole-batch block that mutates fields (ArchiveStatus,
        /// ArchiveStatusDetail) on that same instance and reassigns it here
        /// must still be observed by the row; the reference-equality
        /// short-circuit <see cref="ObservableObject.Set{T}"/> uses elsewhere
        /// would otherwise make that reassignment a silent no-op.
        /// </summary>
        public ProjectOverview Project
        {
            get { return _project; }
            set
            {
                _project = value;
                // Notify "Project" so WPF CollectionViewSource re-evaluates
                // the group bucket (PropertyGroupDescription "Project.Group")
                // when the overview is swapped/mutated in place during seeding.
                OnPropertyChanged("Project");
                // RepoState is a pass-through used by the shared lamp style
                // (which binds {Binding RepoState}); notify so the lamp
                // recolours when the overview is swapped/mutated in place.
                OnPropertyChanged("RepoState");
                OnPropertyChanged("Launch");
            }
        }

        /// <summary>
        /// Forces the row's display bindings to re-read <see cref="Project"/>
        /// without reassigning it — a convenience for callers that only hold
        /// a reference to the row (not the overview) and need a refresh.
        /// </summary>
        public void NotifyProjectChanged()
        {
            OnPropertyChanged("Project");
            OnPropertyChanged("RepoState");
            OnPropertyChanged("Launch");
        }

        /// <summary>
        /// Pass-through of the wrapped overview's repo state so the shared
        /// StatusLampStyle (which binds <c>{Binding RepoState}</c> against the
        /// row's DataContext) colours the lamp correctly through the wrapper.
        /// </summary>
        public RepoState RepoState
        {
            get { return _project == null ? RepoState.Unknown : _project.RepoState; }
        }

        /// <summary>True when this row is checked for a bulk action.</summary>
        public bool IsSelected
        {
            get { return _isSelected; }
            set
            {
                if (Set(ref _isSelected, value))
                {
                    _onSelectionChanged?.Invoke(this);
                }
            }
        }

        public string Id
        {
            get { return _project == null ? string.Empty : _project.Id; }
        }
    }
}
