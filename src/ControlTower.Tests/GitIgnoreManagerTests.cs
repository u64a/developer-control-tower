using System.IO;
using System.Linq;
using ControlTower.Infrastructure.Registration;

namespace ControlTower.Tests;

public class GitIgnoreManagerTests
{
    [Fact]
    public void Ensure_NoGitRepo_NoOp()
    {
        var dir = MakeTempDir();
        try
        {
            GitIgnoreManager.EnsureControlTowerIgnored(dir);
            Assert.False(File.Exists(Path.Combine(dir, ".gitignore")));
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Ensure_NewGitRepo_NoGitignore_CreatesOne()
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            GitIgnoreManager.EnsureControlTowerIgnored(dir);

            var gi = Path.Combine(dir, ".gitignore");
            Assert.True(File.Exists(gi));
            var contents = File.ReadAllText(gi);
            Assert.Contains(".controltower/", contents);
        }
        finally { Cleanup(dir); }
    }

    [Fact]
    public void Ensure_ExistingGitignore_AppendsIfMissing()
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var gi = Path.Combine(dir, ".gitignore");
            File.WriteAllText(gi, "bin/\nobj/\n");

            GitIgnoreManager.EnsureControlTowerIgnored(dir);

            var contents = File.ReadAllText(gi);
            Assert.Contains("bin/", contents);
            Assert.Contains("obj/", contents);
            Assert.Contains(".controltower/", contents);
        }
        finally { Cleanup(dir); }
    }

    [Theory]
    [InlineData(".controltower/")]
    [InlineData(".controltower")]
    [InlineData("/.controltower/")]
    [InlineData("/.controltower")]
    public void Ensure_ExistingPattern_NoDuplicate(string existingPattern)
    {
        var dir = MakeTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(dir, ".git"));
            var gi = Path.Combine(dir, ".gitignore");
            File.WriteAllText(gi, "bin/\n" + existingPattern + "\nobj/\n");

            GitIgnoreManager.EnsureControlTowerIgnored(dir);

            var matches = File.ReadAllLines(gi)
                .Count(l => l.Trim().Equals(existingPattern, System.StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1, matches);
        }
        finally { Cleanup(dir); }
    }

    private static string MakeTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "dct_gi_" + Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
    }
}
