using System;
using System.Windows;
using Microsoft.Win32;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Desktop.Interop
{
    /// <summary>
    /// Observes OS preference changes (light/dark theme + accent color)
    /// and invokes the supplied callback on the WPF UI thread. Built on
    /// <see cref="SystemEvents.UserPreferenceChanged"/> — no new NuGet,
    /// no WinRT dependency.
    /// </summary>
    public sealed class SystemThemeObserver : IDisposable
    {
        private readonly Action _onChanged;
        private readonly HkcuRegistryDword _registry = new();
        private bool _disposed;

        public SystemThemeObserver(Action onChanged)
        {
            _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
            SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        }

        public SystemTheme CurrentTheme() => SystemThemeReader.Resolve(_registry);

        private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            // General captures Color/Theme/Accent changes; we re-evaluate
            // both theme and accent on any general preference change.
            if (e.Category != UserPreferenceCategory.General &&
                e.Category != UserPreferenceCategory.Color)
            {
                return;
            }

            var app = Application.Current;
            if (app == null) return;
            app.Dispatcher.BeginInvoke(_onChanged);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        }
    }
}
