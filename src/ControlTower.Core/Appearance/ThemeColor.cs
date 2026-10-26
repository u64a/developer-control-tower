using System;
using System.Globalization;

namespace ControlTower.Core.Appearance
{
    /// <summary>
    /// An opaque sRGB colour with the small amount of colour maths the theme
    /// resolver needs (mixing, WCAG contrast, hue). Kept free of WPF types so
    /// palettes can be resolved and tested in Core.
    /// </summary>
    public readonly struct ThemeColor : IEquatable<ThemeColor>
    {
        public ThemeColor(byte r, byte g, byte b)
        {
            R = r;
            G = g;
            B = b;
        }

        public byte R { get; }
        public byte G { get; }
        public byte B { get; }

        public static readonly ThemeColor White = new(255, 255, 255);
        public static readonly ThemeColor Black = new(0, 0, 0);

        /// <summary>Parses a strict <c>#RRGGBB</c> value.</summary>
        public static ThemeColor Parse(string hex)
        {
            if (!TryParse(hex, out var color))
            {
                throw new FormatException("Theme colours must be #RRGGBB: '" + hex + "'.");
            }
            return color;
        }

        public static bool TryParse(string hex, out ThemeColor color)
        {
            color = default;
            if (hex == null || hex.Length != 7 || hex[0] != '#')
            {
                return false;
            }
            if (!int.TryParse(hex.AsSpan(1), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }
            color = new ThemeColor((byte)(value >> 16), (byte)(value >> 8), (byte)value);
            return true;
        }

        /// <summary>Returns <c>#RRGGBB</c> in upper case.</summary>
        public string ToHex() => "#" + R.ToString("X2", CultureInfo.InvariantCulture)
            + G.ToString("X2", CultureInfo.InvariantCulture)
            + B.ToString("X2", CultureInfo.InvariantCulture);

        /// <summary>
        /// Linear sRGB blend: <paramref name="weight"/> of this colour and
        /// the remainder of <paramref name="other"/>.
        /// </summary>
        public ThemeColor Mix(ThemeColor other, double weight)
        {
            weight = Math.Clamp(weight, 0, 1);
            static byte Channel(byte a, byte b, double w) => (byte)Math.Round(a * w + b * (1 - w));
            return new ThemeColor(Channel(R, other.R, weight), Channel(G, other.G, weight), Channel(B, other.B, weight));
        }

        /// <summary>WCAG 2.x relative luminance.</summary>
        public double RelativeLuminance
        {
            get
            {
                static double Linear(byte c)
                {
                    var v = c / 255.0;
                    return v <= 0.03928 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
                }
                return 0.2126 * Linear(R) + 0.7152 * Linear(G) + 0.0722 * Linear(B);
            }
        }

        /// <summary>WCAG 2.x contrast ratio (1 to 21).</summary>
        public double ContrastWith(ThemeColor other)
        {
            var a = RelativeLuminance;
            var b = other.RelativeLuminance;
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }

        /// <summary>HSL hue in degrees (0 for greys).</summary>
        public double Hue
        {
            get
            {
                var (r, g, b) = (R / 255.0, G / 255.0, B / 255.0);
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var d = max - min;
                if (d == 0) return 0;
                double h;
                if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
                else if (max == g) h = (b - r) / d + 2;
                else h = (r - g) / d + 4;
                return h * 60;
            }
        }

        /// <summary>HSL saturation (0 to 1).</summary>
        public double Saturation
        {
            get
            {
                var (r, g, b) = (R / 255.0, G / 255.0, B / 255.0);
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                if (max == min) return 0;
                var l = (max + min) / 2;
                var d = max - min;
                return l > 0.5 ? d / (2 - max - min) : d / (max + min);
            }
        }

        /// <summary>Shortest distance between two hues on the colour wheel.</summary>
        public static double HueGap(ThemeColor a, ThemeColor b)
        {
            var d = Math.Abs(a.Hue - b.Hue);
            return Math.Min(d, 360 - d);
        }

        public bool Equals(ThemeColor other) => R == other.R && G == other.G && B == other.B;
        public override bool Equals(object obj) => obj is ThemeColor other && Equals(other);
        public override int GetHashCode() => (R << 16) | (G << 8) | B;
        public static bool operator ==(ThemeColor left, ThemeColor right) => left.Equals(right);
        public static bool operator !=(ThemeColor left, ThemeColor right) => !left.Equals(right);
        public override string ToString() => ToHex();
    }
}
