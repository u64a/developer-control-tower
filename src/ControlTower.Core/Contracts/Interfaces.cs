using System;
using System.Collections.Generic;
using ControlTower.Core.Models;

namespace ControlTower.Core.Contracts
{
    public interface IPortfolioProvider
    {
        PortfolioIndex LoadPortfolio();

        /// <summary>
        /// Persists the portfolio index back to disk. Writes are atomic
        /// (temp file + copy). Comments and unknown fields in the existing
        /// portfolio file are NOT preserved — the writer rewrites from the
        /// in-memory model.
        /// </summary>
        void SavePortfolio(PortfolioIndex portfolio);
    }

    public interface IProjectProvider
    {
        ProjectLoadResult LoadProject(string projectRootPath);

        /// <summary>
        /// Loads a project whose working tree and metadata live in different
        /// locations. <paramref name="workingRootPath"/> is the repo working
        /// directory (used for <c>ProjectRootPath</c> / launch / roadmap
        /// defaults); <paramref name="metadataRootPath"/> is the folder that
        /// CONTAINS the <c>.controltower</c> metadata directory. Metadata is
        /// now stored centrally per project id rather than inside the repo,
        /// so these differ for local projects. The default delegates to the
        /// single-arg overload for fakes/legacy providers where they coincide.
        /// </summary>
        ProjectLoadResult LoadProject(string workingRootPath, string metadataRootPath)
            => LoadProject(metadataRootPath);
    }

    /// <summary>
    /// Resolves where a project's <c>.controltower</c> metadata lives. Metadata
    /// is stored centrally under <c>{configRoot}\portfolio-projects\{id}</c>
    /// (the OneDrive stub), never inside the managed repo working tree.
    /// </summary>
    public interface IProjectMetadataLocator
    {
        string ResolveMetadataRoot(string projectId);
    }

    public interface IProductMapProvider
    {
        ProductMapLoadResult LoadProductMap(string projectRootPath, string sourceRef);
    }

    /// <summary>
    /// V0-only adapter that renders <c>product-map.yml</c> (or a resolved
    /// roadmap) as a flat node list for the UI. This is not a planning
    /// system contract; do not extend. See ADR-001.
    /// </summary>
    /// <remarks>Deprecated for any post-V0 use: "V0-only product-map view facade; do not extend. See ADR-001."</remarks>
    public interface IPlanningBoardProvider
    {
        PlanningBoardLoadResult LoadPlanningBoard(string projectRootPath);

        PlanningBoardLoadResult ParseFromContent(string yamlContent, string sourceLabel);
    }

    public interface IRepoScanner
    {
        RepoSnapshot Scan(string repoPath);
    }

    public interface ILibraryProvider
    {
        LibraryIndex LoadLibrary(string libraryRoot);

        LibraryAsset GetAsset(string libraryRoot, string assetId);

        /// <summary>Append a new asset to library.yml + write its asset.yml.
        /// Best-effort preservation of the registry; comments may be lost.</summary>
        void RegisterAsset(string libraryRoot, LibraryAsset asset, string fromProjectId);

        /// <summary>Stamp an existing asset's last_updated (and optionally append
        /// a source_history entry) without touching its files. Used after a pull
        /// to mark the library copy as freshly synced from a project.</summary>
        void TouchAsset(string libraryRoot, string assetId, DateTime updatedUtc, string fromProjectId);
    }

    public interface IAssetCaptureService
    {
        AssetCaptureResult CaptureFromLocal(
            string libraryRoot,
            LibraryIndex index,
            string assetId,
            string assetTypeId,
            string sourceFolder,
            string fromProjectId);

        /// <summary>
        /// Capture an asset from an SSH-hosted project. The remoteRelativePath
        /// is the asset folder's path inside the project root (e.g.
        /// ".github/skills/my-skill"). Files are downloaded via SFTP into the
        /// library and registered.
        /// </summary>
        AssetCaptureResult CaptureFromSsh(
            string libraryRoot,
            LibraryIndex index,
            string assetId,
            string assetTypeId,
            string sshTarget,
            string remoteRelativePath,
            string fromProjectId);
    }

    public sealed class AssetCaptureResult
    {
        public bool Success { get; init; }
        public string Message { get; init; } = string.Empty;
        public string AssetId { get; init; } = string.Empty;
        public int FilesCopied { get; init; }
    }

    public interface IAssetTransferService
    {
        AssetPushPlan PreparePush(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            string targetProjectRoot,
            IEnumerable<string> includedFiles = null);

        AssetPushResult ApplyPush(AssetPushPlan plan);

        /// <summary>
        /// Build a plan to pull files from a project back into the library.
        /// The plan uses the same FileChange shape as push: SourceAbsolutePath
        /// points at the project file, TargetAbsolutePath points at the library
        /// file. ApplyPush is then valid to commit the pull.
        /// </summary>
        AssetPushPlan PreparePull(
            LibraryAsset asset,
            AssetType assetType,
            string libraryRoot,
            string sourceProjectRoot);
    }

    public interface IAuditLogger
    {
        void RecordPush(string libraryRoot, AuditEntry entry);
    }

    /// <summary>Resolves the raw YAML content of a project's roadmap, fetching
    /// from local filesystem or SSH remote as needed. Returns null when no
    /// roadmap is found.</summary>
    public interface IRoadmapResolver
    {
        RoadmapContent Resolve(ProjectDefinition project);
    }

    public sealed class RoadmapContent
    {
        public string Yaml { get; init; } = string.Empty;
        public string SourceLabel { get; init; } = string.Empty;
    }

    public interface ISnapshotStore
    {
        RepoSnapshot Load(string projectId);

        void Save(string projectId, RepoSnapshot snapshot);
    }

    public interface ILaunchService
    {
        LaunchResult Launch(ProjectDefinition project, LaunchTargetKind targetKind);
    }

    public interface IProjectRegistrationService
    {
        ProjectRegistrationResult RegisterProject(ProjectRegistrationRequest request);

        ProjectRegistrationResult RemoveProject(string projectId);
    }

    public interface IProjectCreationService
    {
        ProjectCreationResult CreateProject(ProjectCreationRequest request);
    }

    public interface IStoreProvider
    {
        IReadOnlyList<RepoStore> GetStores();

        RepoStore GetStore(string storeId);

        /// <summary>
        /// Resolves the local filesystem path for a project in the given store.
        /// For SSH stores, returns the remote path (host:root/folder).
        /// </summary>
        string ResolveProjectPath(string storeId, string projectId, string folder);
    }
}
