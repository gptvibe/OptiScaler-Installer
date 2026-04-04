using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using OptiScalerInstaller.App.Infrastructure;
using OptiScalerInstaller.App.Services;
using OptiScalerInstaller.Core;
using System.Windows.Data;

namespace OptiScalerInstaller.App.ViewModels;

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogEntries = 400;
    private const string FilterAll = "all";
    private const string FilterInstallable = "installable";
    private const string FilterSelected = "selected";
    private const string FilterNeedsReview = "needs-review";

    private readonly IGameScannerService gameScannerService;
    private readonly IGpuDetectorService gpuDetector;
    private readonly IInstallationWorkflowService installationService;
    private readonly IUserInteractionService userInteractionService;
    private readonly RunLogger? runLogger;
    private readonly AppBuildInfoSnapshot buildInfo = AppBuildInfo.GetCurrent();
    private readonly ListCollectionView gamesView;
    private readonly object logsGate = new();

    private string gpuVendorText = "Detecting GPU...";
    private string statusText = "Ready";
    private string currentStepText = "Scan for supported games to begin.";
    private bool isBusy;
    private CancellationTokenSource? operationCts;
    private double progressValue;
    private double progressMaximum = 1;
    private bool isProgressVisible;
    private bool isProgressIndeterminate;
    private string progressText = string.Empty;
    private string searchText = string.Empty;
    private string emptyGamesMessage = "No supported games found yet.";
    private DetectedGameItemViewModel? selectedGame;
    private string bannerTitle = string.Empty;
    private string bannerMessage = string.Empty;
    private LogSeverity bannerSeverity = LogSeverity.Info;
    private string latestPreparedReleaseTag = "Latest stable (resolved during install)";
    private BackupSnapshotItemViewModel? selectedSnapshot;
    private GameFilterOptionViewModel selectedGameFilter;
    private int visibleGameCount;
    private int deferredVisibleGamesRefreshDepth;
    private bool pendingVisibleGamesRefresh;

    public MainViewModel(
        IGameScannerService gameScannerService,
        IGpuDetectorService gpuDetector,
        IInstallationWorkflowService installationService,
        IUserInteractionService userInteractionService,
        RunLogger? runLogger = null)
    {
        this.gameScannerService = gameScannerService;
        this.gpuDetector = gpuDetector;
        this.installationService = installationService;
        this.userInteractionService = userInteractionService;
        this.runLogger = runLogger;

        GameFilters = new ReadOnlyCollection<GameFilterOptionViewModel>(
        [
            new() { Key = FilterAll, Label = "All games" },
            new() { Key = FilterInstallable, Label = "Installable" },
            new() { Key = FilterSelected, Label = "Selected" },
            new() { Key = FilterNeedsReview, Label = "Needs review" },
        ]);
        selectedGameFilter = GameFilters[0];
        gamesView = new ListCollectionView(Games);
        gamesView.Filter = FilterVisibleGame;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        BrowseFolderCommand = new AsyncRelayCommand(BrowseFolderAsync, () => !IsBusy);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, CanInstallSelected);
        InstallAllCommand = new AsyncRelayCommand(InstallAllAsync, CanInstallAny);
        UndoCommand = new AsyncRelayCommand<InstallRecordItemViewModel>(UndoAsync, record => !IsBusy && record is not null);
        CancelCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy && operationCts is not null);
        SelectAllVisibleCommand = new RelayCommand(SelectAllVisibleGames, () => !IsBusy && VisibleGames.Any(game => game.CanSelect && !game.IsSelected));
        SelectNoneVisibleCommand = new RelayCommand(SelectNoneVisibleGames, () => !IsBusy && VisibleGames.Any(game => game.IsSelected));
        OpenSelectedGameFolderCommand = new RelayCommand(OpenSelectedGameFolder, () => SelectedGame is not null);
        RestoreSnapshotCommand = new AsyncRelayCommand<BackupSnapshotItemViewModel>(
            RestoreSnapshotAsync,
            snapshot => !IsBusy && snapshot is not null && snapshot.CanRestore);
        OpenSnapshotFolderCommand = new AsyncRelayCommand<BackupSnapshotItemViewModel>(
            OpenSnapshotFolderAsync,
            snapshot => snapshot is not null);
        DeleteSnapshotCommand = new AsyncRelayCommand<BackupSnapshotItemViewModel>(
            DeleteSnapshotAsync,
            snapshot => !IsBusy && snapshot is not null && snapshot.CanDelete);
        CopyDiagnosticsCommand = new AsyncRelayCommand(CopyDiagnosticsAsync);
        ExportLogsCommand = new AsyncRelayCommand(ExportLogsAsync);
        DismissBannerCommand = new RelayCommand(ClearBanner, () => HasBanner);
    }

    public ObservableCollection<DetectedGameItemViewModel> Games { get; } = [];

    public ICollectionView GamesView => gamesView;

    public IReadOnlyList<DetectedGameItemViewModel> VisibleGames => GetVisibleGames().ToList();

    public ObservableCollection<InstallRecordItemViewModel> InstalledGames { get; } = [];

    public ObservableCollection<BackupSnapshotItemViewModel> Snapshots { get; } = [];

    public ObservableCollection<LogEntryViewModel> Logs { get; } = [];

    public ReadOnlyCollection<GameFilterOptionViewModel> GameFilters { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand BrowseFolderCommand { get; }

    public AsyncRelayCommand InstallSelectedCommand { get; }

    public AsyncRelayCommand InstallAllCommand { get; }

    public AsyncRelayCommand<InstallRecordItemViewModel> UndoCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand SelectAllVisibleCommand { get; }

    public RelayCommand SelectNoneVisibleCommand { get; }

    public RelayCommand OpenSelectedGameFolderCommand { get; }

    public AsyncRelayCommand<BackupSnapshotItemViewModel> RestoreSnapshotCommand { get; }

    public AsyncRelayCommand<BackupSnapshotItemViewModel> OpenSnapshotFolderCommand { get; }

    public AsyncRelayCommand<BackupSnapshotItemViewModel> DeleteSnapshotCommand { get; }

    public AsyncRelayCommand CopyDiagnosticsCommand { get; }

    public AsyncRelayCommand ExportLogsCommand { get; }

    public RelayCommand DismissBannerCommand { get; }

    public string GpuVendorText
    {
        get => gpuVendorText;
        private set => SetProperty(ref gpuVendorText, value);
    }

    public string StatusText
    {
        get => statusText;
        private set => SetProperty(ref statusText, value);
    }

    public string CurrentStepText
    {
        get => currentStepText;
        private set => SetProperty(ref currentStepText, value);
    }

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(HasNoVisibleGames));
                NotifyCommandStates();
            }
        }
    }

    public double ProgressValue
    {
        get => progressValue;
        private set => SetProperty(ref progressValue, value);
    }

    public double ProgressMaximum
    {
        get => progressMaximum;
        private set => SetProperty(ref progressMaximum, value);
    }

    public bool IsProgressVisible
    {
        get => isProgressVisible;
        private set => SetProperty(ref isProgressVisible, value);
    }

    public bool IsProgressIndeterminate
    {
        get => isProgressIndeterminate;
        private set => SetProperty(ref isProgressIndeterminate, value);
    }

    public string ProgressText
    {
        get => progressText;
        private set => SetProperty(ref progressText, value);
    }

    public string SearchText
    {
        get => searchText;
        set
        {
            if (SetProperty(ref searchText, value))
            {
                RefreshVisibleGames();
            }
        }
    }

    public string EmptyGamesMessage
    {
        get => emptyGamesMessage;
        private set => SetProperty(ref emptyGamesMessage, value);
    }

    public GameFilterOptionViewModel SelectedGameFilter
    {
        get => selectedGameFilter;
        set
        {
            if (value is null)
            {
                return;
            }

            if (SetProperty(ref selectedGameFilter, value))
            {
                RefreshVisibleGames();
            }
        }
    }

    public DetectedGameItemViewModel? SelectedGame
    {
        get => selectedGame;
        set
        {
            if (SetProperty(ref selectedGame, value))
            {
                OnPropertyChanged(nameof(HasSelectedGame));
                OnPropertyChanged(nameof(SelectedGameReleaseTagText));
                NotifyCommandStates();
            }
        }
    }

    public BackupSnapshotItemViewModel? SelectedSnapshot
    {
        get => selectedSnapshot;
        set => SetProperty(ref selectedSnapshot, value);
    }

    public bool HasSelectedGame => SelectedGame is not null;

    public string SelectedGameReleaseTagText => SelectedGame is null
        ? string.Empty
        : SelectedGame.HasInstalledRecord || SelectedGame.HasSnapshot
            ? SelectedGame.ReleaseTagText
            : latestPreparedReleaseTag;

    public bool HasNoVisibleGames => VisibleGameCount == 0 && !IsBusy;

    public int VisibleGameCount
    {
        get => visibleGameCount;
        private set => SetProperty(ref visibleGameCount, value);
    }

    public bool HasInstalledGames => InstalledGames.Any();

    public bool HasSnapshots => Snapshots.Any();

    public string VisibleGamesSummaryText
        => $"{VisibleGameCount} shown · {Games.Count(game => game.IsSelected)} selected · {Games.Count} total";

    public bool HasBanner => !string.IsNullOrWhiteSpace(BannerMessage);

    public string BannerTitle
    {
        get => bannerTitle;
        private set => SetProperty(ref bannerTitle, value);
    }

    public string BannerMessage
    {
        get => bannerMessage;
        private set => SetProperty(ref bannerMessage, value);
    }

    public System.Windows.Media.Brush BannerAccentBrush => bannerSeverity switch
    {
        LogSeverity.Success => System.Windows.Media.Brushes.MediumSpringGreen,
        LogSeverity.Warning => System.Windows.Media.Brushes.Gold,
        LogSeverity.Error => System.Windows.Media.Brushes.OrangeRed,
        _ => System.Windows.Media.Brushes.DeepSkyBlue,
    };

    public System.Windows.Media.Brush BannerBackgroundBrush => bannerSeverity switch
    {
        LogSeverity.Success => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(18, 52, 35)),
        LogSeverity.Warning => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(61, 47, 18)),
        LogSeverity.Error => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(62, 24, 24)),
        _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(19, 35, 49)),
    };

    public async Task InitializeAsync()
    {
        UpdateGpuVendorText();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        BeginOperation("Scanning game libraries...", "Searching Steam, Epic, GOG, and Ubisoft installs...", isIndeterminate: true);
        AddLog(LogSeverity.Info, "Starting auto-detection.");

        try
        {
            UpdateGpuVendorText();

            var detectedGames = await gameScannerService.ScanGamesAsync(operationCts!.Token);
            ReplaceGames(detectedGames);
            await ReloadInstalledGamesAsync();
            await ReloadSnapshotsAsync();
            SynchronizeGameInstallState();
            RefreshVisibleGames();

            StatusText = detectedGames.Count == 0
                ? "No supported games found."
                : $"Found {detectedGames.Count} supported game(s).";
            CurrentStepText = "Scan complete.";
            AddLog(detectedGames.Count == 0 ? LogSeverity.Warning : LogSeverity.Success, StatusText);
            UpdateRecoveryBanner();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Scan cancelled.";
            CurrentStepText = "Scanning was cancelled.";
            AddLog(LogSeverity.Warning, "Scan cancelled.");
            ShowBanner("Scan cancelled", "The scan stopped before it finished. You can refresh again whenever you're ready.", LogSeverity.Warning);
        }
        catch (Exception exception)
        {
            StatusText = "Scan failed.";
            CurrentStepText = "Scanning failed.";
            AddLog(LogSeverity.Error, exception.Message);
            ShowBanner("Scan failed", exception.Message, LogSeverity.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task BrowseFolderAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var selectedPath = userInteractionService.PickFolder();
        if (string.IsNullOrWhiteSpace(selectedPath))
        {
            return;
        }

        BeginOperation("Inspecting manual folder...", $"Checking {selectedPath}", isIndeterminate: true);
        AddLog(LogSeverity.Info, $"Inspecting {selectedPath}");

        try
        {
            var detectedGame = await gameScannerService.InspectManualFolderAsync(selectedPath, operationCts!.Token);
            var item = AddOrReplaceGame(new DetectedGameItemViewModel(detectedGame));
            SelectedGame = item;

            StatusText = $"Ready to review {item.DisplayName}.";
            CurrentStepText = "Manual folder added.";

            if (detectedGame.SupportStatus == SupportStatus.Blocked)
            {
                AddLog(LogSeverity.Warning, $"{detectedGame.DisplayName} is blocked from auto-install.");
                ShowBanner(
                    "Blocked game detected",
                    $"{detectedGame.DisplayName} was added for reference, but it stays out of the install flow because the catalog marks it unsafe.",
                    LogSeverity.Warning);
                return;
            }

            if (detectedGame.SupportStatus == SupportStatus.Unsupported)
            {
                item.IsSelected = true;
                AddLog(LogSeverity.Warning, $"{detectedGame.DisplayName} requires a manual override.");
                ShowBanner(
                    "Manual override required",
                    $"{detectedGame.DisplayName} is outside the supported catalog. Review the details panel and enable manual override there if you want to try it.",
                    LogSeverity.Warning);
                return;
            }

            ShowBanner(
                "Manual folder added",
                $"{item.DisplayName} is now in the detected games list and ready for the normal install flow.",
                LogSeverity.Success);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Manual folder check cancelled.";
            CurrentStepText = "Manual folder inspection was cancelled.";
            AddLog(LogSeverity.Warning, "Manual folder inspection cancelled.");
        }
        catch (Exception exception)
        {
            StatusText = "Manual folder inspection failed.";
            CurrentStepText = "Manual folder inspection failed.";
            AddLog(LogSeverity.Error, exception.Message);
            ShowBanner("Manual folder inspection failed", exception.Message, LogSeverity.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task InstallSelectedAsync()
    {
        var selectedGames = Games.Where(game => game.IsSelected && game.CanInstall).ToList();
        await InstallGamesAsync(selectedGames);
    }

    private async Task InstallAllAsync()
    {
        var installableGames = Games.Where(game => game.CanInstall).ToList();
        RunWithDeferredVisibleGamesRefresh(() =>
        {
            foreach (var game in installableGames)
            {
                game.IsSelected = true;
            }
        });

        await InstallGamesAsync(installableGames);
    }

    private async Task InstallGamesAsync(IReadOnlyList<DetectedGameItemViewModel> gamesToInstall)
    {
        if (IsBusy)
        {
            return;
        }

        if (gamesToInstall.Count == 0)
        {
            ShowBanner("Nothing selected", "Choose at least one installable game before starting an install run.", LogSeverity.Warning);
            return;
        }

        BeginOperation("Installing selected games...", "Preparing the install run...", isIndeterminate: false, maximum: gamesToInstall.Count);
        var progress = CreateProgress();
        var gpuVendor = UpdateGpuVendorText();
        PreparedReleaseAsset? preparedRelease = null;
        var successCount = 0;
        var failureCount = 0;

        try
        {
            if (gamesToInstall.Count > 1)
            {
                SetOperationStep($"Preparing the latest stable release for {gamesToInstall.Count} games...");
                AddLog(LogSeverity.Info, $"Preparing OptiScaler once for {gamesToInstall.Count} selected games.");
                preparedRelease = await installationService.PrepareLatestStableReleaseAsync(progress, operationCts!.Token);
                latestPreparedReleaseTag = preparedRelease.Release.TagName;
                OnPropertyChanged(nameof(SelectedGameReleaseTagText));
            }

            for (var index = 0; index < gamesToInstall.Count; index++)
            {
                var game = gamesToInstall[index];
                operationCts!.Token.ThrowIfCancellationRequested();

                SetOperationStep($"Installing {game.DisplayName} ({index + 1}/{gamesToInstall.Count})...");

                var request = new InstallationRequest
                {
                    GpuVendor = gpuVendor,
                    ForceUnsupportedInstall = game.ForceUnsupportedInstall,
                };

                var outcome = await installationService.InstallAsync(
                    game.Model,
                    request,
                    preparedRelease,
                    progress,
                    operationCts.Token);

                AddLog(outcome.Success ? LogSeverity.Success : LogSeverity.Error, outcome.Message);
                if (outcome.Success && outcome.Record is not null)
                {
                    successCount++;
                }
                else
                {
                    failureCount++;
                }

                UpdateProgress(index + 1, gamesToInstall.Count);
            }

            await ReloadInstalledGamesAsync();
            await ReloadSnapshotsAsync();
            SynchronizeGameInstallState();

            StatusText = failureCount == 0
                ? "Install run complete."
                : successCount == 0
                    ? "Install run failed."
                    : "Install run finished with warnings.";
            CurrentStepText = failureCount == 0
                ? $"Installed {successCount} game(s)."
                : $"Installed {successCount} game(s), {failureCount} failed.";

            ShowBanner(
                failureCount == 0 ? "Install complete" : "Install run finished",
                failureCount == 0
                    ? $"Installed {successCount} game(s) successfully."
                    : $"Installed {successCount} game(s); {failureCount} game(s) still need attention. The log panel has the details.",
                failureCount == 0 ? LogSeverity.Success : LogSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Install cancelled.";
            CurrentStepText = "Install run cancelled.";
            AddLog(LogSeverity.Warning, "Install cancelled.");
            ShowBanner("Install cancelled", "The installer stopped before finishing the current run.", LogSeverity.Warning);
        }
        catch (Exception exception)
        {
            StatusText = "Install failed.";
            CurrentStepText = "Install run failed.";
            AddLog(LogSeverity.Error, exception.Message);
            ShowBanner("Install failed", exception.Message, LogSeverity.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task UndoAsync(InstallRecordItemViewModel recordItem)
    {
        if (IsBusy)
        {
            return;
        }

        if (!userInteractionService.Confirm(
            "Undo Install",
            $"Remove OptiScaler from {recordItem.DisplayName} and restore the backed up files?"))
        {
            return;
        }

        BeginOperation($"Undoing {recordItem.DisplayName}...", "Restoring backed up files...", isIndeterminate: true);
        var progress = CreateProgress();

        try
        {
            var outcome = await installationService.UndoAsync(recordItem.Record, progress, operationCts!.Token);
            AddLog(outcome.Success ? LogSeverity.Success : LogSeverity.Error, outcome.Message);

            await ReloadInstalledGamesAsync();
            await ReloadSnapshotsAsync();
            SynchronizeGameInstallState();

            StatusText = outcome.Success ? "Restore complete." : "Restore failed.";
            CurrentStepText = outcome.Message;
            ShowBanner(
                outcome.Success ? "Restore complete" : "Restore failed",
                outcome.Message,
                outcome.Success ? LogSeverity.Success : LogSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Restore cancelled.";
            CurrentStepText = "Undo cancelled.";
            AddLog(LogSeverity.Warning, "Undo cancelled.");
            ShowBanner("Restore cancelled", "The undo flow stopped before finishing.", LogSeverity.Warning);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task RestoreSnapshotAsync(BackupSnapshotItemViewModel snapshot)
    {
        if (IsBusy)
        {
            return;
        }

        if (!userInteractionService.Confirm(
            "Restore Backup",
            $"Restore {snapshot.DisplayName} from the snapshot created on {snapshot.CreatedText}?"))
        {
            return;
        }

        BeginOperation($"Restoring {snapshot.DisplayName}...", "Applying snapshot backup files...", isIndeterminate: true);
        var progress = CreateProgress();

        try
        {
            var outcome = await installationService.RestoreSnapshotAsync(snapshot.SnapshotId, progress, operationCts!.Token);
            AddLog(outcome.Success ? LogSeverity.Success : LogSeverity.Error, outcome.Message);

            await ReloadInstalledGamesAsync();
            await ReloadSnapshotsAsync();
            SynchronizeGameInstallState();
            UpdateRecoveryBanner();

            StatusText = outcome.Success ? "Snapshot restored." : "Snapshot restore failed.";
            CurrentStepText = outcome.Message;
            ShowBanner(
                outcome.Success ? "Snapshot restored" : "Snapshot restore failed",
                outcome.Message,
                outcome.Success ? LogSeverity.Success : LogSeverity.Error);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Snapshot restore cancelled.";
            CurrentStepText = "Snapshot restore cancelled.";
            AddLog(LogSeverity.Warning, "Snapshot restore cancelled.");
            ShowBanner("Snapshot restore cancelled", "The restore operation stopped before it finished.", LogSeverity.Warning);
        }
        finally
        {
            EndOperation();
        }
    }

    private Task OpenSnapshotFolderAsync(BackupSnapshotItemViewModel snapshot)
    {
        userInteractionService.OpenFolder(snapshot.OpenFolderPath);
        StatusText = $"Opened folder for {snapshot.DisplayName}.";
        CurrentStepText = "Folder opened in Explorer.";
        return Task.CompletedTask;
    }

    private async Task DeleteSnapshotAsync(BackupSnapshotItemViewModel snapshot)
    {
        if (IsBusy || !snapshot.CanDelete)
        {
            return;
        }

        var deleteMessage = snapshot.CanRestore
            ? $"Delete the stored backup for {snapshot.DisplayName}? This permanently removes that snapshot from the restore list."
            : $"Delete the completed snapshot record for {snapshot.DisplayName}?";
        if (!userInteractionService.Confirm("Delete Snapshot", deleteMessage))
        {
            return;
        }

        BeginOperation($"Deleting {snapshot.DisplayName} snapshot...", "Cleaning up snapshot files...", isIndeterminate: true);

        try
        {
            var deleted = await installationService.DeleteSnapshotAsync(snapshot.SnapshotId, operationCts!.Token);
            await ReloadSnapshotsAsync();
            SynchronizeGameInstallState();
            UpdateRecoveryBanner();

            StatusText = deleted ? "Snapshot deleted." : "Snapshot was already missing.";
            CurrentStepText = StatusText;
            ShowBanner(
                deleted ? "Snapshot deleted" : "Snapshot missing",
                deleted
                    ? $"{snapshot.DisplayName} was removed from the backup list."
                    : "That snapshot was already gone, so the list was refreshed instead.",
                deleted ? LogSeverity.Success : LogSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Delete cancelled.";
            CurrentStepText = "Snapshot deletion cancelled.";
            ShowBanner("Delete cancelled", "The snapshot delete operation was cancelled.", LogSeverity.Warning);
        }
        catch (Exception exception)
        {
            StatusText = "Delete failed.";
            CurrentStepText = "Snapshot deletion failed.";
            AddLog(LogSeverity.Error, exception.Message);
            ShowBanner("Delete failed", exception.Message, LogSeverity.Error);
        }
        finally
        {
            EndOperation();
        }
    }

    private async Task CopyDiagnosticsAsync()
    {
        var report = BuildDiagnosticsReport();
        userInteractionService.CopyText(report);
        StatusText = "Diagnostics copied.";
        CurrentStepText = "Diagnostics copied to the clipboard.";
        ShowBanner("Diagnostics copied", "A diagnostics summary with recent logs is now on the clipboard.", LogSeverity.Success);
        await Task.CompletedTask;
    }

    private async Task ExportLogsAsync()
    {
        var suggestedFileName = $"OptiScalerInstaller-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.log";
        var path = userInteractionService.PickSaveFile(
            "Export diagnostics and logs",
            suggestedFileName,
            "Log files (*.log)|*.log|Text files (*.txt)|*.txt|All files (*.*)|*.*");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await File.WriteAllTextAsync(path, BuildDiagnosticsReport());
        StatusText = "Logs exported.";
        CurrentStepText = $"Saved diagnostics to {path}.";
        ShowBanner("Logs exported", $"Diagnostics and recent logs were written to {path}.", LogSeverity.Success);
    }

    private void CancelCurrentOperation()
    {
        if (operationCts is null)
        {
            return;
        }

        CurrentStepText = "Cancellation requested...";
        operationCts.Cancel();
    }

    private Progress<InstallerLogEntry> CreateProgress()
        => new(entry =>
        {
            CurrentStepText = entry.Message;
            AddLog(entry.Severity, entry.Message);
        });

    private void ReplaceGames(IReadOnlyList<DetectedGame> detectedGames)
    {
        foreach (var existing in Games)
        {
            existing.PropertyChanged -= OnGamePropertyChanged;
        }

        Games.Clear();
        foreach (var detectedGame in detectedGames)
        {
            var item = new DetectedGameItemViewModel(detectedGame);
            item.PropertyChanged += OnGamePropertyChanged;
            Games.Add(item);
        }
    }

    private DetectedGameItemViewModel AddOrReplaceGame(DetectedGameItemViewModel item)
    {
        var existing = Games.FirstOrDefault(game =>
            string.Equals(game.InstallPath, item.InstallPath, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            existing.PropertyChanged -= OnGamePropertyChanged;
            var index = Games.IndexOf(existing);
            item.PropertyChanged += OnGamePropertyChanged;
            Games[index] = item;
        }
        else
        {
            item.PropertyChanged += OnGamePropertyChanged;
            Games.Add(item);
        }

        SynchronizeGameInstallState();
        RefreshVisibleGames();
        return item;
    }

    private async Task ReloadInstalledGamesAsync()
    {
        var installed = await installationService.LoadInstalledGamesAsync();
        InstalledGames.Clear();

        foreach (var record in installed.OrderByDescending(record => record.InstalledAtUtc))
        {
            InstalledGames.Add(new InstallRecordItemViewModel(record));
        }

        OnPropertyChanged(nameof(HasInstalledGames));
    }

    private async Task ReloadSnapshotsAsync()
    {
        var previouslySelectedSnapshotId = SelectedSnapshot?.SnapshotId;
        var snapshots = await installationService.LoadSnapshotsAsync();
        Snapshots.Clear();

        foreach (var snapshot in snapshots.OrderByDescending(snapshot => snapshot.CreatedAtUtc))
        {
            Snapshots.Add(new BackupSnapshotItemViewModel(snapshot));
        }

        SelectedSnapshot = previouslySelectedSnapshotId is null
            ? Snapshots.FirstOrDefault()
            : Snapshots.FirstOrDefault(snapshot => string.Equals(snapshot.SnapshotId, previouslySelectedSnapshotId, StringComparison.OrdinalIgnoreCase))
                ?? Snapshots.FirstOrDefault();

        OnPropertyChanged(nameof(HasSnapshots));
    }

    private void SynchronizeGameInstallState()
    {
        var recordsByGameKey = InstalledGames.ToDictionary(item => item.Record.GameKey, StringComparer.OrdinalIgnoreCase);
        var recordsByPath = InstalledGames.ToDictionary(item => item.InstallPath, StringComparer.OrdinalIgnoreCase);
        var snapshotsByGameKey = Snapshots
            .GroupBy(item => item.Manifest.GameKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Manifest, StringComparer.OrdinalIgnoreCase);
        var snapshotsByPath = Snapshots
            .GroupBy(item => item.InstallPath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First().Manifest, StringComparer.OrdinalIgnoreCase);

        foreach (var game in Games)
        {
            recordsByGameKey.TryGetValue(game.Model.GameKey, out var recordByGameKey);
            recordsByPath.TryGetValue(game.InstallPath, out var recordByPath);
            snapshotsByGameKey.TryGetValue(game.Model.GameKey, out var snapshotByGameKey);
            snapshotsByPath.TryGetValue(game.InstallPath, out var snapshotByPath);

            game.SyncInstallState(recordByGameKey?.Record ?? recordByPath?.Record, snapshotByGameKey ?? snapshotByPath);
        }

        OnPropertyChanged(nameof(SelectedGameReleaseTagText));
    }

    private void RefreshVisibleGames()
    {
        if (deferredVisibleGamesRefreshDepth > 0)
        {
            pendingVisibleGamesRefresh = true;
            return;
        }

        gamesView.Refresh();
        var selectedPath = SelectedGame?.InstallPath;
        var matchingGames = GetVisibleGames().ToList();
        VisibleGameCount = matchingGames.Count;

        SelectedGame = selectedPath is null
            ? matchingGames.FirstOrDefault()
            : matchingGames.FirstOrDefault(game =>
                string.Equals(game.InstallPath, selectedPath, StringComparison.OrdinalIgnoreCase))
                ?? matchingGames.FirstOrDefault();

        EmptyGamesMessage = Games.Count == 0
            ? "No supported games found yet."
            : "No detected games match the current search or filter.";

        pendingVisibleGamesRefresh = false;
        OnPropertyChanged(nameof(VisibleGames));
        OnPropertyChanged(nameof(HasNoVisibleGames));
        OnPropertyChanged(nameof(VisibleGamesSummaryText));
        NotifyCommandStates();
    }

    private bool FilterVisibleGame(object item)
        => item is DetectedGameItemViewModel game && MatchesSearchAndFilter(game);

    private bool MatchesSearchAndFilter(DetectedGameItemViewModel game)
    {
        var passesFilter = SelectedGameFilter.Key switch
        {
            FilterInstallable => game.CanInstall,
            FilterSelected => game.IsSelected,
            FilterNeedsReview => game.Model.SupportStatus is SupportStatus.Warning or SupportStatus.Unsupported or SupportStatus.Blocked,
            _ => true,
        };

        if (!passesFilter)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(SearchText))
        {
            return true;
        }

        return SearchMatches(game.DisplayName) ||
               SearchMatches(game.InstallPath) ||
               SearchMatches(game.ExecutablePath) ||
               SearchMatches(game.SourceLabel) ||
               SearchMatches(game.StatusText);
    }

    private bool SearchMatches(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           value.Contains(SearchText.Trim(), StringComparison.OrdinalIgnoreCase);

    private void SelectAllVisibleGames()
    {
        RunWithDeferredVisibleGamesRefresh(() =>
        {
            foreach (var game in GetVisibleGames().Where(game => game.CanSelect && !game.IsSelected).ToList())
            {
                game.IsSelected = true;
            }
        });
    }

    private void SelectNoneVisibleGames()
    {
        RunWithDeferredVisibleGamesRefresh(() =>
        {
            foreach (var game in GetVisibleGames().Where(game => game.IsSelected).ToList())
            {
                game.IsSelected = false;
            }
        });
    }

    private void OpenSelectedGameFolder()
    {
        if (SelectedGame is null)
        {
            return;
        }

        userInteractionService.OpenFolder(SelectedGame.InstallPath);
        StatusText = $"Opened {SelectedGame.DisplayName}.";
        CurrentStepText = "Folder opened in Explorer.";
    }

    private void BeginOperation(string status, string step, bool isIndeterminate, int maximum = 1)
    {
        IsBusy = true;
        operationCts = new CancellationTokenSource();
        StatusText = status;
        CurrentStepText = step;
        ProgressMaximum = Math.Max(1, maximum);
        ProgressValue = 0;
        IsProgressIndeterminate = isIndeterminate;
        IsProgressVisible = true;
        ProgressText = isIndeterminate ? "Working..." : $"0 / {ProgressMaximum:0}";
    }

    private void EndOperation()
    {
        operationCts?.Dispose();
        operationCts = null;
        IsBusy = false;
        IsProgressVisible = false;
        IsProgressIndeterminate = false;
        ProgressMaximum = 1;
        ProgressValue = 0;
        ProgressText = string.Empty;
        NotifyCommandStates();
    }

    public void ReportUnhandledException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (IsBusy)
        {
            EndOperation();
        }

        StatusText = "Unexpected error.";
        CurrentStepText = "The app recovered from an unexpected UI error.";
        AddLog(LogSeverity.Error, exception.ToString());
        ShowBanner("Unexpected error", exception.Message, LogSeverity.Error);
    }

    private void UpdateProgress(int completed, int total)
    {
        ProgressMaximum = Math.Max(1, total);
        ProgressValue = Math.Min(completed, total);
        ProgressText = $"{ProgressValue:0} / {ProgressMaximum:0}";
    }

    private void SetOperationStep(string message)
    {
        CurrentStepText = message;
    }

    private GpuVendor UpdateGpuVendorText()
    {
        var vendor = gpuDetector.DetectGpuVendor();
        GpuVendorText = vendor switch
        {
            GpuVendor.Nvidia => "GPU: Nvidia detected",
            GpuVendor.Amd => "GPU: AMD detected",
            GpuVendor.Intel => "GPU: Intel detected",
            _ => "GPU: Unknown",
        };

        return vendor;
    }

    private void AddLog(LogSeverity severity, string message)
    {
        var entry = InstallerLogEntry.Create(severity, message);
        lock (logsGate)
        {
            while (Logs.Count >= MaxLogEntries)
            {
                Logs.RemoveAt(0);
            }

            Logs.Add(LogEntryViewModel.FromCore(entry));
            runLogger?.Log(entry);
        }
    }

    private bool CanInstallSelected()
        => !IsBusy && Games.Any(game => game.IsSelected && game.CanInstall);

    private bool CanInstallAny()
        => !IsBusy && Games.Any(game => game.CanInstall);

    private void NotifyCommandStates()
    {
        RefreshCommand.NotifyCanExecuteChanged();
        BrowseFolderCommand.NotifyCanExecuteChanged();
        InstallSelectedCommand.NotifyCanExecuteChanged();
        InstallAllCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        SelectAllVisibleCommand.NotifyCanExecuteChanged();
        SelectNoneVisibleCommand.NotifyCanExecuteChanged();
        OpenSelectedGameFolderCommand.NotifyCanExecuteChanged();
        RestoreSnapshotCommand.NotifyCanExecuteChanged();
        OpenSnapshotFolderCommand.NotifyCanExecuteChanged();
        DeleteSnapshotCommand.NotifyCanExecuteChanged();
        DismissBannerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasNoVisibleGames));
        OnPropertyChanged(nameof(VisibleGames));
        OnPropertyChanged(nameof(VisibleGamesSummaryText));
    }

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DetectedGameItemViewModel.IsSelected) or nameof(DetectedGameItemViewModel.ForceUnsupportedInstall))
        {
            OnPropertyChanged(nameof(VisibleGamesSummaryText));
            RefreshVisibleGames();
        }
    }

    private IEnumerable<DetectedGameItemViewModel> GetVisibleGames()
        => gamesView.Cast<DetectedGameItemViewModel>();

    private void RunWithDeferredVisibleGamesRefresh(Action action)
    {
        deferredVisibleGamesRefreshDepth++;
        try
        {
            action();
        }
        finally
        {
            deferredVisibleGamesRefreshDepth--;
            if (deferredVisibleGamesRefreshDepth == 0 && pendingVisibleGamesRefresh)
            {
                RefreshVisibleGames();
            }
        }
    }

    private void ShowBanner(string title, string message, LogSeverity severity)
    {
        bannerSeverity = severity;
        BannerTitle = title;
        BannerMessage = message;
        OnPropertyChanged(nameof(HasBanner));
        OnPropertyChanged(nameof(BannerAccentBrush));
        OnPropertyChanged(nameof(BannerBackgroundBrush));
        DismissBannerCommand.NotifyCanExecuteChanged();
    }

    private void ClearBanner()
    {
        BannerTitle = string.Empty;
        BannerMessage = string.Empty;
        OnPropertyChanged(nameof(HasBanner));
        DismissBannerCommand.NotifyCanExecuteChanged();
    }

    private void UpdateRecoveryBanner()
    {
        var recoverableSnapshots = Snapshots.Where(snapshot =>
            snapshot.Manifest.Status is
                SnapshotTransactionStatus.Pending or
                SnapshotTransactionStatus.RollingBack or
                SnapshotTransactionStatus.RollbackFailed or
                SnapshotTransactionStatus.Restoring or
                SnapshotTransactionStatus.RestoreFailed).ToList();
        if (recoverableSnapshots.Count > 0)
        {
            ShowBanner(
                "Recovery available",
                $"{recoverableSnapshots.Count} snapshot(s) can still be restored. Use the Backups & Restore tab if you need to roll a game back.",
                LogSeverity.Warning);
        }
        else if (string.Equals(BannerTitle, "Recovery available", StringComparison.Ordinal))
        {
            ClearBanner();
        }
    }

    private string BuildDiagnosticsReport()
    {
        var builder = new StringBuilder();
        builder.AppendLine("OptiScaler Installer Diagnostics");
        builder.AppendLine($"Generated: {DateTimeOffset.Now:O}");
        builder.AppendLine($"AppProduct: {buildInfo.ProductName}");
        builder.AppendLine($"AppVersion: {buildInfo.AssemblyVersion}");
        builder.AppendLine($"AppFileVersion: {buildInfo.FileVersion}");
        builder.AppendLine($"AppBuild: {buildInfo.InformationalVersion}");
        builder.AppendLine($"GPU: {GpuVendorText}");
        builder.AppendLine($"Status: {StatusText}");
        builder.AppendLine($"CurrentStep: {CurrentStepText}");
        builder.AppendLine($"Filter: {SelectedGameFilter.Label}");
        builder.AppendLine($"DetectedGames: {Games.Count}");
        builder.AppendLine($"VisibleGames: {VisibleGameCount}");
        builder.AppendLine($"SelectedGames: {Games.Count(game => game.IsSelected)}");
        builder.AppendLine($"ManagedInstalls: {InstalledGames.Count}");
        builder.AppendLine($"Snapshots: {Snapshots.Count}");
        builder.AppendLine($"RunLogPath: {runLogger?.LogFilePath ?? "Unavailable"}");

        if (SelectedGame is not null)
        {
            builder.AppendLine($"SelectedGame: {SelectedGame.DisplayName}");
            builder.AppendLine($"SelectedGamePath: {SelectedGame.InstallPath}");
            builder.AppendLine($"SelectedGameStatus: {SelectedGame.StatusText}");
            builder.AppendLine($"SelectedGameRelease: {SelectedGameReleaseTagText}");
            builder.AppendLine($"SelectedGameProxy: {SelectedGame.ProxyChoiceText}");
        }

        builder.AppendLine();
        builder.AppendLine("[ManagedInstalls]");
        foreach (var install in InstalledGames)
        {
            builder.AppendLine($"{install.DisplayName} | {install.Record.ReleaseTag} | {install.Record.ProxyName} | {install.InstallPath}");
        }

        builder.AppendLine();
        builder.AppendLine("[Snapshots]");
        foreach (var snapshot in Snapshots)
        {
            builder.AppendLine($"{snapshot.DisplayName} | {snapshot.StatusText} | {snapshot.ReleaseTag} | {snapshot.ProxyName} | {snapshot.TransactionRootPath}");
            if (snapshot.HasError)
            {
                builder.AppendLine($"  Error: {snapshot.ErrorText}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("[RecentLogEntries]");
        foreach (var log in Logs)
        {
            builder.AppendLine($"[{log.Time}] [{log.Severity}] {log.Message}");
        }

        if (!string.IsNullOrWhiteSpace(runLogger?.LogFilePath) && File.Exists(runLogger.LogFilePath))
        {
            builder.AppendLine();
            builder.AppendLine("[RunLogFile]");
            try
            {
                builder.Append(File.ReadAllText(runLogger.LogFilePath));
            }
            catch (IOException)
            {
                builder.AppendLine("Current run log could not be read while the session was active.");
            }
        }

        return builder.ToString();
    }
}
