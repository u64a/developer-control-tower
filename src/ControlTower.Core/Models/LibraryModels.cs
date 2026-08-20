using System;
using System.Collections.Generic;

namespace ControlTower.Core.Models
{
    /// <summary>Layout of files in an asset.</summary>
    public enum AssetLayout
    {
        /// <summary>Whole folder pushed as a unit.</summary>
        Folder,

        /// <summary>Pick individual files from the asset to push.</summary>
        FileCollection,
    }

    public sealed class AssetType
    {
        public string Id { get; set; } = string.Empty;
        public AssetLayout Layout { get; set; } = AssetLayout.Folder;
        public string DefaultTarget { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
    }

    public sealed class LibraryAsset
    {
        public string Id { get; set; } = string.Empty;
        public string TypeId { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string LastUpdated { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;

        /// <summary>
        /// Optional asset-level override of the asset-type's default target. Asset-level
        /// wins when set; otherwise the type's DefaultTarget is used.
        /// </summary>
        public string DefaultTargetOverride { get; set; } = string.Empty;

        /// <summary>For FileCollection layout, the named files belonging to this asset.</summary>
        public IList<string> Files { get; set; } = new List<string>();

        public IList<string> Tags { get; set; } = new List<string>();

        /// <summary>Absolute path to the asset folder on disk. Set by the provider.</summary>
        public string AbsoluteRoot { get; set; } = string.Empty;
    }

    public sealed class LibraryIndex
    {
        public string LibraryRoot { get; set; } = string.Empty;
        public IList<AssetType> AssetTypes { get; set; } = new List<AssetType>();
        public IList<LibraryAsset> Assets { get; set; } = new List<LibraryAsset>();
        /// <summary>Explicit load issues for entries that were rejected or could not be read safely.</summary>
        public IList<string> Issues { get; set; } = new List<string>();
    }

    /// <summary>Kind of change for a single file in a push plan.</summary>
    public enum FileChangeKind
    {
        Identical,
        New,
        Modified,
    }

    public sealed class FileChange
    {
        public string RelativePath { get; set; } = string.Empty;
        public FileChangeKind Kind { get; set; }
        public string SourceAbsolutePath { get; set; } = string.Empty;
        public string TargetAbsolutePath { get; set; } = string.Empty;
        public long SourceSize { get; set; }
        public long? TargetSize { get; set; }

        /// <summary>True when this file should be written on Apply. Defaults: New=true, Modified=false, Identical=false.</summary>
        public bool Apply { get; set; }
    }

    public sealed class AssetPushPlan
    {
        public LibraryAsset Asset { get; set; }
        public string TargetRoot { get; set; } = string.Empty;
        public string ResolvedTargetPath { get; set; } = string.Empty;
        /// <summary>SSH preview OS classification used to repeat lexical checks during Apply.</summary>
        public bool? RemoteIsWindows { get; set; }
        public IList<FileChange> Changes { get; set; } = new List<FileChange>();
        public IList<string> Warnings { get; set; } = new List<string>();
    }

    public sealed class AssetPushResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int FilesWritten { get; set; }
        public int FilesSkipped { get; set; }
        public int FilesIdentical { get; set; }
    }

    public sealed class AuditEntry
    {
        public string Asset { get; set; } = string.Empty;
        public string AssetVersion { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string TargetProject { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;
        public DateTime OnUtc { get; set; }
        public int FilesWritten { get; set; }
        public int FilesSkipped { get; set; }
    }
}
