using System.IO;
using ControlTower.Core.Contracts;

namespace ControlTower.Core.UseCases
{
    /// <summary>
    /// Maps a project id to the folder that holds its central
    /// <c>.controltower</c> metadata: <c>{configRoot}\portfolio-projects\{id}</c>.
    /// <c>configRoot</c> is the directory that contains <c>portfolio.yml</c>
    /// (typically the OneDrive-backed app config root). This is the same
    /// location SSH projects have always used, so local and remote projects
    /// now store metadata uniformly outside the target repos.
    /// </summary>
    public sealed class ProjectMetadataLocator : IProjectMetadataLocator
    {
        public const string ManagedProjectsFolder = "portfolio-projects";

        private readonly string _configRoot;

        public ProjectMetadataLocator(string configRoot)
        {
            _configRoot = configRoot ?? string.Empty;
        }

        public string ResolveMetadataRoot(string projectId)
        {
            var id = (projectId ?? string.Empty).Trim();
            return Path.Combine(_configRoot, ManagedProjectsFolder, id);
        }
    }
}
