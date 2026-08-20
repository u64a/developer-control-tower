using System;
using System.IO;
using System.Linq;

namespace ControlTower.Infrastructure.Library
{
    public enum LibraryBootstrapSource
    {
        Existing,
        LegacyApplication,
        Starter,
        Unavailable,
        Conflict
    }

    public sealed record LibraryBootstrapResult(
        LibraryBootstrapSource Source,
        string LibraryRoot,
        string Message)
    {
        public bool Changed =>
            Source == LibraryBootstrapSource.LegacyApplication ||
            Source == LibraryBootstrapSource.Starter;

        public bool HasWarning =>
            Source == LibraryBootstrapSource.Unavailable ||
            Source == LibraryBootstrapSource.Conflict;
    }

    /// <summary>
    /// Initializes the default user library without modifying an existing
    /// library. Legacy app-side content takes precedence over the packaged
    /// starter seed so upgrades preserve user captures.
    /// </summary>
    public static class LibraryBootstrapper
    {
        private const string RegistryFileName = "library.yml";

        public static LibraryBootstrapResult EnsureInitialized(
            string libraryRoot,
            string legacyApplicationRoot,
            string starterRoot)
        {
            if (string.IsNullOrWhiteSpace(libraryRoot))
            {
                throw new ArgumentException(
                    "A default library path is required.",
                    nameof(libraryRoot));
            }

            var destination = Path.GetFullPath(libraryRoot);
            if (File.Exists(Path.Combine(destination, RegistryFileName)))
            {
                return new LibraryBootstrapResult(
                    LibraryBootstrapSource.Existing,
                    destination,
                    "Using the existing user library.");
            }

            if (Directory.Exists(destination) &&
                Directory.EnumerateFileSystemEntries(destination).Any())
            {
                return new LibraryBootstrapResult(
                    LibraryBootstrapSource.Conflict,
                    destination,
                    "The default library folder contains files but no library.yml. " +
                    "It was left unchanged.");
            }

            var legacy = ResolveUsableSource(legacyApplicationRoot);
            var starter = ResolveUsableSource(starterRoot);
            var source = legacy ?? starter;
            if (source == null)
            {
                Directory.CreateDirectory(destination);
                return new LibraryBootstrapResult(
                    LibraryBootstrapSource.Unavailable,
                    destination,
                    "No existing library or packaged starter library was found.");
            }

            var sourceKind = legacy != null
                ? LibraryBootstrapSource.LegacyApplication
                : LibraryBootstrapSource.Starter;

            CopyAtomically(source, destination);
            return new LibraryBootstrapResult(
                sourceKind,
                destination,
                sourceKind == LibraryBootstrapSource.LegacyApplication
                    ? "Migrated the legacy application library to the user-writable library."
                    : "Initialized the user-writable library from the packaged starter.");
        }

        private static string ResolveUsableSource(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return null;
            }

            var fullPath = Path.GetFullPath(path);
            return File.Exists(Path.Combine(fullPath, RegistryFileName))
                ? fullPath
                : null;
        }

        private static void CopyAtomically(string sourceRoot, string destinationRoot)
        {
            var parent = Path.GetDirectoryName(destinationRoot);
            if (string.IsNullOrWhiteSpace(parent))
            {
                throw new InvalidOperationException(
                    "The default library path has no parent directory.");
            }

            Directory.CreateDirectory(parent);
            var staging = destinationRoot + ".seed-" + Guid.NewGuid().ToString("N");
            try
            {
                CopyDirectory(sourceRoot, staging);

                if (Directory.Exists(destinationRoot))
                {
                    if (Directory.EnumerateFileSystemEntries(destinationRoot).Any())
                    {
                        throw new IOException(
                            "The default library changed while it was being initialized.");
                    }

                    Directory.Delete(destinationRoot);
                }

                Directory.Move(staging, destinationRoot);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }

        private static void CopyDirectory(string sourceRoot, string destinationRoot)
        {
            Directory.CreateDirectory(destinationRoot);

            foreach (var file in Directory.EnumerateFiles(
                sourceRoot,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(file);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                File.Copy(
                    file,
                    Path.Combine(destinationRoot, Path.GetFileName(file)),
                    overwrite: false);
            }

            foreach (var directory in Directory.EnumerateDirectories(
                sourceRoot,
                "*",
                SearchOption.TopDirectoryOnly))
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                CopyDirectory(
                    directory,
                    Path.Combine(destinationRoot, Path.GetFileName(directory)));
            }
        }
    }
}
