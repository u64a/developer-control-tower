using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Tests;

public class ArchiveStorageRootResolverTests
{
    [Fact]
    public void GetDefaultRoot_UsesUserProfileDeveloperControlTowerArchive()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var expected = Path.GetFullPath(Path.Combine(
            userProfile,
            AppPathsResolver.AppFolderName,
            "Archive"));

        var resolved = ArchiveStorageRootResolver.GetDefaultRoot();

        Assert.Equal(expected, Path.GetFullPath(resolved));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryResolve_BlankValues_FallBackToDefault(string? configured)
    {
        var ok = ArchiveStorageRootResolver.TryResolve(configured ?? string.Empty, out var resolved);
        var expected = Path.GetFullPath(ArchiveStorageRootResolver.GetDefaultRoot());

        Assert.True(ok);
        Assert.Equal(expected, resolved);
    }

    [Fact]
    public void TryResolve_AllowedConfiguredPath_SucceedsAndNormalizes()
    {
        var configured = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeveloperControlTower",
            "archive-root",
            "..",
            "archive-root-final");

        var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    [Theory]
    [InlineData(@"\\server\share\archive-root")]
    [InlineData(@"\\?\C:\archive-root")]
    public void TryResolve_DisallowedPath_Fails(string configured)
    {
        var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolve_FullyQualifiedSafeLocalFolderOutsideProfile_AcceptsUserExample()
    {
        var configured = @"C:\GitHub\Archive";

        var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    [Fact]
    public void TryResolve_SubstAliasToSafeFolder_CanonicalizesToPhysicalPath_WhenSupported()
    {
        var physicalRoot = Path.Combine(
            Path.GetTempPath(),
            "ct-archive-root-subst-safe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(physicalRoot);

        try
        {
            if (!TryWithTemporarySubstDrive(physicalRoot, driveRoot =>
                {
                    var configured = Path.Combine(driveRoot, "archive-safe");
                    var expected = Path.GetFullPath(Path.Combine(physicalRoot, "archive-safe"));

                    var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

                    Assert.True(ok);
                    Assert.Equal(expected, resolved);
                    Assert.DoesNotContain(
                        driveRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                        resolved,
                        StringComparison.OrdinalIgnoreCase);
                    return true;
                }))
            {
                return;
            }
        }
        finally
        {
            TryDeleteDirectory(physicalRoot);
        }
    }

    [Fact]
    public void TryResolve_SubstAliasToInstallDirectory_IsRejected_WhenSupported()
    {
        var installDirectory = Path.GetFullPath(AppContext.BaseDirectory);

        if (!TryWithTemporarySubstDrive(installDirectory, driveRoot =>
            {
                var configured = Path.Combine(driveRoot, "archive-under-install");

                var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

                Assert.False(ok);
                Assert.Equal(string.Empty, resolved);
                return true;
            }))
        {
            return;
        }
    }

    [Fact]
    public void TryResolve_ProfileRoot8Dot3Alias_IsRejected_WhenDistinctAliasIsAvailable()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(profile) ||
            !TryGetDistinctShortAlias(profile, out var shortAlias))
        {
            return;
        }

        var ok = ArchiveStorageRootResolver.TryResolve(shortAlias, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Theory]
    [InlineData("archive-root")]
    [InlineData(@".\archive-root")]
    [InlineData(@"..\archive-root")]
    [InlineData(@"C:archive-root")]
    public void TryResolve_RelativeOrDriveRelativePath_Fails(string configured)
    {
        var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolve_AllowedRootItself_FailsStrictChildConstraint()
    {
        var configured = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolve_RuntimeInstallDirectoryPath_IsRejected()
    {
        var configured = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "archive-under-install"));

        var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);

        var inInstallMethod = typeof(ArchiveStorageRootResolver).GetMethod(
            "IsInApplicationInstallDirectory",
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(inInstallMethod);
        var inInstall = (bool)(inInstallMethod!.Invoke(null, new object[] { configured }) ?? false);
        Assert.True(inInstall);
    }

    [Fact]
    public void TryResolve_PathCrossingExistingJunction_Fails()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ct-archive-root-reparse-" + Guid.NewGuid().ToString("N"));
        var realTarget = Path.Combine(root, "real-target");
        var junctionParent = Path.Combine(root, "junction-parent");
        var junction = Path.Combine(junctionParent, "link");

        Directory.CreateDirectory(realTarget);
        Directory.CreateDirectory(junctionParent);

        try
        {
            if (!TryCreateJunction(junction, realTarget))
            {
                return;
            }

            var configured = Path.Combine(junction, "archive-root");
            var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

            Assert.False(ok);
            Assert.Equal(string.Empty, resolved);
        }
        finally
        {
            TryRemoveJunction(junction);
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryResolve_AllowedAbsolutePathForMissingFolder_Succeeds()
    {
        var configured = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeveloperControlTower",
            "archive-root-tests",
            Guid.NewGuid().ToString("N"));
        Assert.False(Directory.Exists(configured));

        var ok = ArchiveStorageRootResolver.TryResolve(configured, out var resolved);

        Assert.True(ok);
        Assert.Equal(Path.GetFullPath(configured), resolved);
    }

    private static bool TryCreateJunction(string junction, string target)
    {
        try
        {
            var psi = new ProcessStartInfo(
                "cmd.exe",
                $"/c mklink /J \"{junction}\" \"{target}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            proc.WaitForExit(5_000);
            return proc.ExitCode == 0 && Directory.Exists(junction);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryWithTemporarySubstDrive(string targetPath, Func<string, bool> body)
    {
        if (!OperatingSystem.IsWindows() || body == null)
        {
            return false;
        }

        var normalizedTarget = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(normalizedTarget))
        {
            return false;
        }

        if (!TryCreateOwnedSubstMapping(normalizedTarget, out var driveLetter))
        {
            return false;
        }

        var driveRoot = driveLetter + @":\";
        try
        {
            return body(driveRoot);
        }
        finally
        {
            TryDeleteOwnedSubstMapping(driveLetter, normalizedTarget);
        }
    }

    private static bool TryCreateOwnedSubstMapping(string targetPath, out char driveLetter)
    {
        driveLetter = '\0';
        var usedLetters = new HashSet<char>(
            DriveInfo.GetDrives().Select(d => char.ToUpperInvariant(d.Name[0])));
        foreach (var kvp in GetSubstMappings())
        {
            usedLetters.Add(kvp.Key);
        }

        for (var candidate = 'Z'; candidate >= 'D'; candidate--)
        {
            if (usedLetters.Contains(candidate))
            {
                continue;
            }

            if (!TryRunCmd($"/c subst {candidate}: \"{targetPath}\"", out _))
            {
                continue;
            }

            var mappings = GetSubstMappings();
            if (mappings.TryGetValue(candidate, out var mappedTarget) &&
                PathsEqual(mappedTarget, targetPath))
            {
                driveLetter = candidate;
                return true;
            }

            TryDeleteOwnedSubstMapping(candidate, targetPath);
        }

        return false;
    }

    private static void TryDeleteOwnedSubstMapping(char driveLetter, string expectedTarget)
    {
        var mappings = GetSubstMappings();
        if (!mappings.TryGetValue(char.ToUpperInvariant(driveLetter), out var mappedTarget) ||
            !PathsEqual(mappedTarget, expectedTarget))
        {
            return;
        }

        TryRunCmd($"/c subst {char.ToUpperInvariant(driveLetter)}: /d", out _);
    }

    private static Dictionary<char, string> GetSubstMappings()
    {
        var mappings = new Dictionary<char, string>();
        if (!TryRunCmd("/c subst", out var stdout))
        {
            return mappings;
        }

        foreach (var rawLine in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            var arrow = line.IndexOf("=>", StringComparison.Ordinal);
            if (arrow <= 0)
            {
                continue;
            }

            var left = line.Substring(0, arrow).Trim();
            if (left.Length < 2 || left[1] != ':')
            {
                continue;
            }

            var letter = char.ToUpperInvariant(left[0]);
            if (letter < 'A' || letter > 'Z')
            {
                continue;
            }

            var right = line.Substring(arrow + 2).Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(right))
            {
                continue;
            }

            mappings[letter] = right;
        }

        return mappings;
    }

    private static bool TryRunCmd(string arguments, out string stdout)
    {
        stdout = string.Empty;
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return false;
            }

            proc.WaitForExit(5_000);
            if (!proc.HasExited)
            {
                return false;
            }

            stdout = proc.StandardOutput.ReadToEnd();
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetDistinctShortAlias(string fullPath, out string shortAlias)
    {
        shortAlias = string.Empty;
        var normalized = Path.GetFullPath(fullPath);
        if (!TryRunCmd($"/c for %I in (\"{normalized}\") do @echo %~sI", out var stdout))
        {
            return false;
        }

        var candidate = stdout
            .Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        candidate = Path.GetFullPath(candidate);
        if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        shortAlias = candidate;
        return true;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            var normalizedLeft = Path.GetFullPath(left)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRight = Path.GetFullPath(right)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static void TryRemoveJunction(string junction)
    {
        try
        {
            if (!Directory.Exists(junction))
            {
                return;
            }

            var psi = new ProcessStartInfo("cmd.exe", $"/c rmdir \"{junction}\"")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            proc?.WaitForExit(5_000);
        }
        catch
        {
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
