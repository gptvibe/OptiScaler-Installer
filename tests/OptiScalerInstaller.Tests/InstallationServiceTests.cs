using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.Tests;

public sealed class InstallationServiceTests
{
    [Fact]
    public async Task InstallAndUndo_RoundTripsManagedFilesAndBackups()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var provider = new TestReleaseAssetProvider(payloadPath, Path.Combine(payloadPath, "OptiPatcher.asi"));
        var stateStore = new InstallStateStore(appPaths);
        var service = new InstallationService(appPaths, provider, stateStore);

        var gamePath = Path.Combine(temp.Path, "Cyberpunk");
        Directory.CreateDirectory(gamePath);
        await File.WriteAllTextAsync(Path.Combine(gamePath, "libxess.dll"), "original-libxess");

        var game = CreateGame("Cyberpunk 2077", gamePath, InstallPolicy.Supported);
        var outcome = await service.InstallAsync(
            game,
            new InstallationRequest { GpuVendor = GpuVendor.Nvidia },
            new Progress<InstallerLogEntry>());

        Assert.True(outcome.Success);
        Assert.NotNull(outcome.Record);
        Assert.True(File.Exists(Path.Combine(gamePath, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(gamePath, "OptiScalerInstaller.manifest.json")));
        Assert.NotEqual("original-libxess", await File.ReadAllTextAsync(Path.Combine(gamePath, "libxess.dll")));

        var undoOutcome = await service.UndoAsync(outcome.Record!, new Progress<InstallerLogEntry>());

        Assert.True(undoOutcome.Success);
        Assert.False(File.Exists(Path.Combine(gamePath, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(gamePath, "OptiScalerInstaller.manifest.json")));
        Assert.Equal("original-libxess", await File.ReadAllTextAsync(Path.Combine(gamePath, "libxess.dll")));
        Assert.Empty(await stateStore.LoadAsync());
    }

    [Fact]
    public async Task InstallAsync_FailsWhenAllProxySlotsAreOccupied()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var provider = new TestReleaseAssetProvider(payloadPath, Path.Combine(payloadPath, "OptiPatcher.asi"));
        var service = new InstallationService(appPaths, provider, new InstallStateStore(appPaths));

        var gamePath = Path.Combine(temp.Path, "BusyGame");
        Directory.CreateDirectory(gamePath);

        foreach (var proxy in new[] { "dxgi.dll", "winmm.dll", "version.dll", "dbghelp.dll", "d3d12.dll", "wininet.dll", "winhttp.dll" })
        {
            await File.WriteAllTextAsync(Path.Combine(gamePath, proxy), $"occupied-{proxy}");
        }

        var outcome = await service.InstallAsync(
            CreateGame("Busy Game", gamePath, InstallPolicy.Supported),
            new InstallationRequest { GpuVendor = GpuVendor.Nvidia },
            new Progress<InstallerLogEntry>());

        Assert.False(outcome.Success);
        Assert.Contains("proxy DLL slot", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_AddsOptiPatcherForIntelOrAmdWhenRequired()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var provider = new TestReleaseAssetProvider(payloadPath, Path.Combine(payloadPath, "OptiPatcher.asi"));
        var service = new InstallationService(appPaths, provider, new InstallStateStore(appPaths));

        var gamePath = Path.Combine(temp.Path, "OptiPatcherGame");
        Directory.CreateDirectory(gamePath);

        var game = CreateGame("OptiPatcher Game", gamePath, InstallPolicy.Supported, requiresOptiPatcher: true);
        var outcome = await service.InstallAsync(
            game,
            new InstallationRequest { GpuVendor = GpuVendor.Intel },
            new Progress<InstallerLogEntry>());

        Assert.True(outcome.Success);
        Assert.True(File.Exists(Path.Combine(gamePath, "plugins", "OptiPatcher.asi")));
        Assert.Contains("LoadAsiPlugins=true", await File.ReadAllTextAsync(Path.Combine(gamePath, "OptiScaler.ini")));
    }

    [Fact]
    public async Task InstallStateStore_UpsertAndRemove_RoundTripsRecords()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var store = new InstallStateStore(appPaths);

        var record = new InstallRecord
        {
            GameKey = "steam-1091500",
            DisplayName = "Cyberpunk 2077",
            InstallPath = @"C:\Games\Cyberpunk 2077",
            MarkerPath = @"C:\Games\Cyberpunk 2077\OptiScalerInstaller.manifest.json",
            ReleaseTag = "v-test",
            ProxyName = "dxgi.dll",
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };

        await store.UpsertAsync(record);
        var loaded = await store.LoadAsync();
        Assert.Single(loaded);
        Assert.Equal(record.GameKey, loaded[0].GameKey);

        await store.RemoveAsync(record.GameKey);
        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task InstallAsync_MidInstallFailure_AutoRollsBackFromSnapshot()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var stateStore = new InstallStateStore(appPaths);
        var provider = new ThrowingPluginReleaseAssetProvider(payloadPath);
        var service = new InstallationService(appPaths, provider, stateStore);

        var gamePath = Path.Combine(temp.Path, "RollbackGame");
        Directory.CreateDirectory(gamePath);
        await File.WriteAllTextAsync(Path.Combine(gamePath, "libxess.dll"), "original-libxess");

        var game = CreateGame("Rollback Game", gamePath, InstallPolicy.Supported, requiresOptiPatcher: true);
        var outcome = await service.InstallAsync(
            game,
            new InstallationRequest { GpuVendor = GpuVendor.Intel },
            new Progress<InstallerLogEntry>());

        Assert.False(outcome.Success);
        Assert.Equal("original-libxess", await File.ReadAllTextAsync(Path.Combine(gamePath, "libxess.dll")));
        Assert.False(File.Exists(Path.Combine(gamePath, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(gamePath, "OptiScalerInstaller.manifest.json")));

        var snapshot = await stateStore.FindLatestSnapshotByGameKeyAsync(game.GameKey);
        Assert.NotNull(snapshot);
        Assert.Equal(SnapshotTransactionStatus.RolledBack, snapshot!.Status);
        Assert.Contains(snapshot.Files, file =>
            file.ReplacedExistingFile &&
            !string.IsNullOrWhiteSpace(file.BackupPath) &&
            file.OriginalFileSizeBytes.HasValue &&
            !string.IsNullOrWhiteSpace(file.OriginalFileSha256));
    }

    [Fact]
    public async Task UndoAsync_MidRestoreFailure_PreservesBackupsForRetry()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var provider = new TestReleaseAssetProvider(payloadPath, Path.Combine(payloadPath, "OptiPatcher.asi"));
        var stateStore = new InstallStateStore(appPaths);
        var service = new InstallationService(appPaths, provider, stateStore);

        var gamePath = Path.Combine(temp.Path, "RestoreLockGame");
        Directory.CreateDirectory(gamePath);
        await File.WriteAllTextAsync(Path.Combine(gamePath, "libxess.dll"), "original-libxess");

        var game = CreateGame("Restore Lock Game", gamePath, InstallPolicy.Supported);
        var installOutcome = await service.InstallAsync(
            game,
            new InstallationRequest { GpuVendor = GpuVendor.Nvidia },
            new Progress<InstallerLogEntry>());

        Assert.True(installOutcome.Success);
        Assert.NotNull(installOutcome.Record);

        var snapshot = await stateStore.FindLatestSnapshotByGameKeyAsync(game.GameKey);
        Assert.NotNull(snapshot);
        var backupPath = snapshot!.Files.First(file => file.ReplacedExistingFile && file.RelativePath == "libxess.dll").BackupPath;
        Assert.False(string.IsNullOrWhiteSpace(backupPath));
        Assert.True(File.Exists(backupPath!));

        using var lockStream = new FileStream(Path.Combine(gamePath, "libxess.dll"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var failedUndo = await service.UndoAsync(installOutcome.Record!, new Progress<InstallerLogEntry>());

        Assert.False(failedUndo.Success);
        Assert.True(File.Exists(backupPath!));

        var failedSnapshot = await stateStore.FindLatestSnapshotByGameKeyAsync(game.GameKey);
        Assert.NotNull(failedSnapshot);
        Assert.Equal(SnapshotTransactionStatus.RestoreFailed, failedSnapshot!.Status);
    }

    [Fact]
    public async Task UndoAsync_RepeatedUndo_IsSafeAndIdempotent()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var provider = new TestReleaseAssetProvider(payloadPath, Path.Combine(payloadPath, "OptiPatcher.asi"));
        var service = new InstallationService(appPaths, provider, new InstallStateStore(appPaths));

        var gamePath = Path.Combine(temp.Path, "RepeatedUndoGame");
        Directory.CreateDirectory(gamePath);
        await File.WriteAllTextAsync(Path.Combine(gamePath, "libxess.dll"), "original-libxess");

        var game = CreateGame("Repeated Undo Game", gamePath, InstallPolicy.Supported);
        var installOutcome = await service.InstallAsync(
            game,
            new InstallationRequest { GpuVendor = GpuVendor.Nvidia },
            new Progress<InstallerLogEntry>());

        Assert.True(installOutcome.Success);
        Assert.NotNull(installOutcome.Record);

        var firstUndo = await service.UndoAsync(installOutcome.Record!, new Progress<InstallerLogEntry>());
        var secondUndo = await service.UndoAsync(installOutcome.Record!, new Progress<InstallerLogEntry>());

        Assert.True(firstUndo.Success);
        Assert.True(secondUndo.Success);
        Assert.Equal("original-libxess", await File.ReadAllTextAsync(Path.Combine(gamePath, "libxess.dll")));
    }

    [Fact]
    public async Task RestoreBackupAsync_WorksWithoutInstallsJson()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var provider = new TestReleaseAssetProvider(payloadPath, Path.Combine(payloadPath, "OptiPatcher.asi"));
        var service = new InstallationService(appPaths, provider, new InstallStateStore(appPaths));

        var gamePath = Path.Combine(temp.Path, "RecoveryGame");
        Directory.CreateDirectory(gamePath);
        await File.WriteAllTextAsync(Path.Combine(gamePath, "libxess.dll"), "original-libxess");

        var game = CreateGame("Recovery Game", gamePath, InstallPolicy.Supported);
        var installOutcome = await service.InstallAsync(
            game,
            new InstallationRequest { GpuVendor = GpuVendor.Nvidia },
            new Progress<InstallerLogEntry>());

        Assert.True(installOutcome.Success);
        File.Delete(appPaths.InstallStateFilePath);

        var restoreOutcome = await service.RestoreBackupAsync(game.GameKey, new Progress<InstallerLogEntry>());

        Assert.True(restoreOutcome.Success);
        Assert.Equal("original-libxess", await File.ReadAllTextAsync(Path.Combine(gamePath, "libxess.dll")));
        Assert.False(File.Exists(Path.Combine(gamePath, "OptiScalerInstaller.manifest.json")));
    }

    private static DetectedGame CreateGame(string displayName, string installPath, InstallPolicy policy, bool requiresOptiPatcher = false)
        => new()
        {
            GameKey = $"steam-{Math.Abs(displayName.GetHashCode())}",
            Source = GameSource.Manual,
            DisplayName = displayName,
            InstallPath = installPath,
            ExePath = Path.Combine(installPath, $"{displayName}.exe"),
            SupportStatus = policy switch
            {
                InstallPolicy.Supported => SupportStatus.Supported,
                InstallPolicy.Warn => SupportStatus.Warning,
                InstallPolicy.Blocked => SupportStatus.Blocked,
                _ => SupportStatus.Unsupported,
            },
            ManifestEntry = new SupportedGameEntry
            {
                DisplayName = displayName,
                ExeNames = [$"{displayName}.exe"],
                PreferredProxy = "dxgi.dll",
                FallbackProxies = ["winmm.dll", "version.dll"],
                InstallPolicy = policy,
                RequiresOptiPatcher = requiresOptiPatcher,
            },
        };

    private static string CreatePayload(string rootPath)
    {
        var payloadPath = Path.Combine(rootPath, "payload");
        Directory.CreateDirectory(payloadPath);

        File.WriteAllText(Path.Combine(payloadPath, "OptiScaler.dll"), "opti-dll");
        File.WriteAllText(Path.Combine(payloadPath, "OptiScaler.ini"), "LoadAsiPlugins=auto");
        File.WriteAllText(Path.Combine(payloadPath, "libxess.dll"), "xess");
        File.WriteAllText(Path.Combine(payloadPath, "OptiPatcher.asi"), "opti-patcher");

        var d3d12Path = Path.Combine(payloadPath, "D3D12_Optiscaler");
        Directory.CreateDirectory(d3d12Path);
        File.WriteAllText(Path.Combine(d3d12Path, "D3D12Core.dll"), "d3d12");

        var licensesPath = Path.Combine(payloadPath, "Licenses");
        Directory.CreateDirectory(licensesPath);
        File.WriteAllText(Path.Combine(licensesPath, "DirectX_LICENSE.txt"), "license");

        return payloadPath;
    }

    private sealed class TestReleaseAssetProvider : IReleaseAssetProvider
    {
        private readonly string payloadPath;
        private readonly string pluginPath;

        public TestReleaseAssetProvider(string payloadPath, string pluginPath)
        {
            this.payloadPath = payloadPath;
            this.pluginPath = pluginPath;
        }

        public Task<string> GetOptiPatcherPluginAsync(IProgress<InstallerLogEntry>? progress, CancellationToken cancellationToken = default)
            => Task.FromResult(pluginPath);

        public Task<PreparedReleaseAsset> PrepareLatestStableReleaseAsync(IProgress<InstallerLogEntry>? progress, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreparedReleaseAsset
            {
                Release = new ReleaseAsset
                {
                    TagName = "v-test",
                    AssetName = "OptiScaler_test.7z",
                    DownloadUrl = "https://example.test/OptiScaler_test.7z",
                    PublishedAtUtc = DateTimeOffset.UtcNow,
                },
                ExtractedPath = payloadPath,
            });
    }

    private sealed class ThrowingPluginReleaseAssetProvider : IReleaseAssetProvider
    {
        private readonly string payloadPath;

        public ThrowingPluginReleaseAssetProvider(string payloadPath)
        {
            this.payloadPath = payloadPath;
        }

        public Task<string> GetOptiPatcherPluginAsync(IProgress<InstallerLogEntry>? progress, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Simulated plugin download failure");

        public Task<PreparedReleaseAsset> PrepareLatestStableReleaseAsync(IProgress<InstallerLogEntry>? progress, CancellationToken cancellationToken = default)
            => Task.FromResult(new PreparedReleaseAsset
            {
                Release = new ReleaseAsset
                {
                    TagName = "v-test",
                    AssetName = "OptiScaler_test.7z",
                    DownloadUrl = "https://example.test/OptiScaler_test.7z",
                    PublishedAtUtc = DateTimeOffset.UtcNow,
                },
                ExtractedPath = payloadPath,
            });
    }
}
