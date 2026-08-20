using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using ControlTower.Core.Composition;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Infrastructure.Cache;
using ControlTower.Infrastructure.Configuration;
using ControlTower.Infrastructure.Credentials;
using ControlTower.Infrastructure.Diagnostics;
using ControlTower.Infrastructure.FileSystem;
using ControlTower.Infrastructure.Git;
using ControlTower.Infrastructure.Launch;
using ControlTower.Infrastructure.Library;
using ControlTower.Infrastructure.Registration;
using ControlTower.Infrastructure.Ssh;
using ControlTower.Infrastructure.Update;
using ControlTower.Infrastructure.Yaml;

namespace ControlTower.Desktop.Bootstrap
{
    /// <summary>
    /// Hand-rolled composition root for the WPF shell. Owns the long-lived
    /// process singletons (credential store, SSH service, resolved app paths)
    /// and rebuilds the settings-dependent service graph on demand.
    /// Per ADR-003 the public <see cref="ControlTowerService"/> surface stays
    /// stable — this root only wires its constructor dependencies.
    /// </summary>
    public sealed class CompositionRoot
    {
        public CompositionRoot()
        {
            var appPaths = AppPathsResolver.Resolve();
            ConfigRoot = appPaths.ConfigRoot;
            SettingsPath = appPaths.GlobalSettingsPath;
            PortfolioPath = appPaths.PortfolioPath;
            ProfilesPath = appPaths.ProfilesPath;
            ActiveProfilePath = appPaths.ActiveProfilePath;
            DefaultLibraryPath = appPaths.DefaultLibraryPath;
            LocalStateRoot = appPaths.LocalStateRoot;
            LegacyInstallLocator.TryRecordCurrentSourceInstall(
                appPaths.LegacyInstallPath,
                AppContext.BaseDirectory);
            LegacyInstallRoot = LegacyInstallLocator.Resolve(
                appPaths.LegacyInstallPath,
                AppContext.BaseDirectory);
            CredentialStore = new WindowsCredentialStore();
            var hostKeyPolicy = new StrictHostKeyPolicy(CredentialStore);
            SshClientFactory = new SshNetClientFactory(hostKeyPolicy);
            SshService = new SshNetService(SshClientFactory);
            // Process-lifetime singleton: every long-running git operation
            // (Restore now, Relocate / Scan later) acquires this lock so
            // we don't run two heavy git invocations in parallel.
            GitOperationLock = new LongRunningGitOperationLock();
            ShellLauncher = new WindowsShellLauncher();
            UninstallService = VelopackUninstallService.TryCreate(
                appPaths,
                ShellLauncher);

            RunStartupMigrations();
        }

        /// <summary>
        /// One-time, idempotent startup housekeeping. Currently: move any
        /// per-project <c>.controltower</c> metadata out of managed repos and
        /// into the central config stub. Runs once per process (the root is
        /// constructed once at startup, including after a self-update relaunch),
        /// and is a no-op once every project is already centralized.
        /// </summary>
        private void RunStartupMigrations()
        {
            try
            {
                var settings = new ToolSettingsProvider().Load(SettingsPath);
                var storeProvider = new StoreProvider(settings.Stores);
                var result = new MetadataStoreMigration(PortfolioPath, storeProvider).Run();
                if (result.DidWork)
                {
                    AppLogger.Info(
                        "startup",
                        $"Metadata migration: {result.Migrated} moved to central store, " +
                        $"{result.Skipped} already centralized, {result.Failed} failed.");
                }
            }
            catch (Exception ex)
            {
                // Never let startup housekeeping block the app.
                AppLogger.Warn("startup", "Metadata migration skipped: " + ex.Message);
            }
        }

        public ICredentialStore CredentialStore { get; }

        public ISshService SshService { get; }

        public SshNetClientFactory SshClientFactory { get; }

        public string SettingsPath { get; }

        public string ConfigRoot { get; }

        public string PortfolioPath { get; }

        public string ProfilesPath { get; }

        public string ActiveProfilePath { get; }

        public string DefaultLibraryPath { get; }

        public string LocalStateRoot { get; }

        public string LegacyInstallRoot { get; }

        public IApplicationUninstallService UninstallService { get; }

        public ILongRunningGitOperationLock GitOperationLock { get; }

        public IShellLauncher ShellLauncher { get; }

        /// <summary>
        /// Builds a fresh settings-dependent service graph by reading the
        /// current global settings file. Called at startup and after the
        /// Settings dialog saves.
        /// </summary>
        public DesktopSession BuildSession(bool forceAllProjects = false)
        {
            var settings = new ToolSettingsProvider().Load(SettingsPath);
            var storeProvider = new StoreProvider(settings.Stores);
            var currentStores = storeProvider.GetStores();
            var libraryPath = ResolveLibraryPath(settings.LibraryPath);
            var profileProvider = new WorkspaceProfileYamlProvider(ProfilesPath);
            var activeProfileStore = new ActiveProfileSelectionStore(ActiveProfilePath);
            var profileManager = new WorkspaceProfileManager(
                profileProvider,
                activeProfileStore);
            var profileState = profileManager.LoadState();
            if (forceAllProjects)
            {
                profileState = new WorkspaceProfileState(
                    profileState.PersistedProfiles,
                    WorkspaceProfilePolicy.CreateAllProjectsProfile(),
                    profileState.Issues);
            }

            new SshConfigManager().UpdateSshConfig(currentStores);

            var registrationService = new ProjectRegistrationService(PortfolioPath, storeProvider);
            var creationService = new ProjectCreationService(
                storeProvider, registrationService, SshService, CredentialStore);

            var portfolioProvider = new PortfolioYamlProvider(PortfolioPath, storeProvider);
            var projectProvider = new ProjectYamlProvider();
            var metadataLocator = new ProjectMetadataLocator(
                Path.GetDirectoryName(PortfolioPath) ?? string.Empty);

            var controlTowerService = new ControlTowerService(
                portfolioProvider,
                projectProvider,
                new ProductMapYamlProvider(),
                new PlanningBoardYamlProvider(),
                new GitRepoScanner(settings, SshService, CredentialStore, storeProvider),
                new LocalSnapshotStore(),
                new WindowsLaunchService(settings),
                registrationService,
                new RoadmapResolver(SshService, storeProvider, CredentialStore),
                metadataLocator,
                storeProvider,
                profileState.ActiveProfile);

            var libraryProvider = new YamlLibraryProvider();
            var assetTransferService = new AssetTransferRouter(
                new LocalAssetTransferService(),
                new SshAssetTransferService(storeProvider, CredentialStore, SshClientFactory));
            var assetCaptureService = new AssetCaptureService(
                libraryProvider,
                storeProvider,
                CredentialStore,
                SshClientFactory);
            var auditLogger = new LibraryAuditLogger();

            // Restore wiring: build the git adapter / inspector / clone service
            // and the orchestrator that drives the per-row state machine.
            var gitAdapter = new GitProcessAdapter(settings);
            var workspaceInspector = new GitWorkspaceInspector(gitAdapter);
            var cloneService = new AsyncCloneService(gitAdapter);
            var quarantineService = new QuarantineService();
            var missingScanner = new MissingProjectScanner(workspaceInspector);
            var restoreOrchestrator = new RestoreOrchestrator(
                cloneService, quarantineService, GitOperationLock);

            // Phase C: scan-and-register service reuses the same inspector
            // (no extra git plumbing) and reads the existing portfolio +
            // project providers to mark duplicates.
            var repoScanService = new RepoScanService(
                workspaceInspector,
                portfolioProvider,
                projectProvider,
                metadataLocator,
                profileState.ActiveProfile);

            // Installed Velopack releases update from their baked-in channel.
            // Unmanaged/source installations retain the existing Git updater
            // during the migration period.
            var packagedUpdateService =
                VelopackUpdateService.TryCreateFromEntryAssembly();
            IUpdateService updateService = packagedUpdateService != null
                ? packagedUpdateService
                : new UpdateService(gitAdapter, ShellLauncher);
            var updateOptions = settings.UpdateOptions ?? UpdateOptions.Defaults();

            // Phase B: Relocate wiring. Reuses the same gitAdapter,
            // workspaceInspector, cloneService, registrationService and
            // gitOperationLock as Restore; adds an SSH-side inspector and
            // the routing file-transfer helper.
            var sshGitInspector = new SshGitInspector(SshService);
            var relocateFileTransfer = new RelocateFileTransfer(SshClientFactory);
            var relocateService = new RelocateProjectService(
                gitAdapter,
                workspaceInspector,
                sshGitInspector,
                cloneService,
                SshService,
                CredentialStore,
                relocateFileTransfer,
                registrationService,
                storeProvider,
                GitOperationLock,
                deleteToRecycleBin: (path, _) =>
                {
                    try
                    {
                        Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                            path,
                            Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                            Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin,
                            Microsoft.VisualBasic.FileIO.UICancelOption.ThrowException);
                        return true;
                    }
                    catch
                    {
                        return false;
                    }
                });

            return new DesktopSession(
                currentStores,
                libraryPath,
                creationService,
                controlTowerService,
                libraryProvider,
                assetTransferService,
                assetCaptureService,
                auditLogger,
                portfolioProvider,
                projectProvider,
                storeProvider,
                missingScanner,
                restoreOrchestrator,
                GitOperationLock,
                repoScanService,
                registrationService,
                updateService,
                updateOptions,
                relocateService,
                profileManager,
                profileState);
        }

        private string ResolveLibraryPath(string configured)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                return Environment.ExpandEnvironmentVariables(configured);
            }

            LibraryBootstrapResult result;
            try
            {
                result = LibraryBootstrapper.EnsureInitialized(
                    DefaultLibraryPath,
                    ResolveLegacyLibraryRoot(),
                    Path.Combine(AppContext.BaseDirectory, "library-seed"));
            }
            catch (Exception ex) when (
                ex is IOException ||
                ex is UnauthorizedAccessException ||
                ex is ArgumentException ||
                ex is NotSupportedException ||
                ex is PathTooLongException ||
                ex is SecurityException)
            {
                AppLogger.Error(
                    "Library",
                    "The writable library could not be initialized at '" +
                    DefaultLibraryPath + "'. The library will remain unavailable.",
                    ex);
                return DefaultLibraryPath;
            }
            if (result.Changed)
            {
                AppLogger.Info("Library", result.Message + " Path: " + result.LibraryRoot);
            }
            else if (result.HasWarning)
            {
                AppLogger.Warn("Library", result.Message + " Path: " + result.LibraryRoot);
            }

            return result.LibraryRoot;
        }

        private string ResolveLegacyLibraryRoot()
        {
            if (!string.IsNullOrWhiteSpace(LegacyInstallRoot))
            {
                return Path.Combine(LegacyInstallRoot, "library");
            }

            return Path.Combine(AppContext.BaseDirectory, "library");
        }
    }

    /// <summary>
    /// Immutable snapshot of the settings-dependent service graph the WPF
    /// windows consume. Recreated whenever settings change.
    /// </summary>
    public sealed class DesktopSession
    {
        public DesktopSession(
            IReadOnlyList<RepoStore> currentStores,
            string libraryPath,
            IProjectCreationService creationService,
            ControlTowerService controlTowerService,
            ILibraryProvider libraryProvider,
            IAssetTransferService assetTransferService,
            IAssetCaptureService assetCaptureService,
            IAuditLogger auditLogger,
            IPortfolioProvider portfolioProvider,
            IProjectProvider projectProvider,
            IStoreProvider storeProvider,
            IMissingProjectScanner missingProjectScanner,
            IRestoreOrchestrator restoreOrchestrator,
            ILongRunningGitOperationLock gitOperationLock,
            IRepoScanService repoScanService,
            IProjectRegistrationService projectRegistrationService,
            IUpdateService updateService,
            UpdateOptions updateOptions,
            IRelocateProjectService relocateService,
            WorkspaceProfileManager profileManager,
            WorkspaceProfileState profileState)
        {
            CurrentStores = currentStores;
            LibraryPath = libraryPath;
            CreationService = creationService;
            ControlTowerService = controlTowerService;
            LibraryProvider = libraryProvider;
            AssetTransferService = assetTransferService;
            AssetCaptureService = assetCaptureService;
            AuditLogger = auditLogger;
            PortfolioProvider = portfolioProvider;
            ProjectProvider = projectProvider;
            StoreProvider = storeProvider;
            MissingProjectScanner = missingProjectScanner;
            RestoreOrchestrator = restoreOrchestrator;
            GitOperationLock = gitOperationLock;
            RepoScanService = repoScanService;
            ProjectRegistrationService = projectRegistrationService;
            UpdateService = updateService;
            UpdateOptions = updateOptions ?? UpdateOptions.Defaults();
            RelocateService = relocateService;
            ProfileManager = profileManager;
            ProfileState = profileState;
        }

        public IReadOnlyList<RepoStore> CurrentStores { get; }
        public string LibraryPath { get; }
        public IProjectCreationService CreationService { get; }
        public ControlTowerService ControlTowerService { get; }
        public ILibraryProvider LibraryProvider { get; }
        public IAssetTransferService AssetTransferService { get; }
        public IAssetCaptureService AssetCaptureService { get; }
        public IAuditLogger AuditLogger { get; }
        public IPortfolioProvider PortfolioProvider { get; }
        public IProjectProvider ProjectProvider { get; }
        public IStoreProvider StoreProvider { get; }
        public IMissingProjectScanner MissingProjectScanner { get; }
        public IRestoreOrchestrator RestoreOrchestrator { get; }
        public ILongRunningGitOperationLock GitOperationLock { get; }
        public IRepoScanService RepoScanService { get; }
        public IProjectRegistrationService ProjectRegistrationService { get; }
        public IUpdateService UpdateService { get; }
        public UpdateOptions UpdateOptions { get; }
        public IRelocateProjectService RelocateService { get; }
        public WorkspaceProfileManager ProfileManager { get; }
        public WorkspaceProfileState ProfileState { get; }
    }
}
