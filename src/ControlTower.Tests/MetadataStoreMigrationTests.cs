using System;
using System.IO;
using ControlTower.Infrastructure.Registration;
using Xunit;

namespace ControlTower.Tests;

/// <summary>
/// Covers the one-time move of per-project metadata out of repos and into the
/// central config stub. The migration is best-effort, idempotent, and never
/// rewrites the portfolio file.
/// </summary>
public class MetadataStoreMigrationTests
{
    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "ct-mig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Run_MovesInRepoMetadataToStub_AndLeavesRepoCopy()
    {
        var configRoot = NewTempDir();
        try
        {
            var repo = Path.Combine(configRoot, "repos", "alpha");
            Directory.CreateDirectory(Path.Combine(repo, ".controltower"));
            File.WriteAllText(Path.Combine(repo, ".controltower", "project.yml"), "id: alpha\n");
            File.WriteAllText(Path.Combine(repo, ".controltower", "product-map.yml"), "nodes: []\n");

            var portfolioPath = Path.Combine(configRoot, "portfolio.yml");
            File.WriteAllText(portfolioPath,
                "schema_version: 0\nprojects:\n  - id: alpha\n    path: '" + repo + "'\n");

            var result = new MetadataStoreMigration(portfolioPath, null).Run();

            Assert.Equal(1, result.Migrated);

            var stubCt = Path.Combine(configRoot, "portfolio-projects", "alpha", ".controltower");
            Assert.True(File.Exists(Path.Combine(stubCt, "project.yml")), "project.yml should now be in the stub");
            Assert.True(File.Exists(Path.Combine(stubCt, "product-map.yml")), "product-map.yml should be copied too");

            // In-repo copy is LEFT in place (user choice: non-destructive).
            Assert.True(File.Exists(Path.Combine(repo, ".controltower", "project.yml")));

            // Idempotent: a second run migrates nothing.
            var second = new MetadataStoreMigration(portfolioPath, null).Run();
            Assert.Equal(0, second.Migrated);
            Assert.Equal(1, second.Skipped);
        }
        finally
        {
            Directory.Delete(configRoot, true);
        }
    }

    [Fact]
    public void Run_SkipsProjectWhoseWorkingTreeIsTheStub()
    {
        var configRoot = NewTempDir();
        try
        {
            // SSH-style / created-in-place: the portfolio path already IS the stub.
            var stub = Path.Combine(configRoot, "portfolio-projects", "beta");
            Directory.CreateDirectory(Path.Combine(stub, ".controltower"));
            File.WriteAllText(Path.Combine(stub, ".controltower", "project.yml"), "id: beta\n");

            var portfolioPath = Path.Combine(configRoot, "portfolio.yml");
            File.WriteAllText(portfolioPath,
                "schema_version: 0\nprojects:\n  - id: beta\n    path: '" + stub + "'\n");

            var result = new MetadataStoreMigration(portfolioPath, null).Run();

            Assert.Equal(0, result.Migrated);
            Assert.Equal(1, result.Skipped);
        }
        finally
        {
            Directory.Delete(configRoot, true);
        }
    }

    [Fact]
    public void Run_SkipsProjectWithNoInRepoMetadata()
    {
        var configRoot = NewTempDir();
        try
        {
            var repo = Path.Combine(configRoot, "repos", "gamma");
            Directory.CreateDirectory(repo); // no .controltower

            var portfolioPath = Path.Combine(configRoot, "portfolio.yml");
            File.WriteAllText(portfolioPath,
                "schema_version: 0\nprojects:\n  - id: gamma\n    path: '" + repo + "'\n");

            var result = new MetadataStoreMigration(portfolioPath, null).Run();

            Assert.Equal(0, result.Migrated);
            Assert.False(Directory.Exists(Path.Combine(configRoot, "portfolio-projects", "gamma")));
        }
        finally
        {
            Directory.Delete(configRoot, true);
        }
    }
}
