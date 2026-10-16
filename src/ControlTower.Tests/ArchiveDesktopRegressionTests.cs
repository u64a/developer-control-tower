using System;
using System.IO;

namespace ControlTower.Tests;

public class ArchiveDesktopRegressionTests
{
    [Fact]
    public void SettingsWindow_ArchiveSection_IsBetweenLaunchEnvironmentAndUpdates()
    {
        var xaml = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "SettingsWindow.xaml"));

        var launch = xaml.IndexOf("<!-- Launch environment -->", StringComparison.Ordinal);
        var archive = xaml.IndexOf("<!-- Archive storage -->", StringComparison.Ordinal);
        var updates = xaml.IndexOf("<!-- Updates -->", StringComparison.Ordinal);

        Assert.True(launch >= 0, "Launch environment section marker not found.");
        Assert.True(archive >= 0, "Archive storage section marker not found.");
        Assert.True(updates >= 0, "Updates section marker not found.");
        Assert.True(launch < archive && archive < updates, "Archive section must stay between Launch environment and Updates.");
        Assert.Contains("ARCHIVE SAFETY NET", xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_RowContextMenu_RemovesPerProjectArchivePathAction()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "MainWindow.xaml.cs"));

        Assert.DoesNotContain("SetArchivePathDialog", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Set archive path", code, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Sync all archives now", code, StringComparison.Ordinal);
        Assert.Contains("Archive syncing is disabled until the archive storage setting is corrected.", code, StringComparison.Ordinal);
        Assert.Contains("_session?.ArchiveSyncService != null", code, StringComparison.Ordinal);
        Assert.Contains("!string.IsNullOrWhiteSpace(_session.ArchiveStorageRootPath)", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_SettingsSave_DoesNotAutoTriggerArchiveSync()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "MainWindow.xaml.cs"));

        var totalInvocations = CountOccurrences(code, "RunArchiveSyncAsync(");
        Assert.Equal(2, totalInvocations);

        var settingsClickStart = code.IndexOf("private async void SettingsClick", StringComparison.Ordinal);
        var addProjectStart = code.IndexOf("private async void AddProjectClick", StringComparison.Ordinal);
        Assert.True(settingsClickStart >= 0 && addProjectStart > settingsClickStart, "Could not isolate SettingsClick method.");

        var settingsClickBody = code.Substring(settingsClickStart, addProjectStart - settingsClickStart);
        Assert.DoesNotContain("RunArchiveSyncAsync(", settingsClickBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_Startup_TriggersOneShotGuardedArchiveSync_AfterAwaitedLoad_WithoutTimer()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "MainWindow.xaml.cs"));

        var loadedStart = code.IndexOf("private async void MainWindowLoaded", StringComparison.Ordinal);
        var profileButtonStart = code.IndexOf("private void ProfileButtonClick", StringComparison.Ordinal);
        Assert.True(loadedStart >= 0 && profileButtonStart > loadedStart, "Could not isolate MainWindowLoaded method.");

        var loadedBody = code.Substring(loadedStart, profileButtonStart - loadedStart);
        var loadCallIndex = loadedBody.IndexOf("await _viewModel.LoadAsync();", StringComparison.Ordinal);
        var startupSyncIndex = loadedBody.IndexOf("RunArchiveSyncAsync(", StringComparison.Ordinal);

        Assert.True(loadCallIndex >= 0, "MainWindowLoaded must await portfolio load before startup archive sync.");
        Assert.True(startupSyncIndex > loadCallIndex, "Startup archive sync must run only after awaited LoadAsync completes.");
        Assert.Equal(1, CountOccurrences(loadedBody, "RunArchiveSyncAsync("));
        Assert.Contains("_viewModel.RunArchiveSyncAsync()", loadedBody, StringComparison.Ordinal);

        Assert.DoesNotContain("DispatcherTimer", loadedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("PeriodicTimer", loadedBody, StringComparison.Ordinal);
        Assert.DoesNotContain("new Timer(", loadedBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ApplySession_WiresCanonicalArchiveGuardDependencies_BeforeStartupSyncIsPossible()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "MainWindow.xaml.cs"));

        Assert.Contains("_viewModel.ArchiveSyncService = session.ArchiveSyncService;", code, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ArchiveSyncSafetyService = session.ArchiveSyncSafetyService;", code, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ArchiveStateStore = session.ArchiveStateStore;", code, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ArchiveStorageRootPath = session.ArchiveStorageRootPath;", code, StringComparison.Ordinal);
        Assert.Contains("_viewModel.ArchiveWorkingSourceInventoryProvider =", code, StringComparison.Ordinal);
    }

    [Fact]
    public void MainViewModel_ArchiveStatus_RootMismatchShowsNeedsSync_NotNotConfigured()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "ViewModels",
            "MainViewModel.cs"));

        Assert.Contains("project.ArchiveStatus = IsCurrentArchiveRoot(project, state)", code, StringComparison.Ordinal);
        Assert.Contains(": \"Needs sync\";", code, StringComparison.Ordinal);
        Assert.Contains("Path.Combine(ArchiveStorageRootPath, projectId)", code, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveStatus = \"Not configured\"", code, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CompositionRoot_ArchiveSyncService_RequiresValidArchiveSettings()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "Bootstrap",
            "CompositionRoot.cs"));

        Assert.Contains("if (settings.ArchiveStorageRootIsValid &&", code, StringComparison.Ordinal);
        Assert.Contains("!string.IsNullOrWhiteSpace(settings.ArchiveStorageRootPath)", code, StringComparison.Ordinal);
        Assert.Contains("ArchiveSyncService = archiveSyncService", code, StringComparison.Ordinal);
    }

    [Fact]
    public void CompositionRoot_ARCHIVE_SOURCE_FAILCLOSED_001_InventoryBuilderRejectsProjectLoadErrors()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "Bootstrap",
            "CompositionRoot.cs"));

        var methodStart = code.IndexOf(
            "private static ArchiveWorkingSourceInventory BuildArchiveWorkingSourceInventory",
            StringComparison.Ordinal);
        var methodEnd = code.IndexOf(
            "private string ResolveLibraryPath",
            StringComparison.Ordinal);

        Assert.True(methodStart >= 0 && methodEnd > methodStart, "Could not isolate BuildArchiveWorkingSourceInventory.");

        var methodBody = code.Substring(methodStart, methodEnd - methodStart);
        Assert.Contains("projectLoad.Issues", methodBody, StringComparison.Ordinal);
        Assert.Contains("IssueSeverity.Error", methodBody, StringComparison.Ordinal);
        Assert.Contains("ArchiveWorkingSourceInventory.Failed(", methodBody, StringComparison.Ordinal);
    }

    [Fact]
    public void MainViewModel_ARCHIVE_SOURCE_FRESHNESS_001_RefreshesInventoryFromProviderBeforeBatchSafetyPreflight()
    {
        var mainViewModelCode = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "ViewModels",
            "MainViewModel.cs"));

        var methodStart = mainViewModelCode.IndexOf(
            "public async Task RunArchiveSyncAsync()",
            StringComparison.Ordinal);
        var methodEnd = mainViewModelCode.IndexOf(
            "private void ApplySeededProject",
            StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart, "Could not isolate RunArchiveSyncAsync.");

        var methodBody = mainViewModelCode.Substring(methodStart, methodEnd - methodStart);
        Assert.Contains("RefreshArchiveWorkingSourceInventory()", methodBody, StringComparison.Ordinal);
        Assert.Contains("new ArchiveSyncSafetyRequest(", methodBody, StringComparison.Ordinal);

        var providerIndex = methodBody.IndexOf("RefreshArchiveWorkingSourceInventory()", StringComparison.Ordinal);
        var safetyIndex = methodBody.IndexOf("new ArchiveSyncSafetyRequest(", StringComparison.Ordinal);
        Assert.True(
            providerIndex >= 0 && providerIndex < safetyIndex,
            "RunArchiveSyncAsync must refresh inventory before the batch safety preflight.");

        var compositionRootCode = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "Bootstrap",
            "CompositionRoot.cs"));
        Assert.Contains("ArchiveWorkingSourceInventoryProvider", compositionRootCode, StringComparison.Ordinal);
        Assert.Contains("BuildArchiveWorkingSourceInventory(", compositionRootCode, StringComparison.Ordinal);
    }

    [Fact]
    public void MainWindow_ArchiveStatusVisibility_ShowsBlockedIdleTextWithoutShowingActiveProgress()
    {
        var xaml = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "MainWindow.xaml"));

        var statusToken = "Text=\"{Binding ArchiveSyncStatusText}\"";
        var statusIndex = xaml.IndexOf(statusToken, StringComparison.Ordinal);
        Assert.True(statusIndex >= 0, "Archive sync status text binding not found.");
        Assert.Contains("ToolTip=\"{Binding ArchiveSyncStatusText}\"", xaml, StringComparison.Ordinal);

        var statusTextBlockStart = xaml.LastIndexOf("<TextBlock", statusIndex, StringComparison.Ordinal);
        var statusTextBlockEnd = xaml.IndexOf(">", statusTextBlockStart, StringComparison.Ordinal);
        Assert.True(statusTextBlockStart >= 0 && statusTextBlockEnd > statusTextBlockStart, "Could not isolate archive status TextBlock.");
        var statusTextBlockTag = xaml.Substring(statusTextBlockStart, statusTextBlockEnd - statusTextBlockStart + 1);

        var statusCarrierTag = statusTextBlockTag;
        if (!statusCarrierTag.Contains("Visibility=\"{Binding", StringComparison.Ordinal))
        {
            var statusPanelStart = xaml.LastIndexOf("<StackPanel", statusIndex, StringComparison.Ordinal);
            var statusPanelEnd = xaml.IndexOf(">", statusPanelStart, StringComparison.Ordinal);
            Assert.True(statusPanelStart >= 0 && statusPanelEnd > statusPanelStart, "Could not isolate archive status host container.");
            statusCarrierTag = xaml.Substring(statusPanelStart, statusPanelEnd - statusPanelStart + 1);
        }

        Assert.Contains("Visibility=\"{Binding", statusCarrierTag, StringComparison.Ordinal);
        Assert.DoesNotContain("ArchiveSyncActive", statusCarrierTag, StringComparison.Ordinal);

        var progressToken = "Value=\"{Binding ArchiveSyncProgressPercent, Mode=OneWay}\"";
        var progressIndex = xaml.IndexOf(progressToken, StringComparison.Ordinal);
        Assert.True(progressIndex >= 0, "Archive sync progress bar binding not found.");

        var progressTextBlockStart = xaml.LastIndexOf("<ProgressBar", progressIndex, StringComparison.Ordinal);
        var progressTextBlockEnd = xaml.IndexOf(">", progressTextBlockStart, StringComparison.Ordinal);
        Assert.True(progressTextBlockStart >= 0 && progressTextBlockEnd > progressTextBlockStart, "Could not isolate archive progress bar.");
        var progressBarTag = xaml.Substring(progressTextBlockStart, progressTextBlockEnd - progressTextBlockStart + 1);

        var progressCarrierTag = progressBarTag;
        if (!progressCarrierTag.Contains("ArchiveSyncActive", StringComparison.Ordinal))
        {
            var progressPanelStart = xaml.LastIndexOf("<StackPanel", progressIndex, StringComparison.Ordinal);
            var progressPanelEnd = xaml.IndexOf(">", progressPanelStart, StringComparison.Ordinal);
            Assert.True(progressPanelStart >= 0 && progressPanelEnd > progressPanelStart, "Could not isolate archive progress host container.");
            progressCarrierTag = xaml.Substring(progressPanelStart, progressPanelEnd - progressPanelStart + 1);
        }

        Assert.Contains("ArchiveSyncActive", progressCarrierTag, StringComparison.Ordinal);
    }

    [Fact]
    public void MainViewModel_BatchSafetyPreflight_HappensBeforeSyncLoop()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "ViewModels",
            "MainViewModel.cs"));

        var methodStart = code.IndexOf("public async Task RunArchiveSyncAsync()", StringComparison.Ordinal);
        var methodEnd = code.IndexOf("private ArchiveWorkingSourceInventory RefreshArchiveWorkingSourceInventory()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart, "Could not isolate RunArchiveSyncAsync.");

        var methodBody = code.Substring(methodStart, methodEnd - methodStart);
        var safetyIndex = methodBody.IndexOf("ValidateSafety(safetyRequest)", StringComparison.Ordinal);
        var loopIndex = methodBody.IndexOf("for (var i = 0; i < targets.Count; i++)", StringComparison.Ordinal);
        var syncIndex = methodBody.IndexOf("ArchiveSyncService.SyncAsync(", StringComparison.Ordinal);

        Assert.True(safetyIndex >= 0, "Batch safety preflight call not found.");
        Assert.True(loopIndex >= 0, "Batch sync loop not found.");
        Assert.True(syncIndex >= 0, "Per-project sync call not found.");
        Assert.True(
            safetyIndex < loopIndex && loopIndex < syncIndex,
            "Batch safety preflight must execute before any per-project sync call.");
    }

    [Fact]
    public void MainViewModel_BlockedSafetyStatus_IsSetBeforeActivationAndEarlyReturn()
    {
        var code = File.ReadAllText(Path.Combine(
            GetRepositoryRoot(),
            "src",
            "ControlTower.Desktop",
            "ViewModels",
            "MainViewModel.cs"));

        var methodStart = code.IndexOf("public async Task RunArchiveSyncAsync()", StringComparison.Ordinal);
        var methodEnd = code.IndexOf("private ArchiveWorkingSourceInventory RefreshArchiveWorkingSourceInventory()", StringComparison.Ordinal);
        Assert.True(methodStart >= 0 && methodEnd > methodStart, "Could not isolate RunArchiveSyncAsync.");

        var methodBody = code.Substring(methodStart, methodEnd - methodStart);
        var blockedStatusIndex = methodBody.IndexOf("ArchiveSyncStatusText = safety == null", StringComparison.Ordinal);
        var earlyReturnIndex = methodBody.IndexOf("return;", blockedStatusIndex, StringComparison.Ordinal);
        var activationIndex = methodBody.IndexOf("ArchiveSyncActive = true;", StringComparison.Ordinal);

        Assert.True(blockedStatusIndex >= 0, "Blocked status assignment not found.");
        Assert.True(earlyReturnIndex > blockedStatusIndex, "Blocked path should return immediately after setting status.");
        Assert.True(activationIndex > earlyReturnIndex, "Blocked path must not activate archive sync UI state.");
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        var index = 0;
        while (true)
        {
            index = text.IndexOf(token, index, StringComparison.Ordinal);
            if (index < 0)
            {
                return count;
            }

            count++;
            index += token.Length;
        }
    }

    private static string GetRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DeveloperControlTower.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test base directory: " + AppContext.BaseDirectory);
    }
}
