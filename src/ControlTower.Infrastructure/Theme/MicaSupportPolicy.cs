using System;

namespace ControlTower.Infrastructure.Theme
{
    /// <summary>
    /// Pure-logic gate for whether the host OS supports the Win11 Mica
    /// system backdrop (DWMWA_SYSTEMBACKDROP_TYPE). Mica was introduced
    /// in Windows 11 22000. We keep the policy in <c>Infrastructure</c>
    /// so the WPF interop call in Desktop stays a thin shim and the
    /// build-number gate is testable on any host (including the CI
    /// xUnit runner which targets <c>net8.0</c>, not Windows).
    /// </summary>
    public static class MicaSupportPolicy
    {
        public const int MinimumBuild = 22000;

        public static bool IsSupported(Version osVersion)
        {
            if (osVersion == null) return false;

            // Windows 10/11 both report Major == 10; only the build number
            // disambiguates. Anything below 22000 is Win10 (or an early
            // Win11 dev build) — soft fallback, no crash, no error.
            if (osVersion.Major < 10) return false;
            if (osVersion.Major == 10 && osVersion.Build < MinimumBuild) return false;
            return true;
        }
    }
}
