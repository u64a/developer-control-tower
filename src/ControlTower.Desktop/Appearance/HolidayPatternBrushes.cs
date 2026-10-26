using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace ControlTower.Desktop.Appearance
{
    /// <summary>
    /// Builds the frozen, static decorative tile used for the
    /// <c>WindowBackgroundBrush</c> of the Christmas and Halloween themes.
    /// Purely decorative chrome: project rows, cards, dialog controls and
    /// text all keep their own solid brushes and never reference this tile,
    /// so legibility is unaffected. Motifs are original miniature line-art
    /// (evergreen tree / ornament / star / snowflake for Christmas,
    /// jack-o'-lantern / bat for Halloween) - no console artwork, no
    /// third-party assets.
    /// </summary>
    internal static class HolidayPatternBrushes
    {
        // Motif scatter spacing: alternating rows offset by half a column
        // for a brick-style layout.
        private const double ColWidth = 94;
        private const double RowHeight = 84;

        private const double TileWidth = ColWidth * 4;
        private const double TileHeight = RowHeight * 4;

        // Sparse and low-contrast by design: this sits behind every window,
        // not just an occasional preview, so it stays well under the
        // thresholds used for actual readable text/decorative chip colours.
        private const byte MotifAlpha = 66;

        private static readonly Color ChristmasGreen = Color.FromRgb(0x23, 0x6B, 0x49);
        private static readonly Color ChristmasRed = Color.FromRgb(0xBE, 0x28, 0x39);
        private static readonly Color ChristmasGold = Color.FromRgb(0xA9, 0x7A, 0x21);
        private static readonly Color ChristmasSnow = Color.FromRgb(0x56, 0x83, 0x99);

        private static readonly Color HalloweenOrange = Color.FromRgb(0xFF, 0x88, 0x33);
        private static readonly Color HalloweenPurple = Color.FromRgb(0xB5, 0x8B, 0xDB);

        public static DrawingBrush Christmas(Color background) =>
            Build(background, new Action<DrawingContext, Color>[]
            {
                (ctx, _) => DrawTree(ctx, Motif(ChristmasGreen)),
                (ctx, _) => DrawOrnament(ctx, Pen(ChristmasRed)),
                (ctx, _) => DrawStar(ctx, Pen(ChristmasGold)),
                (ctx, _) => DrawSnowflake(ctx, Pen(ChristmasSnow)),
            });

        public static DrawingBrush Halloween(Color background) =>
            Build(background, new Action<DrawingContext, Color>[]
            {
                (ctx, bg) => DrawPumpkin(ctx, Motif(HalloweenOrange), Solid(bg)),
                (ctx, _) => DrawBat(ctx, Motif(HalloweenPurple)),
            });

        private static DrawingBrush Build(Color background, Action<DrawingContext, Color>[] motifs)
        {
            var group = new DrawingGroup();
            using (var ctx = group.Open())
            {
                ctx.DrawRectangle(Solid(background), null, new Rect(0, 0, TileWidth, TileHeight));

                var index = 0;
                var row = 0;
                for (var y = RowHeight / 2; y < TileHeight; y += RowHeight, row++)
                {
                    var offset = row % 2 == 0 ? 0 : ColWidth / 2;
                    for (var x = ColWidth / 4 + offset; x < TileWidth; x += ColWidth)
                    {
                        ctx.PushTransform(new TranslateTransform(x, y));
                        motifs[(index + row) % motifs.Length](ctx, background);
                        ctx.Pop();
                        index++;
                    }
                }
            }
            group.Freeze();

            var brush = new DrawingBrush(group)
            {
                Viewbox = new Rect(0, 0, TileWidth, TileHeight),
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, TileWidth, TileHeight),
                ViewportUnits = BrushMappingMode.Absolute,
                TileMode = TileMode.Tile,
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
            };
            brush.Freeze();
            return brush;
        }

        private static SolidColorBrush Motif(Color c) => Solid(Color.FromArgb(MotifAlpha, c.R, c.G, c.B));

        private static SolidColorBrush Solid(Color c)
        {
            var brush = new SolidColorBrush(c);
            brush.Freeze();
            return brush;
        }

        private static Pen Pen(Color c)
        {
            var pen = new System.Windows.Media.Pen(Motif(c), 1.4);
            pen.Freeze();
            return pen;
        }

        private static StreamGeometry Polygon(params double[] xy)
        {
            var geometry = new StreamGeometry();
            using (var ctx = geometry.Open())
            {
                ctx.BeginFigure(new Point(xy[0], xy[1]), true, true);
                var points = new List<Point>();
                for (var i = 2; i < xy.Length; i += 2)
                {
                    points.Add(new Point(xy[i], xy[i + 1]));
                }
                ctx.PolyLineTo(points, true, false);
            }
            geometry.Freeze();
            return geometry;
        }

        private static Point Rotate(double x, double y, double degrees)
        {
            var rad = degrees * Math.PI / 180;
            var cos = Math.Cos(rad);
            var sin = Math.Sin(rad);
            return new Point(x * cos - y * sin, x * sin + y * cos);
        }

        /// <summary>Original miniature evergreen tree.</summary>
        private static void DrawTree(DrawingContext ctx, Brush brush)
        {
            ctx.DrawGeometry(brush, null, Polygon(
                0, -15, -8, -4, -4, -4, -12, 6, -6, 6, -15, 15,
                15, 15, 6, 6, 12, 6, 4, -4, 8, -4));
            ctx.DrawRectangle(brush, null, new Rect(-2, 14, 4, 6));
        }

        /// <summary>Original miniature bauble/ornament, outline only.</summary>
        private static void DrawOrnament(DrawingContext ctx, Pen pen)
        {
            ctx.DrawEllipse(null, pen, new Point(0, 4.5), 10, 11.5);
            ctx.DrawRectangle(null, pen, new Rect(-4, -12, 8, 5));
            ctx.DrawEllipse(null, pen, new Point(0, -15.5), 3, 3.5);
            ctx.DrawLine(pen, new Point(-6, 0), new Point(6, 8));
            ctx.DrawLine(pen, new Point(-6, 7), new Point(4, 13));
        }

        /// <summary>Original miniature five-point star, outline only.</summary>
        private static void DrawStar(DrawingContext ctx, Pen pen)
        {
            var xy = new double[20];
            for (var j = 0; j < 10; j++)
            {
                var radius = j % 2 == 0 ? 14 : 5.5;
                var angle = (j * 36 - 90) * Math.PI / 180;
                xy[j * 2] = radius * Math.Cos(angle);
                xy[j * 2 + 1] = radius * Math.Sin(angle);
            }
            ctx.DrawGeometry(null, pen, Polygon(xy));
        }

        /// <summary>Original miniature six-armed snowflake.</summary>
        private static void DrawSnowflake(DrawingContext ctx, Pen pen)
        {
            for (var arm = 0; arm < 6; arm++)
            {
                var angle = arm * 60.0;
                ctx.DrawLine(pen, Rotate(0, 0, angle), Rotate(0, -16, angle));
                ctx.DrawLine(pen, Rotate(0, -10, angle), Rotate(-5, -14, angle));
                ctx.DrawLine(pen, Rotate(0, -10, angle), Rotate(5, -14, angle));
            }
        }

        /// <summary>Original miniature jack-o'-lantern; eyes/mouth are cut from the background colour.</summary>
        private static void DrawPumpkin(DrawingContext ctx, Brush brush, Brush backgroundBrush)
        {
            ctx.DrawEllipse(brush, null, new Point(0, 3), 16, 13);
            var stemPen = new System.Windows.Media.Pen(brush, 2);
            ctx.DrawLine(stemPen, new Point(0, -10), new Point(2, -19));
            ctx.DrawLine(stemPen, new Point(2, -19), new Point(7, -18));
            ctx.DrawGeometry(backgroundBrush, null, Polygon(-11, -5, -3, -1, -10, 1));
            ctx.DrawGeometry(backgroundBrush, null, Polygon(11, -5, 3, -1, 10, 1));
            ctx.DrawGeometry(backgroundBrush, null, Polygon(
                -11, 5, -6, 8, -3, 5, 0, 9, 4, 5, 7, 8, 11, 4, 7, 12, -6, 12));
        }

        /// <summary>Original miniature bat silhouette.</summary>
        private static void DrawBat(DrawingContext ctx, Brush brush)
        {
            ctx.DrawGeometry(brush, null, Polygon(
                -19, -8, -10, -4, -5, -7, -3, -3, -2, -10, 0, -7, 2, -10, 3, -3, 5, -7,
                10, -4, 19, -8, 15, 4, 9, 1, 5, 7, 0, 11, -5, 7, -9, 1, -15, 4));
        }
    }
}
