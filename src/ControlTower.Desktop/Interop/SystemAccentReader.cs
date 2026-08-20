using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace ControlTower.Desktop.Interop
{
    /// <summary>
    /// Reads the current Windows accent color via WPF's built-in
    /// <see cref="SystemParameters.WindowGlassBrush"/> (no UWP / WinRT
    /// dependency required). Returns null when no glass color is
    /// available (rare; happens in remote sessions).
    /// </summary>
    public static class SystemAccentReader
    {
        public static Color? TryReadAccent()
        {
            try
            {
                if (SystemParameters.WindowGlassBrush is SolidColorBrush b)
                {
                    return b.Color;
                }
            }
            catch
            {
            }

            // Fallback path: DWM accent color (registry).
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\DWM");
                var raw = key?.GetValue("AccentColor");
                if (raw is int packed)
                {
                    // DWM stores ABGR.
                    byte a = (byte)((packed >> 24) & 0xFF);
                    byte b = (byte)((packed >> 16) & 0xFF);
                    byte g = (byte)((packed >> 8) & 0xFF);
                    byte r = (byte)(packed & 0xFF);
                    if (a == 0) a = 0xFF;
                    return Color.FromArgb(a, r, g, b);
                }
            }
            catch
            {
            }

            return null;
        }
    }
}
