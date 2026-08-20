namespace ControlTower.Infrastructure.Theme
{
    /// <summary>
    /// Decides whether the in-app accent should come from the OS accent or
    /// from a built-in fallback. The decision is trivial today (use OS if
    /// we got a value), but having a named seam means we can grow the rule
    /// (e.g. clamp low-contrast accents) and test it without WPF.
    /// </summary>
    public static class AccentPolicy
    {
        /// <summary>
        /// Returns the OS accent hex when present, otherwise the brand
        /// fallback hex. Inputs are case-insensitive 7/9-char hex strings.
        /// </summary>
        public static string Resolve(string osAccentHex, string fallbackHex)
        {
            if (string.IsNullOrWhiteSpace(osAccentHex)) return fallbackHex;
            return osAccentHex;
        }
    }
}
