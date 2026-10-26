using System.Collections.Generic;
using System.Linq;
using System.Windows.Media;
using ControlTower.Core.Appearance;

namespace ControlTower.Desktop
{
    /// <summary>A theme as shown in the Settings pickers, with preview swatches.</summary>
    public sealed class ThemeOption
    {
        private ThemeOption(ThemeDefinition theme, ThemePalette palette)
        {
            Id = theme.Id;
            DisplayName = theme.DisplayName;
            Source = theme.Source;
            Background = Freeze(palette.PaneBackground);
            Border = Freeze(palette.CardBorder);
            Swatches = new[] { palette.Accent, palette.Success, palette.Caution, palette.Critical, palette.RepoAhead }
                .Select(Freeze)
                .ToArray();
        }

        public string Id { get; }
        public string DisplayName { get; }
        public string Source { get; }
        public Brush Background { get; }
        public Brush Border { get; }
        public IReadOnlyList<Brush> Swatches { get; }

        public static IReadOnlyList<ThemeOption> ForMode(ThemeVariant mode, bool keepGitStatesDistinct) =>
            ThemeCatalog.ForMode(mode)
                .Select(t => new ThemeOption(t, ThemeResolver.Resolve(t, keepGitStatesDistinct)))
                .ToList();

        private static Brush Freeze(ThemeColor color)
        {
            var brush = new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));
            brush.Freeze();
            return brush;
        }
    }
}
