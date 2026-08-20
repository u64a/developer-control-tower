using System;
using System.Windows.Input;

namespace ControlTower.Desktop.ViewModels
{
    public sealed class DelegateCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;

        public DelegateCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute();
        }

        public void Execute(object parameter)
        {
            if (_execute != null)
            {
                _execute();
            }
        }
        public void RaiseCanExecuteChanged()
        {
            CommandManager.InvalidateRequerySuggested();
        }
    }

    /// <summary>
    /// Parameterized command for per-row / per-selection actions in the
    /// portfolio table. Acts on the bound row (a ProjectOverview) rather than
    /// the single SelectedProject, and uses CommandManager requery so row
    /// buttons enable/disable as the bound item changes.
    /// </summary>
    public sealed class DelegateCommand<T> : ICommand
    {
        private readonly Action<T> _execute;
        private readonly Predicate<T> _canExecute;

        public DelegateCommand(Action<T> execute, Predicate<T> canExecute = null)
        {
            _execute = execute;
            _canExecute = canExecute;
        }

        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }

        public bool CanExecute(object parameter)
        {
            if (_canExecute == null)
            {
                return true;
            }

            return parameter is T t && _canExecute(t);
        }

        public void Execute(object parameter)
        {
            if (parameter is T t && _execute != null)
            {
                _execute(t);
            }
        }
    }
}
