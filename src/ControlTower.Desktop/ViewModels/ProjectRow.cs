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
        private ProjectOverview _project;
        private bool _isSelected;

        public ProjectRow(ProjectOverview project, bool isSelected, Action<ProjectRow> onSelectionChanged)
        {
            _project = project;
            _isSelected = isSelected;
            _onSelectionChanged = onSelectionChanged;
        }

        /// <summary>The wrapped portfolio overview (swappable on in-place seed/refresh).</summary>
        public ProjectOverview Project
        {
            get { return _project; }
            set
            {
                if (Set(ref _project, value))
                {
                    // Notify "Project" so WPF CollectionViewSource re-evaluates
                    // the group bucket (PropertyGroupDescription "Project.Group")
                    // when the overview is swapped in place during seeding.
                    OnPropertyChanged("Project");
                    // RepoState is a pass-through used by the shared lamp style
                    // (which binds {Binding RepoState}); notify so the lamp
                    // recolours when the overview is swapped in place on seed.
                    OnPropertyChanged("RepoState");
                }
            }
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
