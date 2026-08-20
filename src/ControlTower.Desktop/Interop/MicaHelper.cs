using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Desktop.Interop
{
    /// <summary>
    /// Enables Mica system backdrop on Windows 11 (build &gt;= 22000) for a
    /// WPF <see cref="Window"/>. Soft-fallback everywhere else — no crash,
    /// no error, no exception bubbled up. The window background must be
    /// transparent for Mica to read through; <see cref="App"/> swaps that
    /// brush at theme-apply time when Mica is active.
    /// </summary>
    public static class MicaHelper
    {
        private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
        private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
        private const int DWMSBT_MAINWINDOW = 2; // Mica

        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(
            IntPtr hwnd, int attr, ref int attrValue, int attrSize);

        public static bool IsSupported() =>
            MicaSupportPolicy.IsSupported(Environment.OSVersion.Version);

        /// <summary>Apply Mica + immersive dark border. Returns true if applied.</summary>
        public static bool TryApply(Window window, bool darkMode)
        {
            if (window == null) return false;
            if (!IsSupported()) return false;

            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero) return false;

                int useDark = darkMode ? 1 : 0;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref useDark, sizeof(int));

                int backdrop = DWMSBT_MAINWINDOW;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                return hr == 0;
            }
            catch
            {
                // Mica is cosmetic — never fatal.
                return false;
            }
        }
    }
}
