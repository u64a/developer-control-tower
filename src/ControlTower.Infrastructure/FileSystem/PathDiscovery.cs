using System;
using System.IO;

namespace ControlTower.Infrastructure.FileSystem
{
    public static class PathDiscovery
    {
        public static string FindRepoRoot()
        {
            return FindRepoRoot(AppDomain.CurrentDomain.BaseDirectory);
        }

        public static string FindRepoRoot(string startPath)
        {
            var current = new DirectoryInfo(startPath);
            while (current != null)
            {
                var portfolio = Path.Combine(current.FullName, "portfolio.yml");
                var git = Path.Combine(current.FullName, ".git");
                if (File.Exists(portfolio) || Directory.Exists(git))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            return AppDomain.CurrentDomain.BaseDirectory;
        }
    }
}
