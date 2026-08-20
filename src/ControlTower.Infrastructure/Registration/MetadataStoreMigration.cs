using System;
using System.IO;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Infrastructure.Registration
{
    /// <summary>
    /// One-time, idempotent migration that moves per-project metadata
    /// (<c>.controltower</c>) out of managed repos and into the central config
    /// stub (<c>{configRoot}\portfolio-projects\{id}</c>) — the same location
    /// SSH projects already use. Existing in-repo copies are left in place
    /// (harmless once the app reads from the stub). Safe to run on every
    /// startup: projects that are already centralized are skipped, so it does
    /// no work after the first successful pass. The portfolio file is NOT
    /// rewritten — entries keep pointing at the repo working directory.
    /// </summary>
    public sealed class MetadataStoreMigration
    {
        private readonly string _portfolioPath;
        private readonly IStoreProvider _storeProvider;
        private readonly ProjectMetadataLocator _locator;

        public MetadataStoreMigration(string portfolioPath, IStoreProvider storeProvider)
        {
            _portfolioPath = portfolioPath ?? string.Empty;
            _storeProvider = storeProvider;
            _locator = new ProjectMetadataLocator(Path.GetDirectoryName(_portfolioPath) ?? string.Empty);
        }

        public MetadataMigrationResult Run()
        {
            var result = new MetadataMigrationResult();

            if (string.IsNullOrWhiteSpace(_portfolioPath) || !File.Exists(_portfolioPath))
            {
                return result;
            }

            PortfolioIndex portfolio;
            try
            {
                portfolio = new PortfolioYamlProvider(_portfolioPath, _storeProvider).LoadPortfolio();
            }
            catch
            {
                // A malformed portfolio is surfaced elsewhere; migration is
                // strictly best-effort and must never block startup.
                return result;
            }

            foreach (var project in portfolio.Projects)
            {
                try
                {
                    if (MigrateOne(project))
                    {
                        result.Migrated++;
                    }
                    else
                    {
                        result.Skipped++;
                    }
                }
                catch
                {
                    result.Failed++;
                }
            }

            return result;
        }

        private bool MigrateOne(ProjectRef project)
        {
            if (project == null ||
                string.IsNullOrWhiteSpace(project.Id) ||
                string.IsNullOrWhiteSpace(project.Path))
            {
                return false;
            }

            var workingRoot = project.Path;
            var stubRoot = _locator.ResolveMetadataRoot(project.Id);

            // Already central: SSH stubs and app-created-in-place projects whose
            // working tree IS the stub have nothing to move.
            if (PathsEqual(workingRoot, stubRoot))
            {
                return false;
            }

            var stubYaml = Path.Combine(stubRoot, ".controltower", "project.yml");
            if (File.Exists(stubYaml))
            {
                return false; // Already migrated on a previous run.
            }

            var repoControlTower = Path.Combine(workingRoot, ".controltower");
            var repoYaml = Path.Combine(repoControlTower, "project.yml");
            if (!File.Exists(repoYaml))
            {
                return false; // No in-repo metadata to move.
            }

            CopyDirectory(repoControlTower, Path.Combine(stubRoot, ".controltower"));
            return File.Exists(stubYaml);
        }

        private static void CopyDirectory(string sourceDir, string destDir)
        {
            Directory.CreateDirectory(destDir);

            foreach (var file in Directory.GetFiles(sourceDir))
            {
                var dest = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, dest, overwrite: true);
            }

            foreach (var sub in Directory.GetDirectories(sourceDir))
            {
                CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
            }
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return string.Equals(
                    Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                    StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }

    public sealed class MetadataMigrationResult
    {
        public int Migrated { get; set; }
        public int Skipped { get; set; }
        public int Failed { get; set; }

        public bool DidWork => Migrated > 0 || Failed > 0;
    }
}
