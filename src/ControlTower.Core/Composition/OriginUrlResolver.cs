using System;
using ControlTower.Core.Models;

namespace ControlTower.Core.Composition
{
    /// <summary>
    /// Resolves GitHub / Azure DevOps URLs for a project, preferring explicit
    /// configuration in project.yml and falling back to the detected git origin.
    /// </summary>
    public static class OriginUrlResolver
    {
        public static string ResolveGitHubUrl(ProjectDefinition project, RepoSnapshot snapshot)
        {
            if (project?.Launch != null && !string.IsNullOrWhiteSpace(project.Launch.GitHub))
            {
                return project.Launch.GitHub.Trim();
            }

            var origin = snapshot?.OriginUrl;
            if (!string.IsNullOrWhiteSpace(origin) && IsGitHubOrigin(origin))
            {
                return NormalizeOriginToHttps(origin);
            }

            return string.Empty;
        }

        public static string ResolveAdoUrl(ProjectDefinition project, RepoSnapshot snapshot)
        {
            if (project?.Launch != null && !string.IsNullOrWhiteSpace(project.Launch.Ado))
            {
                return project.Launch.Ado.Trim();
            }

            var origin = snapshot?.OriginUrl;
            if (!string.IsNullOrWhiteSpace(origin) && IsAdoOrigin(origin))
            {
                return NormalizeOriginToHttps(origin);
            }

            return string.Empty;
        }

        public static bool IsGitHubOrigin(string origin)
        {
            return !string.IsNullOrWhiteSpace(origin) &&
                   origin.IndexOf("github.com", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool IsAdoOrigin(string origin)
        {
            return !string.IsNullOrWhiteSpace(origin) &&
                   (origin.IndexOf("dev.azure.com", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    origin.IndexOf("visualstudio.com", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        // Convert git@host:owner/repo.git or ssh://git@host/owner/repo.git to https://host/owner/repo
        public static string NormalizeOriginToHttps(string origin)
        {
            var url = (origin ?? string.Empty).Trim();
            if (url.Length == 0)
            {
                return string.Empty;
            }

            if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                return StripDotGit(url);
            }

            if (url.StartsWith("ssh://", StringComparison.OrdinalIgnoreCase))
            {
                var withoutScheme = url.Substring("ssh://".Length);
                var atIdx = withoutScheme.IndexOf('@');
                if (atIdx >= 0)
                {
                    withoutScheme = withoutScheme.Substring(atIdx + 1);
                }
                return StripDotGit("https://" + withoutScheme);
            }

            // scp-style: git@host:owner/repo.git
            var scpAt = url.IndexOf('@');
            var scpColon = url.IndexOf(':');
            if (scpAt > 0 && scpColon > scpAt)
            {
                var host = url.Substring(scpAt + 1, scpColon - scpAt - 1);
                var path = url.Substring(scpColon + 1);
                return StripDotGit("https://" + host + "/" + path);
            }

            return url;
        }

        private static string StripDotGit(string url)
        {
            return url.EndsWith(".git", StringComparison.OrdinalIgnoreCase)
                ? url.Substring(0, url.Length - 4)
                : url;
        }
    }
}
