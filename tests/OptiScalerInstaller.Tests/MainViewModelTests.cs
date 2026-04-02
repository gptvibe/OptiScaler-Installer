using System.Windows;
using OptiScalerInstaller.App.Services;
using OptiScalerInstaller.App.ViewModels;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.Tests;

public sealed class MainViewModelTests
{
    [Fact]
    public async Task InitializeAsync_LoadsSelectedGameDetailsFromManagedState()
    {
        var scanner = new FakeGameScannerService
        {
            ScannedGames =
            [
                CreateGame("Alpha Game", @"C:\Games\Alpha", SupportStatus.Supported),
                CreateGame("Beta Game", @"C:\Games\Beta", SupportStatus.Warning),
            ],
        };
        var workflow = new FakeInstallationWorkflowService
        {
            InstalledRecords =
            [
                CreateInstallRecord("steam-100", "Alpha Game", @"C:\Games\Alpha", "v1.2.3", "dxgi.dll"),
            ],
            Snapshots =
            [
                CreateSnapshot("snap-1", "steam-100", "Alpha Game", @"C:\Games\Alpha", SnapshotTransactionStatus.Applied, "v1.2.3", "dxgi.dll"),
            ],
        };

        var viewModel = CreateViewModel(scanner, workflow);
        await viewModel.InitializeAsync();

        Assert.Equal(2, viewModel.VisibleGames.Count);
        Assert.NotNull(viewModel.SelectedGame);
        Assert.Equal("Alpha Game", viewModel.SelectedGame!.DisplayName);
        Assert.True(viewModel.SelectedGame.HasInstalledRecord);
        Assert.Equal("v1.2.3", viewModel.SelectedGameReleaseTagText);
        Assert.Equal("dxgi.dll", viewModel.SelectedGame.ProxyChoiceText);
    }

    [Fact]
    public async Task SearchFiltersAndSelectionCommandsWorkOnVisibleGames()
    {
        var scanner = new FakeGameScannerService
        {
            ScannedGames =
            [
                CreateGame("Alpha Game", @"C:\Games\Alpha", SupportStatus.Supported),
                CreateGame("Beta Game", @"C:\Games\Beta", SupportStatus.Unsupported),
                CreateGame("Gamma Game", @"C:\Games\Gamma", SupportStatus.Blocked),
            ],
        };

        var viewModel = CreateViewModel(scanner, new FakeInstallationWorkflowService());
        await viewModel.InitializeAsync();

        viewModel.SearchText = "Beta";
        Assert.Single(viewModel.VisibleGames);
        Assert.Equal("Beta Game", viewModel.VisibleGames[0].DisplayName);

        viewModel.SelectAllVisibleCommand.Execute(null);
        Assert.True(viewModel.VisibleGames[0].IsSelected);

        viewModel.SelectedGameFilter = viewModel.GameFilters.First(filter => filter.Key == "selected");
        Assert.Single(viewModel.VisibleGames);
        Assert.Equal("Beta Game", viewModel.VisibleGames[0].DisplayName);

        viewModel.SelectNoneVisibleCommand.Execute(null);
        Assert.Empty(viewModel.VisibleGames);

        viewModel.SearchText = string.Empty;
        viewModel.SelectedGameFilter = viewModel.GameFilters.First(filter => filter.Key == "all");
        viewModel.SelectAllVisibleCommand.Execute(null);

        Assert.True(viewModel.Games.First(game => game.DisplayName == "Alpha Game").IsSelected);
        Assert.True(viewModel.Games.First(game => game.DisplayName == "Beta Game").IsSelected);
        Assert.False(viewModel.Games.First(game => game.DisplayName == "Gamma Game").IsSelected);
    }

    [Fact]
    public async Task InitializeAsync_WithRecoverableSnapshots_ShowsRecoveryBanner()
    {
        var scanner = new FakeGameScannerService();
        var workflow = new FakeInstallationWorkflowService
        {
            Snapshots =
            [
                CreateSnapshot("snap-recover", "steam-404", "Recovery Game", @"C:\Games\Recovery", SnapshotTransactionStatus.RestoreFailed, "v2.0.0", "winmm.dll"),
            ],
        };

        var viewModel = CreateViewModel(scanner, workflow);
        await viewModel.InitializeAsync();

        Assert.True(viewModel.HasBanner);
        Assert.Equal("Recovery available", viewModel.BannerTitle);
    }

    [Fact]
    public async Task CopyDiagnosticsCommand_IncludesAppVersionAndBuildInfo()
    {
        var scanner = new FakeGameScannerService
        {
            ScannedGames = [CreateGame("Alpha Game", @"C:\Games\Alpha", SupportStatus.Supported)],
        };
        var userInteraction = new FakeUserInteractionService();
        var viewModel = new MainViewModel(
            scanner,
            new FakeGpuDetectorService(),
            new FakeInstallationWorkflowService(),
            userInteraction);

        await viewModel.InitializeAsync();
        viewModel.CopyDiagnosticsCommand.Execute(null);
        await Task.Delay(50);

        Assert.Contains("AppVersion:", userInteraction.CopiedText);
        Assert.Contains("AppBuild:", userInteraction.CopiedText);
    }

    private static MainViewModel CreateViewModel(
        FakeGameScannerService scanner,
        FakeInstallationWorkflowService workflow)
        => new(
            scanner,
            new FakeGpuDetectorService(),
            workflow,
            new FakeUserInteractionService());

    private static DetectedGame CreateGame(string displayName, string installPath, SupportStatus supportStatus)
        => new()
        {
            GameKey = displayName switch
            {
                "Alpha Game" => "steam-100",
                "Beta Game" => "steam-200",
                "Gamma Game" => "steam-300",
                _ => $"steam-{Math.Abs(displayName.GetHashCode())}",
            },
            Source = GameSource.Steam,
            DisplayName = displayName,
            InstallPath = installPath,
            ExePath = Path.Combine(installPath, $"{displayName}.exe"),
            SupportStatus = supportStatus,
            ManifestEntry = new SupportedGameEntry
            {
                DisplayName = displayName,
                ExeNames = [$"{displayName}.exe"],
                PreferredProxy = "dxgi.dll",
                FallbackProxies = ["winmm.dll"],
                InstallPolicy = supportStatus switch
                {
                    SupportStatus.Supported => InstallPolicy.Supported,
                    SupportStatus.Warning => InstallPolicy.Warn,
                    SupportStatus.Blocked => InstallPolicy.Blocked,
                    _ => InstallPolicy.Warn,
                },
            },
        };

    private static InstallRecord CreateInstallRecord(
        string gameKey,
        string displayName,
        string installPath,
        string releaseTag,
        string proxyName)
        => new()
        {
            GameKey = gameKey,
            DisplayName = displayName,
            InstallPath = installPath,
            MarkerPath = Path.Combine(installPath, "OptiScalerInstaller.manifest.json"),
            ReleaseTag = releaseTag,
            ProxyName = proxyName,
            InstalledAtUtc = new DateTimeOffset(2026, 4, 1, 8, 30, 0, TimeSpan.Zero),
        };

    private static BackupSnapshotManifest CreateSnapshot(
        string snapshotId,
        string gameKey,
        string displayName,
        string installPath,
        SnapshotTransactionStatus status,
        string releaseTag,
        string proxyName)
        => new()
        {
            SnapshotId = snapshotId,
            GameKey = gameKey,
            DisplayName = displayName,
            InstallPath = installPath,
            MarkerPath = Path.Combine(installPath, "OptiScalerInstaller.manifest.json"),
            ReleaseTag = releaseTag,
            ProxyName = proxyName,
            TransactionRootPath = Path.Combine(@"C:\Backups", snapshotId),
            CreatedAtUtc = new DateTimeOffset(2026, 4, 1, 8, 0, 0, TimeSpan.Zero),
            LastUpdatedAtUtc = new DateTimeOffset(2026, 4, 1, 8, 5, 0, TimeSpan.Zero),
            Status = status,
        };

    private sealed class FakeGameScannerService : IGameScannerService
    {
        public IReadOnlyList<DetectedGame> ScannedGames { get; init; } = [];

        public DetectedGame? ManualGame { get; init; }

        public Task<IReadOnlyList<DetectedGame>> ScanSteamGamesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(ScannedGames);

        public Task<DetectedGame> InspectManualFolderAsync(string installPath, CancellationToken cancellationToken = default)
            => Task.FromResult(ManualGame ?? CreateGame(Path.GetFileName(installPath), installPath, SupportStatus.Unsupported));
    }

    private sealed class FakeGpuDetectorService : IGpuDetectorService
    {
        public GpuVendor DetectGpuVendor() => GpuVendor.Nvidia;
    }

    private sealed class FakeInstallationWorkflowService : IInstallationWorkflowService
    {
        public IReadOnlyList<InstallRecord> InstalledRecords { get; init; } = [];

        public IReadOnlyList<BackupSnapshotManifest> Snapshots { get; init; } = [];

        public Task<IReadOnlyList<InstallRecord>> LoadInstalledGamesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(InstalledRecords);

        public Task<IReadOnlyList<BackupSnapshotManifest>> LoadSnapshotsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshots);

        public Task<IReadOnlyList<BackupSnapshotManifest>> LoadRecoverableSnapshotsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(Snapshots);

        public Task<PreparedReleaseAsset> PrepareLatestStableReleaseAsync(IProgress<InstallerLogEntry>? progress = null, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreparedReleaseAsset
            {
                Release = new ReleaseAsset
                {
                    TagName = "v-test",
                    AssetName = "OptiScaler_test.7z",
                    DownloadUrl = "https://example.test/release.7z",
                    PublishedAtUtc = DateTimeOffset.UtcNow,
                },
                ExtractedPath = @"C:\Temp\OptiScaler",
            });

        public Task<InstallOutcome> InstallAsync(
            DetectedGame game,
            InstallationRequest request,
            PreparedReleaseAsset? preparedRelease = null,
            IProgress<InstallerLogEntry>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(InstallOutcome.Succeeded("Installed"));

        public Task<InstallOutcome> UndoAsync(
            InstallRecord record,
            IProgress<InstallerLogEntry>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(InstallOutcome.Succeeded("Restored"));

        public Task<InstallOutcome> RestoreBackupAsync(
            string gameKey,
            IProgress<InstallerLogEntry>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(InstallOutcome.Succeeded("Restored"));

        public Task<InstallOutcome> RestoreSnapshotAsync(
            string snapshotId,
            IProgress<InstallerLogEntry>? progress = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(InstallOutcome.Succeeded("Restored"));

        public Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default)
            => Task.FromResult(true);
    }

    private sealed class FakeUserInteractionService : IUserInteractionService
    {
        public string CopiedText { get; private set; } = string.Empty;

        public string? PickFolder() => null;

        public string? PickSaveFile(string title, string suggestedFileName, string filter) => null;

        public bool Confirm(string title, string message) => true;

        public void CopyText(string text) => CopiedText = text;

        public void OpenFolder(string path)
        {
        }

        public void ShowMessage(string title, string message, MessageBoxImage icon = MessageBoxImage.Information)
        {
        }
    }
}
