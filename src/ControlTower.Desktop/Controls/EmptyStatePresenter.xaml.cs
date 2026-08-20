using System.Windows;
using System.Windows.Controls;

namespace ControlTower.Desktop.Controls
{
    public partial class EmptyStatePresenter : UserControl
    {
        public static readonly DependencyProperty GlyphProperty =
            DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(EmptyStatePresenter),
                new PropertyMetadata("\uE7C3", OnGlyphChanged));

        public static readonly DependencyProperty HeadingProperty =
            DependencyProperty.Register(nameof(Heading), typeof(string), typeof(EmptyStatePresenter),
                new PropertyMetadata("Nothing here yet", OnHeadingChanged));

        public static readonly DependencyProperty HintProperty =
            DependencyProperty.Register(nameof(Hint), typeof(string), typeof(EmptyStatePresenter),
                new PropertyMetadata(string.Empty, OnHintChanged));

        public EmptyStatePresenter()
        {
            InitializeComponent();
        }

        public string Glyph
        {
            get => (string)GetValue(GlyphProperty);
            set => SetValue(GlyphProperty, value);
        }

        public string Heading
        {
            get => (string)GetValue(HeadingProperty);
            set => SetValue(HeadingProperty, value);
        }

        public string Hint
        {
            get => (string)GetValue(HintProperty);
            set => SetValue(HintProperty, value);
        }

        private static void OnGlyphChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EmptyStatePresenter p && e.NewValue is string s) p.GlyphText.Text = s;
        }

        private static void OnHeadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EmptyStatePresenter p && e.NewValue is string s) p.HeadingText.Text = s;
        }

        private static void OnHintChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is EmptyStatePresenter p && e.NewValue is string s) p.HintText.Text = s;
        }
    }
}
