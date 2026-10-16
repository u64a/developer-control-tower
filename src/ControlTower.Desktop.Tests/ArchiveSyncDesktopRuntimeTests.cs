using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Threading;
using ControlTower.Core.Contracts;
using ControlTower.Core.Models;
using ControlTower.Core.UseCases;
using ControlTower.Desktop.ViewModels;
using ControlTower.Infrastructure.Archive;

namespace ControlTower.Desktop.Tests;

public sealed class ArchiveSyncDesktopRuntimeTests
{
    [Theory]
    [InlineData("equal")]
    [InlineData("root-contains-source")]
    [InlineData("root-inside-source")]
    public async Task RunArchiveSyncAsync_ConflictingRoot_BlocksRowsWithoutSyncAndSurfacesModalSignal(string conflictShape)
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "ct-desktop-archive-blocked-" + Guid.NewGuid().ToString("N"));

        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");

        switch (conflictShape)
        {
            case "equal":
                sourceRoot = archiveRoot;
                break;
            case "root-contains-source":
                sourceRoot = Path.Combine(archiveRoot, "working-source");
                break;
            case "root-inside-source":
                archiveRoot = Path.Combine(sourceRoot, "archive-root");
                break;
            default:
                throw new InvalidOperationException("Unknown conflict shape: " + conflictShape);
        }

        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(archiveRoot);

        var sourceSentinel = Path.Combine(sourceRoot, "source-sentinel.txt");
        File.WriteAllText(sourceSentinel, "do-not-touch");

        try
        {
            var stateStore = new SpyArchiveStateStore();
            var syncService = new SpyArchiveSyncService();
            var inventory = ArchiveWorkingSourceInventory.Complete(new[]
            {
                new ArchiveWorkingSource("registered-working-source", sourceRoot)
            });

            var projects = new[]
            {
                CreateArchiveProject("archive-main", "https://github.com/org/main.git", "Needs sync"),
                CreateArchiveProject("archive-fork", "https://github.com/fork-owner/fork-repo.git", "Needs sync"),
                new ProjectOverview
                {
                    Id = "non-archive",
                    DisplayName = "Non archive",
                    Group = "Ungrouped",
                    RemoteUrl = "https://github.com/org/non-archive.git",
                    ArchiveStatus = string.Empty
                }
            };

            var viewModel = CreateViewModel(
                projects,
                syncService,
                CreateRealSafetyService(),
                stateStore,
                archiveRoot,
                inventory);
            ArchiveSyncOutcome? completedOutcome = null;
            var completionCount = 0;
            viewModel.ArchiveSyncCompleted += (_, outcome) =>
            {
                completionCount++;
                completedOutcome = outcome;
            };

            using var recorder = new PropertyChangeRecorder(viewModel);

            await viewModel.RunArchiveSyncAsync();

            Assert.Empty(syncService.Requests);
            Assert.Equal(0, stateStore.LoadCalls);
            Assert.Equal(0, stateStore.SaveCalls);
            Assert.False(viewModel.ArchiveSyncActive);
            Assert.Equal(0, viewModel.ArchiveSyncProgressPercent);
            Assert.False(recorder.HasBooleanValue(nameof(MainViewModel.ArchiveSyncActive), true));
            Assert.True(recorder.HasProperty(nameof(MainViewModel.LastArchiveSyncOutcome)));
            Assert.True(recorder.HasProperty(nameof(MainViewModel.ArchiveSyncBlockedMessage)));
            Assert.Contains("Archive sync blocked:", viewModel.ArchiveSyncStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("conflicts with registered working source", viewModel.ArchiveSyncStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Move source/update registered path or choose a separate archive root.", viewModel.ArchiveSyncStatusText, StringComparison.Ordinal);
            Assert.Equal(viewModel.ArchiveSyncStatusText, viewModel.ArchiveSyncBlockedMessage);
            Assert.NotNull(viewModel.LastArchiveSyncOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Blocked, viewModel.LastArchiveSyncOutcome!.Kind);
            Assert.Equal(1, completionCount);
            Assert.NotNull(completedOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Blocked, completedOutcome!.Kind);
            Assert.Equal(viewModel.ArchiveSyncStatusText, completedOutcome.Message);
            Assert.Equal(2, completedOutcome.BlockedProjectIds.Count);
            Assert.Contains("archive-main", completedOutcome.BlockedProjectIds);
            Assert.Contains("archive-fork", completedOutcome.BlockedProjectIds);

            var archiveRows = viewModel.Projects
                .Where(row => ArchiveGroup.Matches(row.Project.Group))
                .ToList();
            Assert.NotEmpty(archiveRows);
            Assert.All(archiveRows, row =>
            {
                Assert.Equal("Blocked", row.Project.ArchiveStatus);
                Assert.Equal(viewModel.ArchiveSyncStatusText, row.Project.ArchiveStatusDetail);
            });
            Assert.DoesNotContain(viewModel.Projects, row => !ArchiveGroup.Matches(row.Project.Group) && row.Project.ArchiveStatus == "Blocked");

            Assert.Equal("do-not-touch", File.ReadAllText(sourceSentinel));
            Assert.False(Directory.Exists(Path.Combine(Path.GetFullPath(archiveRoot), "archive-main")));
            Assert.False(Directory.Exists(Path.Combine(Path.GetFullPath(archiveRoot), "archive-fork")));
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task RunArchiveSyncAsync_RootChange_ClearsStaleBlockedState_AndSyncsForkAndNonFork()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "ct-desktop-archive-root-change-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(rootBase, "working-source");
        var safeArchiveRoot = Path.Combine(rootBase, "archive-root-safe");

        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(safeArchiveRoot);

        var sourceSentinel = Path.Combine(sourceRoot, "source-sentinel.txt");
        File.WriteAllText(sourceSentinel, "do-not-touch");

        try
        {
            var stateStore = new SpyArchiveStateStore();
            stateStore.States["archive-main"] = new ArchiveRepoState
            {
                ProjectId = "archive-main",
                ArchivePath = Path.Combine(Path.GetFullPath(safeArchiveRoot), "archive-main"),
                UpstreamStatus = ArchiveUpstreamStatus.Reachable
            };
            stateStore.States["archive-fork"] = new ArchiveRepoState
            {
                ProjectId = "archive-fork",
                ArchivePath = Path.Combine(Path.GetFullPath(safeArchiveRoot), "archive-fork"),
                UpstreamStatus = ArchiveUpstreamStatus.Unavailable,
                LastError = "repository not found"
            };

            var projects = new[]
            {
                CreateArchiveProject("archive-main", "https://github.com/org/main.git", "Needs sync"),
                CreateArchiveProject("archive-fork", "https://github.com/fork-owner/fork-repo.git", "Needs sync")
            };

            var syncService = new SpyArchiveSyncService
            {
                ResultFactory = request =>
                    request.ProjectId == "archive-fork"
                        ? new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.UpstreamUnavailable, "fork upstream gone", "sha-fork")
                        : new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Succeeded, "synced", "sha-main")
            };

            var inventory = ArchiveWorkingSourceInventory.Complete(new[]
            {
                new ArchiveWorkingSource("registered-working-source", sourceRoot)
            });

            var viewModel = CreateViewModel(
                projects,
                syncService,
                CreateRealSafetyService(),
                stateStore,
                sourceRoot,
                inventory);
            var outcomes = new List<ArchiveSyncOutcome>();
            viewModel.ArchiveSyncCompleted += (_, outcome) => outcomes.Add(outcome);

            await viewModel.RunArchiveSyncAsync();
            Assert.Contains("Archive sync blocked:", viewModel.ArchiveSyncStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(viewModel.LastArchiveSyncOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Blocked, viewModel.LastArchiveSyncOutcome!.Kind);
            var blockedOutcome = Assert.Single(outcomes);
            Assert.Equal(ArchiveSyncOutcomeKind.Blocked, blockedOutcome.Kind);
            Assert.NotNull(viewModel.ArchiveSyncBlockedMessage);
            Assert.All(
                viewModel.Projects.Where(row => ArchiveGroup.Matches(row.Project.Group)),
                row => Assert.Equal("Blocked", row.Project.ArchiveStatus));

            var rowSignals = new List<(string RowId, string PropertyName)>();
            foreach (var row in viewModel.Projects.Where(row => ArchiveGroup.Matches(row.Project.Group)))
            {
                row.PropertyChanged += (_, args) =>
                    rowSignals.Add((row.Id, args.PropertyName ?? string.Empty));
            }

            viewModel.ArchiveStorageRootPath = safeArchiveRoot;
            await viewModel.RunArchiveSyncAsync();

            Assert.Equal(2, syncService.Requests.Count);
            Assert.Contains(syncService.Requests, request => request.ProjectId == "archive-main");
            Assert.Contains(syncService.Requests, request => request.ProjectId == "archive-fork");
            Assert.All(syncService.Requests, request =>
            {
                Assert.Equal(Path.GetFullPath(safeArchiveRoot), Path.GetFullPath(request.ArchiveStorageRoot));
                Assert.Same(inventory, request.WorkingSourceInventory);
            });

            var mainRow = viewModel.Projects.Single(row => row.Id == "archive-main");
            var forkRow = viewModel.Projects.Single(row => row.Id == "archive-fork");
            Assert.Equal("Synced", mainRow.Project.ArchiveStatus);
            Assert.Equal("Upstream gone", forkRow.Project.ArchiveStatus);
            Assert.DoesNotContain("Archive sync blocked:", viewModel.ArchiveSyncStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(string.Empty, mainRow.Project.ArchiveStatusDetail);
            Assert.Contains("upstream remote could not be reached", forkRow.Project.ArchiveStatusDetail, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(rowSignals, signal => signal.RowId == "archive-main" && signal.PropertyName == "Project");
            Assert.Contains(rowSignals, signal => signal.RowId == "archive-fork" && signal.PropertyName == "Project");
            Assert.NotNull(viewModel.LastArchiveSyncOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Completed, viewModel.LastArchiveSyncOutcome!.Kind);
            Assert.Equal(2, viewModel.LastArchiveSyncOutcome.SucceededCount);
            Assert.Equal(0, viewModel.LastArchiveSyncOutcome.FailedCount);
            Assert.Empty(viewModel.LastArchiveSyncOutcome.Failures);
            Assert.Equal(2, outcomes.Count);
            Assert.Equal(ArchiveSyncOutcomeKind.Completed, outcomes[1].Kind);
            Assert.Equal(2, outcomes[1].SucceededCount);
            Assert.Equal(0, outcomes[1].FailedCount);
            Assert.Null(viewModel.ArchiveSyncBlockedMessage);

            Assert.Equal("do-not-touch", File.ReadAllText(sourceSentinel));
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task RunArchiveSyncAsync_FailedResultReason_RemainsVisibleToUi()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "ct-desktop-archive-failed-result-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");

        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(sourceRoot);

        try
        {
            const string failureReason = "simulated archive failure reason";
            var syncService = new SpyArchiveSyncService
            {
                ResultFactory = request =>
                    new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Failed, failureReason, null)
            };
            var viewModel = CreateViewModel(
                new[]
                {
                    CreateArchiveProject("archive-main", "https://github.com/org/main.git", "Needs sync")
                },
                syncService,
                new PassThroughSafetyService(),
                new SpyArchiveStateStore(),
                archiveRoot,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("registered-working-source", sourceRoot)
                }));
            ArchiveSyncOutcome? completedOutcome = null;
            viewModel.ArchiveSyncCompleted += (_, outcome) => completedOutcome = outcome;

            await viewModel.RunArchiveSyncAsync();

            Assert.Single(syncService.Requests);
            Assert.NotNull(completedOutcome);
            Assert.NotNull(viewModel.LastArchiveSyncOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Completed, completedOutcome!.Kind);
            Assert.Equal(ArchiveSyncOutcomeKind.Completed, viewModel.LastArchiveSyncOutcome!.Kind);
            Assert.Equal(0, completedOutcome.SucceededCount);
            Assert.Equal(1, completedOutcome.FailedCount);
            var failure = Assert.Single(completedOutcome.Failures);
            Assert.Equal("archive-main", failure.ProjectId);
            Assert.Equal(failureReason, failure.Reason);
            Assert.Contains(failureReason, completedOutcome.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(failureReason, viewModel.ArchiveSyncStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Null(viewModel.ArchiveSyncBlockedMessage);
            Assert.False(viewModel.ArchiveSyncActive);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task RunArchiveSyncAsync_ServiceException_RemainsVisibleToUi()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "ct-desktop-archive-failed-exception-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");

        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(sourceRoot);

        try
        {
            const string exceptionDetail = "simulated archive sync exception detail";
            var syncService = new SpyArchiveSyncService
            {
                ExceptionFactory = _ => new InvalidOperationException(exceptionDetail)
            };

            var viewModel = CreateViewModel(
                new[]
                {
                    CreateArchiveProject("archive-main", "https://github.com/org/main.git", "Needs sync")
                },
                syncService,
                new PassThroughSafetyService(),
                new SpyArchiveStateStore(),
                archiveRoot,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("registered-working-source", sourceRoot)
                }));
            ArchiveSyncOutcome? completedOutcome = null;
            viewModel.ArchiveSyncCompleted += (_, outcome) => completedOutcome = outcome;

            await viewModel.RunArchiveSyncAsync();

            Assert.Single(syncService.Requests);
            Assert.NotNull(completedOutcome);
            Assert.NotNull(viewModel.LastArchiveSyncOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Completed, completedOutcome!.Kind);
            Assert.Equal(ArchiveSyncOutcomeKind.Completed, viewModel.LastArchiveSyncOutcome!.Kind);
            Assert.Equal(0, completedOutcome.SucceededCount);
            Assert.Equal(1, completedOutcome.FailedCount);
            var failure = Assert.Single(completedOutcome.Failures);
            Assert.Equal("archive-main", failure.ProjectId);
            Assert.Equal(exceptionDetail, failure.Reason);
            Assert.Contains(exceptionDetail, completedOutcome.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(exceptionDetail, viewModel.ArchiveSyncStatusText, StringComparison.OrdinalIgnoreCase);
            Assert.Null(viewModel.ArchiveSyncBlockedMessage);
            Assert.False(viewModel.ArchiveSyncActive);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task RunArchiveSyncAsync_FailedResultWithoutStateSave_ShowsErrorRowAndActionableReason()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "ct-desktop-archive-failed-row-result-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");

        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(sourceRoot);

        try
        {
            const string failureReason = "cannot reach upstream for fetch";
            var stateStore = new SpyArchiveStateStore();
            var syncService = new SpyArchiveSyncService
            {
                ResultFactory = request =>
                    new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Failed, failureReason, null)
            };

            var viewModel = CreateViewModel(
                new[]
                {
                    CreateArchiveProject("archive-main", "https://github.com/org/main.git", "Needs sync")
                },
                syncService,
                new PassThroughSafetyService(),
                stateStore,
                archiveRoot,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("registered-working-source", sourceRoot)
                }));

            await viewModel.RunArchiveSyncAsync();

            Assert.Single(syncService.Requests);
            Assert.Equal(0, stateStore.SaveCalls);
            Assert.NotNull(viewModel.LastArchiveSyncOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Completed, viewModel.LastArchiveSyncOutcome!.Kind);
            Assert.Equal(1, viewModel.LastArchiveSyncOutcome.FailedCount);
            Assert.Contains(failureReason, viewModel.LastArchiveSyncOutcome.Message, StringComparison.OrdinalIgnoreCase);
            var failure = Assert.Single(viewModel.LastArchiveSyncOutcome.Failures);
            Assert.Equal(failureReason, failure.Reason);

            var archiveRow = Assert.Single(viewModel.Projects, row => row.Id == "archive-main");
            Assert.Equal(
                "Error",
                archiveRow.Project.ArchiveStatus);
            Assert.Contains(
                failureReason,
                archiveRow.Project.ArchiveStatusDetail,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task RunArchiveSyncAsync_ExceptionWithoutStateSave_ShowsErrorRowAndActionableReason()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "ct-desktop-archive-failed-row-exception-" + Guid.NewGuid().ToString("N"));
        var archiveRoot = Path.Combine(rootBase, "archive-root");
        var sourceRoot = Path.Combine(rootBase, "working-source");

        Directory.CreateDirectory(archiveRoot);
        Directory.CreateDirectory(sourceRoot);

        try
        {
            const string failureReason = "fatal simulated failure during sync";
            var stateStore = new SpyArchiveStateStore();
            var syncService = new SpyArchiveSyncService
            {
                ExceptionFactory = _ => new InvalidOperationException(failureReason)
            };

            var viewModel = CreateViewModel(
                new[]
                {
                    CreateArchiveProject("archive-main", "https://github.com/org/main.git", "Needs sync")
                },
                syncService,
                new PassThroughSafetyService(),
                stateStore,
                archiveRoot,
                ArchiveWorkingSourceInventory.Complete(new[]
                {
                    new ArchiveWorkingSource("registered-working-source", sourceRoot)
                }));

            await viewModel.RunArchiveSyncAsync();

            Assert.Single(syncService.Requests);
            Assert.Equal(0, stateStore.SaveCalls);
            Assert.NotNull(viewModel.LastArchiveSyncOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Completed, viewModel.LastArchiveSyncOutcome!.Kind);
            Assert.Equal(1, viewModel.LastArchiveSyncOutcome.FailedCount);
            Assert.Contains(failureReason, viewModel.LastArchiveSyncOutcome.Message, StringComparison.OrdinalIgnoreCase);
            var failure = Assert.Single(viewModel.LastArchiveSyncOutcome.Failures);
            Assert.Equal(failureReason, failure.Reason);

            var archiveRow = Assert.Single(viewModel.Projects, row => row.Id == "archive-main");
            Assert.Equal(
                "Error",
                archiveRow.Project.ArchiveStatus);
            Assert.Contains(
                failureReason,
                archiveRow.Project.ArchiveStatusDetail,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task LoadAsync_SameViewModelRootChange_ClearsBlockedRowAndBlockedSignals()
    {
        var rootBase = Path.Combine(
            Path.GetTempPath(),
            "ct-desktop-archive-reload-clear-" + Guid.NewGuid().ToString("N"));
        var sourceRoot = Path.Combine(rootBase, "working-source");
        var conflictingArchiveRoot = Path.Combine(sourceRoot, "archive-root");
        var safeArchiveRoot = Path.Combine(rootBase, "archive-root-safe");

        Directory.CreateDirectory(sourceRoot);
        Directory.CreateDirectory(conflictingArchiveRoot);
        Directory.CreateDirectory(safeArchiveRoot);

        try
        {
            var service = CreateControlTowerServiceForReload(
                "archive-main",
                sourceRoot,
                "https://github.com/org/main.git");
            var stateStore = new SpyArchiveStateStore();
            var viewModel = new MainViewModel(service, null, null, null)
            {
                ArchiveSyncService = new SpyArchiveSyncService
                {
                    ResultFactory = request =>
                        new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Succeeded, "ok", "sha-main")
                },
                ArchiveSyncSafetyService = CreateRealSafetyService(),
                ArchiveStateStore = stateStore,
                ArchiveStorageRootPath = conflictingArchiveRoot,
                ArchiveWorkingSourceInventoryProvider = () =>
                    ArchiveWorkingSourceInventory.Complete(new[]
                    {
                        new ArchiveWorkingSource("registered-working-source", sourceRoot)
                    })
            };

            await viewModel.LoadAsync();
            await viewModel.RunArchiveSyncAsync();

            var blockedRow = Assert.Single(viewModel.Projects, row => row.Id == "archive-main");
            Assert.Equal("Blocked", blockedRow.Project.ArchiveStatus);
            Assert.NotNull(viewModel.ArchiveSyncBlockedMessage);
            Assert.NotNull(viewModel.LastArchiveSyncOutcome);
            Assert.Equal(ArchiveSyncOutcomeKind.Blocked, viewModel.LastArchiveSyncOutcome!.Kind);

            stateStore.States["archive-main"] = new ArchiveRepoState
            {
                ProjectId = "archive-main",
                ArchivePath = Path.Combine(Path.GetFullPath(safeArchiveRoot), "archive-main"),
                UpstreamStatus = ArchiveUpstreamStatus.Reachable,
                LastSyncedUtc = DateTime.UtcNow
            };

            viewModel.ArchiveStorageRootPath = safeArchiveRoot;
            await viewModel.LoadAsync();

            var reloadedRow = Assert.Single(viewModel.Projects, row => row.Id == "archive-main");
            Assert.NotEqual("Blocked", reloadedRow.Project.ArchiveStatus);
            Assert.DoesNotContain(
                "Archive sync blocked:",
                reloadedRow.Project.ArchiveStatusDetail ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);

            Assert.True(
                string.IsNullOrWhiteSpace(viewModel.ArchiveSyncBlockedMessage),
                "Reload should clear stale blocked modal signal on the same MainViewModel instance.");
            Assert.True(
                viewModel.LastArchiveSyncOutcome == null || viewModel.LastArchiveSyncOutcome.Kind != ArchiveSyncOutcomeKind.Blocked,
                "Reload should not leave LastArchiveSyncOutcome stuck on Blocked after root correction.");
            Assert.DoesNotContain(
                "Archive sync blocked:",
                viewModel.ArchiveSyncStatusText ?? string.Empty,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            TryDeleteDirectory(rootBase);
        }
    }

    [Fact]
    public async Task ProjectRow_SameReferenceOverviewMutation_RaisesBindingVisibleChange()
    {
        await RunOnStaAsync(async () =>
        {
            var overview = CreateArchiveProject(
                "archive-main",
                "https://github.com/org/main.git",
                "Needs sync");
            var row = new ProjectRow(overview, false, _ => { });
            var rowSignals = new List<string>();
            row.PropertyChanged += (_, args) => rowSignals.Add(args.PropertyName ?? string.Empty);

            var archiveStatusText = new TextBlock { DataContext = row };
            archiveStatusText.SetBinding(TextBlock.TextProperty, new Binding("Project.ArchiveStatus"));
            var archiveStatusDetailText = new TextBlock { DataContext = row };
            archiveStatusDetailText.SetBinding(TextBlock.TextProperty, new Binding("Project.ArchiveStatusDetail"));
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Equal("Needs sync", archiveStatusText.Text);
            Assert.Equal(string.Empty, archiveStatusDetailText.Text);

            overview.ArchiveStatus = "Blocked";
            overview.ArchiveStatusDetail = "Archive sync blocked: root conflicts with source.";
            row.Project = overview;
            await Dispatcher.Yield(DispatcherPriority.Background);

            Assert.Contains("Project", rowSignals);
            Assert.Equal("Blocked", archiveStatusText.Text);
            Assert.Equal("Archive sync blocked: root conflicts with source.", archiveStatusDetailText.Text);
        });
    }

    private static MainViewModel CreateViewModel(
        IReadOnlyList<ProjectOverview> projects,
        IArchiveSyncService syncService,
        IArchiveSyncSafetyService safetyService,
        SpyArchiveStateStore stateStore,
        string archiveRoot,
        ArchiveWorkingSourceInventory inventory)
    {
        var viewModel = new MainViewModel(
            service: null!,
            shellLauncher: null,
            updateService: null,
            updateOptions: null)
        {
            ArchiveSyncService = syncService,
            ArchiveSyncSafetyService = safetyService,
            ArchiveStateStore = stateStore,
            ArchiveStorageRootPath = archiveRoot,
            ArchiveWorkingSourceInventoryProvider = () => inventory
        };

        SeedProjects(viewModel, projects);
        return viewModel;
    }

    private static ProjectOverview CreateArchiveProject(string id, string remoteUrl, string archiveStatus)
    {
        return new ProjectOverview
        {
            Id = id,
            DisplayName = id,
            Group = ArchiveGroup.Name,
            RemoteUrl = remoteUrl,
            ArchiveStatus = archiveStatus
        };
    }

    private static ControlTowerService CreateControlTowerServiceForReload(
        string projectId,
        string localPath,
        string remoteUrl)
    {
        var projectRoot = Path.Combine(localPath, projectId);
        var portfolio = new FakePortfolioProvider();
        portfolio.Index.Projects.Add(new ProjectRef
        {
            Id = projectId,
            Path = projectRoot
        });

        var definition = new ProjectDefinition
        {
            Id = projectId,
            DisplayName = projectId,
            ProjectRootPath = projectRoot,
            Group = ArchiveGroup.Name
        };
        definition.Locations.LocalPath = localPath;
        definition.Locations.RemoteUrl = remoteUrl;
        definition.Launch.GitHub = remoteUrl;

        var projectProvider = new FakeProjectProvider();
        projectProvider.Map[projectRoot] = new ProjectLoadResult
        {
            Project = definition
        };

        return new ControlTowerService(
            portfolio,
            projectProvider,
            new FakeProductMapProvider(),
            new FakePlanningBoardProvider(),
            new FakeRepoScanner(),
            new FakeSnapshotStore(),
            new FakeLaunchService(),
            new FakeRegistrationService());
    }

    private static void SeedProjects(MainViewModel viewModel, IReadOnlyList<ProjectOverview> projects)
    {
        var allProjectsField = typeof(MainViewModel).GetField(
            "_allProjects",
            BindingFlags.Instance | BindingFlags.NonPublic);
        if (allProjectsField?.GetValue(viewModel) is not IList<ProjectOverview> allProjects)
        {
            throw new InvalidOperationException("Could not access MainViewModel._allProjects.");
        }

        allProjects.Clear();
        foreach (var project in projects)
        {
            allProjects.Add(project);
        }

        viewModel.Projects.Clear();
        foreach (var project in projects)
        {
            viewModel.Projects.Add(new ProjectRow(project, false, _ => { }));
        }
    }

    private static IArchiveSyncSafetyService CreateRealSafetyService()
    {
        return new ArchiveSyncService(
            new NoOpGitProcessAdapter(),
            new NoOpCloneService(),
            new NoOpArchiveStateStore(),
            new NoOpArchiveReleaseClient());
    }

    private static Task RunOnStaAsync(Func<Task> body)
    {
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherSynchronizationContext(dispatcher));

            dispatcher.BeginInvoke(new Action(async () =>
            {
                try
                {
                    await body();
                    completion.SetResult(null);
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
                finally
                {
                    dispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                }
            }));

            Dispatcher.Run();
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return completion.Task;
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

    private sealed class FakePortfolioProvider : IPortfolioProvider
    {
        public PortfolioIndex Index { get; } = new();

        public PortfolioIndex LoadPortfolio() => Index;

        public void SavePortfolio(PortfolioIndex portfolio)
        {
            Index.Projects.Clear();
            foreach (var project in portfolio.Projects)
            {
                Index.Projects.Add(project);
            }
        }
    }

    private sealed class FakeProjectProvider : IProjectProvider
    {
        public Dictionary<string, ProjectLoadResult> Map { get; } = new(StringComparer.OrdinalIgnoreCase);

        public ProjectLoadResult LoadProject(string projectRootPath)
        {
            if (Map.TryGetValue(projectRootPath, out var result))
            {
                return result;
            }

            return new ProjectLoadResult
            {
                Project = new ProjectDefinition
                {
                    Id = "fallback",
                    DisplayName = "fallback",
                    ProjectRootPath = projectRootPath ?? string.Empty
                }
            };
        }
    }

    private sealed class FakeProductMapProvider : IProductMapProvider
    {
        public ProductMapLoadResult LoadProductMap(string projectRootPath, string sourceRef) => new();
    }

    private sealed class FakePlanningBoardProvider : IPlanningBoardProvider
    {
        public PlanningBoardLoadResult LoadPlanningBoard(string projectRootPath) => new();

        public PlanningBoardLoadResult ParseFromContent(string yamlContent, string sourceLabel) => new();
    }

    private sealed class FakeRepoScanner : IRepoScanner
    {
        public RepoSnapshot Scan(string repoPath) => new() { RepoPath = repoPath ?? string.Empty };
    }

    private sealed class FakeSnapshotStore : ISnapshotStore
    {
        public RepoSnapshot Load(string projectId) => null!;

        public void Save(string projectId, RepoSnapshot snapshot)
        {
        }
    }

    private sealed class FakeLaunchService : ILaunchService
    {
        public LaunchResult Launch(ProjectDefinition project, LaunchTargetKind targetKind) =>
            LaunchResult.Ok("ok");
    }

    private sealed class FakeRegistrationService : IProjectRegistrationService
    {
        public ProjectRegistrationResult RegisterProject(ProjectRegistrationRequest request) =>
            new() { Success = true, ProjectId = request.ProjectId ?? string.Empty };

        public ProjectRegistrationResult RemoveProject(string projectId) =>
            new() { Success = true, ProjectId = projectId ?? string.Empty };
    }

    private sealed class SpyArchiveSyncService : IArchiveSyncService
    {
        public List<ArchiveSyncRequest> Requests { get; } = new();
        public Func<ArchiveSyncRequest, Exception>? ExceptionFactory { get; set; }
        public Func<ArchiveSyncRequest, ArchiveSyncResult>? ResultFactory { get; set; }

        public Task<ArchiveSyncResult> SyncAsync(
            ArchiveSyncRequest request,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            Requests.Add(request);

            var exception = ExceptionFactory?.Invoke(request);
            if (exception != null)
            {
                throw exception;
            }

            return Task.FromResult(
                ResultFactory?.Invoke(request)
                ?? new ArchiveSyncResult(request.ProjectId, ArchiveSyncStatus.Succeeded, "ok", "sha"));
        }
    }

    private sealed class SpyArchiveStateStore : IArchiveStateStore
    {
        public Dictionary<string, ArchiveRepoState> States { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int LoadCalls { get; private set; }
        public int SaveCalls { get; private set; }

        public ArchiveRepoState Load(string projectId)
        {
            LoadCalls++;
            if (States.TryGetValue(projectId, out var state))
            {
                return state;
            }

            var created = new ArchiveRepoState { ProjectId = projectId ?? string.Empty };
            States[created.ProjectId] = created;
            return created;
        }

        public void Save(ArchiveRepoState state)
        {
            SaveCalls++;
            States[state.ProjectId] = state;
        }
    }

    private sealed class PassThroughSafetyService : IArchiveSyncSafetyService
    {
        public ArchiveSyncSafetyResult ValidateSafety(ArchiveSyncSafetyRequest request)
        {
            var root = Path.GetFullPath(request.ArchiveStorageRoot);
            return new ArchiveSyncSafetyResult(true, root, string.Empty);
        }
    }

    private sealed class NoOpGitProcessAdapter : IGitProcessAdapter
    {
        public Task<GitRunResult> RunAsync(
            IEnumerable<string> arguments,
            string workingDirectory,
            TimeSpan timeout,
            IProgress<string>? progress,
            CancellationToken ct)
        {
            return Task.FromResult(
                new GitRunResult(0, string.Empty, string.Empty, TimeSpan.Zero, TimedOut: false, Cancelled: false));
        }
    }

    private sealed class NoOpCloneService : IAsyncCloneService
    {
        public Task<CloneResult> CloneAsync(
            CloneRequest request,
            IProgress<CloneProgress>? progress,
            CancellationToken ct)
        {
            return Task.FromResult(CloneResult.Ok("main", "sha", "ok"));
        }
    }

    private sealed class NoOpArchiveStateStore : IArchiveStateStore
    {
        public ArchiveRepoState Load(string projectId) => new() { ProjectId = projectId ?? string.Empty };

        public void Save(ArchiveRepoState state)
        {
        }
    }

    private sealed class NoOpArchiveReleaseClient : IArchiveReleaseClient
    {
        public Task<ArchiveReleaseInfo?> GetLatestReleaseAsync(
            string remoteUrl,
            CancellationToken ct) => Task.FromResult<ArchiveReleaseInfo?>(null);

        public Task<ArchiveReleaseIntegrity> DownloadAndVerifyAsync(
            ArchiveReleaseAsset asset,
            string destinationPath,
            CancellationToken ct) => Task.FromResult(ArchiveReleaseIntegrity.NotCaptured);

        public Task<ArchiveUpstreamRepo?> ResolveUpstreamAsync(
            string remoteUrl,
            CancellationToken ct) => Task.FromResult<ArchiveUpstreamRepo?>(null);
    }

    private sealed class PropertyChangeRecorder : IDisposable
    {
        private readonly INotifyPropertyChanged _source;
        private readonly List<PropertyChangeEntry> _changes = new();

        public PropertyChangeRecorder(INotifyPropertyChanged source)
        {
            _source = source;
            _source.PropertyChanged += OnPropertyChanged;
        }

        public IEnumerable<PropertyChangeEntry> StringValuesContaining(string token)
        {
            return _changes.Where(change =>
                change.Value is string text &&
                text.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        public bool HasProperty(string propertyName)
        {
            return _changes.Any(change =>
                string.Equals(change.PropertyName, propertyName, StringComparison.Ordinal));
        }

        public bool HasBooleanValue(string propertyName, bool expected)
        {
            return _changes.Any(change =>
                string.Equals(change.PropertyName, propertyName, StringComparison.Ordinal) &&
                change.Value is bool value &&
                value == expected);
        }

        public void Dispose()
        {
            _source.PropertyChanged -= OnPropertyChanged;
        }

        private void OnPropertyChanged(object? sender, PropertyChangedEventArgs args)
        {
            if (sender == null || string.IsNullOrWhiteSpace(args.PropertyName))
            {
                return;
            }

            var property = sender.GetType().GetProperty(args.PropertyName, BindingFlags.Instance | BindingFlags.Public);
            object? value = null;
            try
            {
                value = property?.CanRead == true ? property.GetValue(sender) : null;
            }
            catch
            {
                value = null;
            }

            _changes.Add(new PropertyChangeEntry(args.PropertyName, value));
        }

        public sealed record PropertyChangeEntry(string PropertyName, object? Value);
    }
}
