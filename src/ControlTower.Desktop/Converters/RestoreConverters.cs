using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace ControlTower.Desktop
{
    /// <summary>
    /// Looks up a SolidColorBrush from the application Resources by its
    /// resource key string. Returns <see cref="Brushes.Gray"/> on miss
    /// so XAML never crashes.
    /// </summary>
    public sealed class BrushKeyToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = value as string;
            if (string.IsNullOrEmpty(key)) return Brushes.Gray;
            try
            {
                var app = Application.Current;
                if (app != null)
                {
                    var resource = app.TryFindResource(key);
                    if (resource is Brush brush) return brush;
                }
            }
            catch { /* fall through */ }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>Inverts a boolean.</summary>
    public sealed class NegateBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : (object)false;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b ? !b : (object)false;
    }

    /// <summary>Maps true=Collapsed, false=Visible (negated BooleanToVisibility).</summary>
    public sealed class BooleanToVisibilityNegatedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => value is bool b && b ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => value is Visibility v && v == Visibility.Collapsed;
    }

    /// <summary>
    /// Non-empty string =&gt; Visible, empty/whitespace =&gt; Collapsed. Used to
    /// hide optional context rows in the rail when a project has no value for
    /// that field (e.g. no planning authority configured).
    /// </summary>
    public sealed class StringToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => string.IsNullOrWhiteSpace(value as string) ? Visibility.Collapsed : Visibility.Visible;

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    /// <summary>
    /// Colours the compact SYNC readout: behind (commits to pull) =&gt; Info
    /// blue, ahead (commits to push) =&gt; teal, otherwise the dim secondary
    /// text. Input is the <see cref="Core.Models.ProjectOverview"/> row.
    /// </summary>
    public sealed class SyncToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = "SecondaryTextBrush";
            if (value is Core.Models.ProjectOverview o)
            {
                if (o.BehindBy > 0) key = "InfoBrush";
                else if (o.AheadBy > 0) key = "RepoAheadBrush";
            }
            return ResolveBrush(key);
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();

        private static Brush ResolveBrush(string key)
        {
            try
            {
                if (Application.Current?.TryFindResource(key) is Brush b) return b;
            }
            catch { /* fall through */ }
            return Brushes.Gray;
        }
    }

    /// <summary>
    /// Colours the TREE column: a dirty working tree reads in caution amber,
    /// everything else in the dim secondary text colour.
    /// </summary>
    public sealed class TreeStatusToBrushConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var key = string.Equals(value as string, "Dirty", StringComparison.OrdinalIgnoreCase)
                ? "CautionBrush"
                : "SecondaryTextBrush";
            try
            {
                if (Application.Current?.TryFindResource(key) is Brush b) return b;
            }
            catch { /* fall through */ }
            return Brushes.Gray;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
