using System;
using System.Collections.Generic;

namespace ControlTower.Infrastructure.Launch
{
    /// <summary>
    /// Per ADR-004 §3, only files with these extensions may be opened via
    /// ShellExecute. Executable extensions are handled separately (only the
    /// configured editor command may be invoked).
    /// </summary>
    internal static class LaunchAllowlist
    {
        public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".md",
            ".txt",
            ".yml",
            ".yaml",
            ".json",
            ".docx",
            ".pptx",
            ".xlsx",
            ".pdf",
            ".png",
            ".jpg",
            ".jpeg",
            ".svg",
            ".cs",
            ".xaml",
            ".csproj",
            ".sln"
        };

        public static readonly HashSet<string> BlockedExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".exe",
            ".bat",
            ".cmd",
            ".ps1",
            ".msi",
            ".scr",
            ".vbs",
            ".js",
            ".jse",
            ".wsf",
            ".com",
            ".lnk"
        };
    }
}
