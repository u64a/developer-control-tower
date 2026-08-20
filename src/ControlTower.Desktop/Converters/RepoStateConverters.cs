using System;
using System.Globalization;
using System.Windows.Data;
using ControlTower.Core.Models;

namespace ControlTower.Desktop
{
    /// <summary>
    /// RepoState -> plain status word for the pill. Plain vocabulary
    /// (Clean / Uncommitted / Behind / Hosted-only / Attention ...) locked
    /// with the design system.
    /// </summary>
    public sealed class RepoStateToWordConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is RepoState state)
            {
                switch (state)
                {
                    case RepoState.Clean: return "Clean";
                    case RepoState.Ahead: return "Ahead";
                    case RepoState.Behind: return "Behind";
                    case RepoState.Diverged: return "Diverged";
                    case RepoState.Uncommitted: return "Uncommitted";
                    case RepoState.Attention: return "Attention";
                    case RepoState.HostedOnly: return "Hosted-only";
                    case RepoState.Unavailable: return "Unavailable";
                    default: return "Unknown";
                }
            }

            return "Unknown";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// RepoState -> Segoe Fluent / MDL2 glyph so the state reads by shape as
    /// well as colour. This is the "not hue alone" differentiation that keeps
    /// Behind (blue) distinct from the system accent and Attention (red)
    /// distinct from destructive actions.
    /// </summary>
    public sealed class RepoStateToGlyphConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is RepoState state)
            {
                switch (state)
                {
                    case RepoState.Clean: return "\uE73E";        // CheckMark
                    case RepoState.Ahead: return "\uE898";        // Upload (commits to push)
                    case RepoState.Behind: return "\uE896";       // Download (commits to pull)
                    case RepoState.Diverged: return "\uE895";     // Sync (both directions)
                    case RepoState.Uncommitted: return "\uE70F";  // Edit
                    case RepoState.Attention: return "\uE7BA";    // Warning
                    case RepoState.HostedOnly: return "\uE753";   // Cloud
                    case RepoState.Unavailable: return "\uE711";  // Cancel
                    default: return "\uE9CE";                     // Unknown
                }
            }

            return "\uE9CE";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
