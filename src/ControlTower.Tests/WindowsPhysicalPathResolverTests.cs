using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using ControlTower.Infrastructure.Configuration;

namespace ControlTower.Tests;

public class WindowsPhysicalPathResolverTests
{
    [Fact]
    public void TryResolveNearestExistingLocalPath_NullInput_Fails()
    {
        var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(null!, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("archive-root")]
    [InlineData(@".\archive-root")]
    [InlineData(@"\\server\share\archive-root")]
    [InlineData(@"\\?\C:\archive-root")]
    [InlineData(@"\\.\C:\archive-root")]
    public void TryResolveNearestExistingLocalPath_InvalidInput_Fails(string input)
    {
        var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(input, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveNearestExistingLocalPath_PathWithNearestExistingAncestor_RebuildsFullPath()
    {
        var root = Path.Combine(Path.GetTempPath(), "ct-physical-root-" + Guid.NewGuid().ToString("N"));
        var existing = Path.Combine(root, "existing-folder");
        Directory.CreateDirectory(existing);

        try
        {
            var candidate = Path.Combine(existing, "missing-a", "missing-b");

            var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(candidate, out var resolved);

            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(candidate), resolved);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryResolveNearestExistingLocalPath_ExistingFilePath_Fails()
    {
        var root = Path.Combine(Path.GetTempPath(), "ct-physical-file-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var file = Path.Combine(root, "owned.txt");
            File.WriteAllText(file, "owned");

            var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(file, out var resolved);

            Assert.False(ok);
            Assert.Equal(string.Empty, resolved);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryResolveNearestExistingLocalPath_UnknownDrive_Fails()
    {
        if (!TryGetUnusedDriveLetter(out var driveLetter))
        {
            return;
        }

        var path = driveLetter + @":\archive-root";
        var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(path, out var resolved);

        Assert.False(ok);
        Assert.Equal(string.Empty, resolved);
    }

    [Fact]
    public void TryResolveNearestExistingLocalPath_PathCrossingExistingJunction_Fails()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ct-physical-junction-" + Guid.NewGuid().ToString("N"));
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

            var candidate = Path.Combine(junction, "archive-root");
            var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(candidate, out var resolved);

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
    public void TryResolveNearestExistingLocalPath_DanglingJunction_IsRejectedUsingReparseMetadata()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ct-physical-dangling-junction-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "target");
        var junctionParent = Path.Combine(root, "junction-parent");
        var junction = Path.Combine(junctionParent, "link");

        Directory.CreateDirectory(target);
        Directory.CreateDirectory(junctionParent);

        try
        {
            Assert.True(TryCreateJunction(junction, target), "Could not create test junction.");

            Directory.Delete(target, recursive: true);

            var metadataReadable = AllowedSettingsRoots.TryGetProvableAttributes(
                junction,
                out var attributes,
                out var definitelyMissing);
            Assert.True(metadataReadable);
            Assert.False(definitelyMissing);
            Assert.True((attributes & FileAttributes.ReparsePoint) != 0);

            var candidate = Path.Combine(junction, "archive-root");
            Assert.True(
                LegacyExistsOnlyNearestDirectoryWalkWouldAccept(candidate, out _),
                "Legacy Directory.Exists-only walk should treat the dangling alias path as reconstructable.");

            var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(candidate, out var resolved);

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
    public void TryResolveNearestExistingLocalPath_IndeterminateAttributes_AreNotTreatedAsMissing()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "ct-physical-indeterminate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        try
        {
            var tooLongSegment = new string('a', 280);
            var indeterminate = Path.Combine(root, tooLongSegment);
            var candidate = Path.Combine(indeterminate, "archive-root");

            Assert.False(Directory.Exists(indeterminate));
            Assert.False(
                AllowedSettingsRoots.TryGetProvableAttributes(
                    indeterminate,
                    out _,
                    out var definitelyMissing));
            Assert.False(definitelyMissing);

            Assert.True(
                LegacyExistsOnlyNearestDirectoryWalkWouldAccept(candidate, out var legacyResolved));
            Assert.Equal(Path.GetFullPath(candidate), legacyResolved);

            var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(candidate, out var resolved);

            Assert.False(ok);
            Assert.Equal(string.Empty, resolved);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    [Fact]
    public void TryResolveNearestExistingLocalPath_Distinct8Dot3Alias_CanonicalizesToLongPhysicalPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "ct-physical-shortname-" + Guid.NewGuid().ToString("N"));
        var longNamedFolder = Path.Combine(root, "Folder Name With Space");
        Directory.CreateDirectory(longNamedFolder);

        try
        {
            if (!TryGetDistinctShortPath(longNamedFolder, out var shortAlias))
            {
                return;
            }

            var candidate = Path.Combine(shortAlias, "archive-root");
            var expected = Path.Combine(Path.GetFullPath(longNamedFolder), "archive-root");
            var ok = WindowsPhysicalPathResolver.TryResolveNearestExistingLocalPath(candidate, out var resolved);

            Assert.True(ok);
            Assert.Equal(Path.GetFullPath(expected), resolved);
            Assert.DoesNotContain("~", resolved, StringComparison.Ordinal);
        }
        finally
        {
            TryDeleteDirectory(root);
        }
    }

    private static bool TryGetUnusedDriveLetter(out char driveLetter)
    {
        driveLetter = '\0';
        var used = new HashSet<char>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            used.Add(char.ToUpperInvariant(drive.Name[0]));
        }

        foreach (var mapping in GetSubstMappings().Keys)
        {
            used.Add(mapping);
        }

        for (var candidate = 'Z'; candidate >= 'D'; candidate--)
        {
            if (!used.Contains(candidate))
            {
                driveLetter = candidate;
                return true;
            }
        }

        return false;
    }

    private static Dictionary<char, string> GetSubstMappings()
    {
        var mappings = new Dictionary<char, string>();
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", "/c subst")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc == null)
            {
                return mappings;
            }

            proc.WaitForExit(5_000);
            if (!proc.HasExited || proc.ExitCode != 0)
            {
                return mappings;
            }

            var stdout = proc.StandardOutput.ReadToEnd();
            foreach (var rawLine in stdout.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
            {
                var line = rawLine.Trim();
                var arrow = line.IndexOf("=>", StringComparison.Ordinal);
                if (arrow <= 0)
                {
                    continue;
                }

                var left = line.Substring(0, arrow).Trim();
                if (left.Length >= 2 && left[1] == ':')
                {
                    mappings[char.ToUpperInvariant(left[0])] = line.Substring(arrow + 2).Trim();
                }
            }
        }
        catch
        {
        }

        return mappings;
    }

    private static bool TryGetDistinctShortPath(string longPath, out string shortPath)
    {
        shortPath = string.Empty;
        try
        {
            var normalized = Path.GetFullPath(longPath);
            var capacity = 512u;
            while (capacity <= 32768)
            {
                var buffer = new StringBuilder((int)capacity);
                var length = GetShortPathName(normalized, buffer, capacity);
                if (length == 0)
                {
                    return false;
                }

                if (length < capacity)
                {
                    var candidate = Path.GetFullPath(buffer.ToString());
                    if (string.Equals(candidate, normalized, StringComparison.OrdinalIgnoreCase))
                    {
                        return false;
                    }

                    shortPath = candidate;
                    return true;
                }

                capacity = length + 1;
            }
        }
        catch
        {
        }

        return false;
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

    private static bool LegacyExistsOnlyNearestDirectoryWalkWouldAccept(
        string fullPath,
        out string reconstructedPath)
    {
        reconstructedPath = string.Empty;
        if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathFullyQualified(fullPath))
        {
            return false;
        }

        try
        {
            var normalized = Path.GetFullPath(fullPath);
            var missing = new List<string>();
            var current = normalized;

            while (!Directory.Exists(current))
            {
                var parent = Path.GetDirectoryName(current);
                var leaf = Path.GetFileName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.IsNullOrWhiteSpace(leaf) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                missing.Add(leaf);
                current = parent;
            }

            missing.Reverse();
            var rebuilt = current;
            foreach (var segment in missing)
            {
                if (string.IsNullOrWhiteSpace(segment) ||
                    segment == "." ||
                    segment == ".." ||
                    segment.IndexOf(Path.DirectorySeparatorChar) >= 0 ||
                    segment.IndexOf(Path.AltDirectorySeparatorChar) >= 0)
                {
                    return false;
                }

                rebuilt = Path.GetFullPath(Path.Combine(rebuilt, segment));
            }

            reconstructedPath = rebuilt;
            return true;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint shortPathLength);
}
