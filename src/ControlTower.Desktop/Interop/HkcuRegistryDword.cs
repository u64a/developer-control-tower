using Microsoft.Win32;
using ControlTower.Infrastructure.Theme;

namespace ControlTower.Desktop.Interop
{
    /// <summary>
    /// Reads <c>AppsUseLightTheme</c> from HKCU via the real Windows
    /// registry. Used by <see cref="App"/> at startup and on
    /// <see cref="SystemEvents.UserPreferenceChanged"/> to follow the OS
    /// light/dark preference.
    /// </summary>
    public sealed class HkcuRegistryDword : IRegistryDword
    {
        public int? Read(string keyPath, string valueName)
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(keyPath);
                if (key == null) return null;
                var raw = key.GetValue(valueName);
                if (raw is int i) return i;
                return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
