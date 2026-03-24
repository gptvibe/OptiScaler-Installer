using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using OptiScalerInstaller.App.Infrastructure;
using OptiScalerInstaller.App.Services;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.App.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly GameScannerService gameScannerService;
    private readonly GpuDetector gpuDetector;
    private readonly InstallationService installationService;
    private readonly IUserInteractionService userInteractionService;

    private string gpuVendorText = "Detecting GPU...";
    private string statusText = "Ready";
    private bool isBusy;

    public MainViewModel(
        GameScannerService gameScannerService,
        GpuDetector gpuDetector,
        InstallationService installationService,
        IUserInteractionService userInteractionService)
    {
        this.gameScannerService = gameScannerService;
        this.gpuDetector = gpuDetector;
        this.installationService = installationService;
        this.userInteractionService = userInteractionService;

        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !IsBusy);
        BrowseFolderCommand = new AsyncRelayCommand(BrowseFolderAsync, () => !IsBusy);
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, CanInstallSelected);
        InstallAllCommand = new AsyncRelayCommand(InstallAllAsync, CanInstallAny);
        UndoCommand = new AsyncRelayCommand<InstallRecordItemViewModel>(UndoAsync, _ => !IsBusy);
    }

    public ObservableCollection<DetectedGameItemViewModel> Games { get; } = [];

    public ObservableCollection<InstallRecordItemViewModel> InstalledGames { get; } = [];

    public ObservableCollection<LogEntryViewModel> Logs { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand BrowseFolderCommand { get; }

    public AsyncRelayCommand InstallSelectedCommand { get; }

    public AsyncRelayCommand InstallAllCommand { get; }

    public AsyncRelayCommand<InstallRecordItemViewModel> UndoCommand { get; }

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

    public bool IsBusy
    {
        get => isBusy;
        private set
        {
            if (SetProperty(ref isBusy, value))
            {
                OnPropertyChanged(nameof(HasNoGames));
                NotifyCommandStates();
            }
        }
    }

    public bool HasNoGames => !Games.Any() && !IsBusy;

    public bool HasInstalledGames => InstalledGames.Any();

    public async Task InitializeAsync() => await RefreshAsync();

    private async Task RefreshAsync()
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = "Scanning Steam libraries...";
        AddLog(LogSeverity.Info, "Starting auto-detection.");

        try
        {
            GpuVendorText = gpuDetector.DetectGpuVendor() switch
            {
                GpuVendor.Nvidia => "GPU: Nvidia detected",
                GpuVendor.Amd => "GPU: AMD detected",
                GpuVendor.Intel => "GPU: Intel detected",
                _ => "GPU: Unknown",
            };

            foreach (var existing in Games)
            {
                existing.PropertyChanged -= OnGamePropertyChanged;
            }

            var detectedGames = await gameScannerService.ScanSteamGamesAsync();
            Games.Clear();

            foreach (var detectedGame in detectedGames)
            {
                var item = new DetectedGameItemViewModel(detectedGame);
                item.PropertyChanged += OnGamePropertyChanged;
                Games.Add(item);
            }

            await ReloadInstalledGamesAsync();
            StatusText = detectedGames.Count == 0
                ? "No supported Steam games found."
                : $"Found {detectedGames.Count} supported game(s).";

            AddLog(
                detectedGames.Count == 0 ? LogSeverity.Warning : LogSeverity.Success,
                StatusText);
        }
        catch (Exception exception)
        {
            StatusText = "Scan failed.";
            AddLog(LogSeverity.Error, exception.Message);
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasInstalledGames));
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

        IsBusy = true;
        StatusText = "Inspecting manual folder...";
        AddLog(LogSeverity.Info, $"Inspecting {selectedPath}");

        try
        {
            var detectedGame = await gameScannerService.InspectManualFolderAsync(selectedPath);
            if (detectedGame.SupportStatus == SupportStatus.Blocked)
            {
                userInteractionService.ShowMessage(
                    "Blocked Game",
                    $"{detectedGame.DisplayName} is marked blocked and will not be installed automatically.",
                    MessageBoxImage.Warning);
                AddLog(LogSeverity.Warning, $"{detectedGame.DisplayName} is blocked from auto-install.");
                return;
            }

            var item = new DetectedGameItemViewModel(detectedGame);
            if (detectedGame.SupportStatus == SupportStatus.Unsupported)
            {
                var confirmed = userInteractionService.Confirm(
                    "Manual Override",
                    $"{detectedGame.DisplayName} is not officially supported. Continue with a manual override?");

                if (!confirmed)
                {
                    AddLog(LogSeverity.Warning, "Manual override cancelled.");
                    return;
                }

                item.ForceUnsupportedInstall = true;
                item.IsSelected = true;
                AddLog(LogSeverity.Warning, $"Manual override approved for {detectedGame.DisplayName}.");
            }

            AddOrReplaceGame(item);
            StatusText = $"Ready to install {item.DisplayName}.";
        }
        catch (Exception exception)
        {
            AddLog(LogSeverity.Error, exception.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallSelectedAsync()
    {
        var selectedGames = Games.Where(game => game.IsSelected && game.CanInstall).ToList();
        await InstallGamesAsync(selectedGames);
    }

    private async Task InstallAllAsync()
    {
        var games = Games.Where(game => game.CanInstall).ToList();
        foreach (var game in games)
        {
            game.IsSelected = true;
        }

        await InstallGamesAsync(games);
    }

    private async Task InstallGamesAsync(IReadOnlyList<DetectedGameItemViewModel> gamesToInstall)
    {
        if (IsBusy || gamesToInstall.Count == 0)
        {
            if (gamesToInstall.Count == 0)
            {
                userInteractionService.ShowMessage("Nothing Selected", "Choose at least one installable game first.");
            }

            return;
        }

        IsBusy = true;
        StatusText = "Installing selected games...";
        var progress = CreateProgress();
        var gpuVendor = gpuDetector.DetectGpuVendor();

        try
        {
            foreach (var game in gamesToInstall)
            {
                var outcome = await installationService.InstallAsync(
                    game.Model,
                    new InstallationRequest
                    {
                        GpuVendor = gpuVendor,
                        ForceUnsupportedInstall = game.ForceUnsupportedInstall,
                    },
                    progress);

                AddLog(outcome.Success ? LogSeverity.Success : LogSeverity.Error, outcome.Message);

                if (outcome.Success && outcome.Record is not null)
                {
                    UpsertInstalledRecord(outcome.Record);
                }
            }

            StatusText = "Install run complete.";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasInstalledGames));
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

        IsBusy = true;
        StatusText = $"Undoing {recordItem.DisplayName}...";
        var progress = CreateProgress();

        try
        {
            var outcome = await installationService.UndoAsync(recordItem.Record, progress);
            AddLog(outcome.Success ? LogSeverity.Success : LogSeverity.Error, outcome.Message);

            if (outcome.Success)
            {
                InstalledGames.Remove(recordItem);
                OnPropertyChanged(nameof(HasInstalledGames));
            }
        }
        finally
        {
            StatusText = "Ready";
            IsBusy = false;
        }
    }

    private Progress<InstallerLogEntry> CreateProgress()
        => new(entry => AddLog(entry.Severity, entry.Message));

    private void AddOrReplaceGame(DetectedGameItemViewModel item)
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

        OnPropertyChanged(nameof(HasNoGames));
        NotifyCommandStates();
    }

    private async Task ReloadInstalledGamesAsync()
    {
        var installed = await installationService.LoadInstalledGamesAsync();
        InstalledGames.Clear();

        foreach (var record in installed.OrderByDescending(record => record.InstalledAtUtc))
        {
            InstalledGames.Add(new InstallRecordItemViewModel(record));
        }
    }

    private void UpsertInstalledRecord(InstallRecord record)
    {
        var existing = InstalledGames.FirstOrDefault(item =>
            string.Equals(item.Record.GameKey, record.GameKey, StringComparison.OrdinalIgnoreCase));

        var replacement = new InstallRecordItemViewModel(record);
        if (existing is null)
        {
            InstalledGames.Insert(0, replacement);
        }
        else
        {
            var index = InstalledGames.IndexOf(existing);
            InstalledGames[index] = replacement;
        }

        OnPropertyChanged(nameof(HasInstalledGames));
    }

    private void AddLog(LogSeverity severity, string message)
    {
        Logs.Add(LogEntryViewModel.FromCore(InstallerLogEntry.Create(severity, message)));
        while (Logs.Count > 400)
        {
            Logs.RemoveAt(0);
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
        OnPropertyChanged(nameof(HasNoGames));
    }

    private void OnGamePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(DetectedGameItemViewModel.IsSelected) or nameof(DetectedGameItemViewModel.ForceUnsupportedInstall))
        {
            NotifyCommandStates();
        }
    }
}
