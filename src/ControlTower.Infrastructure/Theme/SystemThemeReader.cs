namespace ControlTower.Infrastructure.Theme
{
    public enum SystemTheme
    {
        Dark = 0,
        Light = 1
    }

    /// <summary>
    /// Tiny abstraction over a registry DWORD read so the parsing rule in
    /// <see cref="SystemThemeReader"/> can be unit-tested without touching
    /// the actual Windows registry (Infrastructure targets net8.0 cross-OS).
    /// </summary>
    public interface IRegistryDword
    {
        int? Read(string keyPath, string valueName);
    }

    /// <summary>
    /// Resolves whether the OS prefers light or dark mode for apps based on
    /// the well-known <c>AppsUseLightTheme</c> DWORD under HKCU Personalize.
    /// 0 = dark, 1 = light. We default to dark when the value is missing —
    /// matches the app's prior PR-A default and keeps contrast safe.
    /// </summary>
    public static class SystemThemeReader
    {
        public const string PersonalizeKey =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize";
        public const string AppsUseLightValue = "AppsUseLightTheme";

        public static SystemTheme Resolve(IRegistryDword registry, SystemTheme fallback = SystemTheme.Dark)
        {
            if (registry == null) return fallback;

            var value = registry.Read(PersonalizeKey, AppsUseLightValue);
            if (!value.HasValue) return fallback;

            return value.Value == 0 ? SystemTheme.Dark : SystemTheme.Light;
        }
    }
}
