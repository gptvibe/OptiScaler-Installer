using System.Net;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.Tests;

/// <summary>
/// Tests covering: atomic writes, corrupted state recovery, offline cache fallback,
/// and preflight failure kinds.
/// </summary>
public sealed class ResilienceTests
{
    // ── Atomic writes ────────────────────────────────────────────────────────

    [Fact]
    public async Task InstallStateStore_AtomicWrite_LeavesNoTempFile()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var store = new InstallStateStore(appPaths);

        await store.UpsertAsync(MinimalRecord("steam-atomictest", temp.Path));

        Assert.True(File.Exists(appPaths.InstallStateFilePath));
        Assert.False(File.Exists(appPaths.InstallStateFilePath + ".tmp"),
            "A stale .tmp file should not exist after a successful save.");
    }

    [Fact]
    public async Task SaveMarkerAsync_AtomicWrite_LeavesNoTempFile()
    {
        using var temp = new TemporaryDirectory();
        var gamePath = Path.Combine(temp.Path, "game");
        Directory.CreateDirectory(gamePath);
        var markerPath = Path.Combine(gamePath, "OptiScalerInstaller.manifest.json");

        var record = new InstallRecord
        {
            GameKey = "steam-markertest",
            DisplayName = "Marker Test",
            InstallPath = gamePath,
            MarkerPath = markerPath,
            ReleaseTag = "v-test",
            ProxyName = "dxgi.dll",
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };

        await InstallStateStore.SaveMarkerAsync(record);

        Assert.True(File.Exists(markerPath));
        Assert.False(File.Exists(markerPath + ".tmp"),
            "A stale .tmp file should not exist after a successful marker save.");
    }

    // ── Corrupted state recovery ─────────────────────────────────────────────

    [Fact]
    public async Task InstallStateStore_CorruptedJson_RecoversGracefully()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        appPaths.EnsureCreated();

        await File.WriteAllTextAsync(appPaths.InstallStateFilePath, "{ this is not valid json }");

        var store = new InstallStateStore(appPaths);
        var records = await store.LoadAsync();

        Assert.Empty(records);
        Assert.True(File.Exists(appPaths.InstallStateFilePath + ".corrupted"),
            "The corrupted file should have been backed up with a .corrupted extension.");
        Assert.False(File.Exists(appPaths.InstallStateFilePath),
            "The corrupted file should have been removed so recovery can proceed cleanly.");
    }

    [Fact]
    public async Task InstallStateStore_CorruptedJson_CanUpsertAfterRecovery()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        appPaths.EnsureCreated();

        await File.WriteAllTextAsync(appPaths.InstallStateFilePath, "not json at all");

        var store = new InstallStateStore(appPaths);
        var record = MinimalRecord("steam-recovery", temp.Path);
        await store.UpsertAsync(record);

        var loaded = await store.LoadAsync();
        Assert.Single(loaded);
        Assert.Equal("steam-recovery", loaded[0].GameKey);
    }

    // ── Offline cache fallback ───────────────────────────────────────────────

    [Fact]
    public async Task PrepareLatestStable_OfflineFallback_UsesCachedRelease()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        appPaths.EnsureCreated();

        // Create a fake prepared cache directory.
        const string cachedTag = "v99.0.0-offline";
        var cacheDir = Path.Combine(appPaths.CachePath, cachedTag);
        var extractedDir = Path.Combine(cacheDir, "extracted");
        Directory.CreateDirectory(extractedDir);
        await File.WriteAllTextAsync(Path.Combine(extractedDir, ".prepared"), cachedTag);
        await File.WriteAllTextAsync(Path.Combine(extractedDir, "OptiScaler.dll"), "dll-bytes");

        var failingClient = new HttpClient(new AlwaysFailMessageHandler());
        var provider = new GitHubReleaseAssetProvider(appPaths, failingClient);

        var logs = new List<InstallerLogEntry>();
        var result = await provider.PrepareLatestStableReleaseAsync(new Progress<InstallerLogEntry>(logs.Add));

        Assert.Equal(cachedTag, result.Release.TagName);
        Assert.True(Directory.Exists(result.ExtractedPath));
        Assert.Contains(logs, entry =>
            entry.Severity == LogSeverity.Warning &&
            entry.Message.Contains("Offline mode", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PrepareLatestStable_OfflineFallback_ThrowsWhenNoCacheExists()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        appPaths.EnsureCreated();

        var failingClient = new HttpClient(new AlwaysFailMessageHandler());
        var provider = new GitHubReleaseAssetProvider(appPaths, failingClient);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.PrepareLatestStableReleaseAsync(null));
    }

    // ── Preflight failures ───────────────────────────────────────────────────

    [Fact]
    public async Task InstallAsync_LockedFile_FailsWithPreflightKind()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var game = CreateTestGame("LockTest", Path.Combine(temp.Path, "LockGame"), InstallPolicy.Supported);
        Directory.CreateDirectory(game.InstallPath);

        // Write and lock one of the RootFiles.
        var lockedFilePath = Path.Combine(game.InstallPath, "libxess.dll");
        await File.WriteAllTextAsync(lockedFilePath, "original");

        using var lockStream = new FileStream(lockedFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var provider = new TestReleaseProvider(payloadPath);
        var service = new InstallationService(appPaths, provider, new InstallStateStore(appPaths));
        var outcome = await service.InstallAsync(game, new InstallationRequest { GpuVendor = GpuVendor.Nvidia });

        Assert.False(outcome.Success);
        Assert.Equal(FailureKind.PreflightFailed, outcome.FailureKind);
        Assert.Contains("locked", outcome.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task InstallAsync_BlockedGame_FailsWithPreflightKind()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var game = CreateTestGame("BlockedGame", Path.Combine(temp.Path, "Blocked"), InstallPolicy.Blocked);
        Directory.CreateDirectory(game.InstallPath);

        var provider = new TestReleaseProvider(payloadPath);
        var service = new InstallationService(appPaths, provider, new InstallStateStore(appPaths));
        var outcome = await service.InstallAsync(game, new InstallationRequest { GpuVendor = GpuVendor.Nvidia });

        Assert.False(outcome.Success);
        Assert.Equal(FailureKind.PreflightFailed, outcome.FailureKind);
    }

    [Fact]
    public async Task InstallAsync_UnsupportedWithoutForce_FailsWithPreflightKind()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));
        var payloadPath = CreatePayload(temp.Path);
        var game = CreateTestGame("UnsupGame", Path.Combine(temp.Path, "Unsup"), InstallPolicy.Blocked);
        Directory.CreateDirectory(game.InstallPath);

        var provider = new TestReleaseProvider(payloadPath);
        var service = new InstallationService(appPaths, provider, new InstallStateStore(appPaths));
        var unsupportedGame = new DetectedGame
        {
            GameKey = game.GameKey,
            Source = game.Source,
            DisplayName = game.DisplayName,
            InstallPath = game.InstallPath,
            ExePath = game.ExePath,
            SupportStatus = SupportStatus.Unsupported,
            ManifestEntry = game.ManifestEntry,
            IsManualOverride = game.IsManualOverride,
        };
        var outcome = await service.InstallAsync(
            unsupportedGame,
            new InstallationRequest { GpuVendor = GpuVendor.Nvidia, ForceUnsupportedInstall = false });

        Assert.False(outcome.Success);
        Assert.Equal(FailureKind.PreflightFailed, outcome.FailureKind);
    }

    // ── RunLogger ────────────────────────────────────────────────────────────

    [Fact]
    public void RunLogger_CreatesLogFileAndWritesEntries()
    {
        using var temp = new TemporaryDirectory();
        var appPaths = new AppPaths(Path.Combine(temp.Path, "appdata"));

        using var logger = new RunLogger(appPaths);
        logger.Log(InstallerLogEntry.Create(LogSeverity.Info, "test message"));
        logger.Dispose();

        Assert.True(File.Exists(logger.LogFilePath));
        var contents = File.ReadAllText(logger.LogFilePath);
        Assert.Contains("test message", contents);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static InstallRecord MinimalRecord(string gameKey, string tempRoot)
        => new()
        {
            GameKey = gameKey,
            DisplayName = "Test",
            InstallPath = Path.Combine(tempRoot, "game"),
            MarkerPath = Path.Combine(tempRoot, "game", "OptiScalerInstaller.manifest.json"),
            ReleaseTag = "v-test",
            ProxyName = "dxgi.dll",
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };

    private static DetectedGame CreateTestGame(string name, string installPath, InstallPolicy policy)
        => new()
        {
            GameKey = $"test-{name.ToLowerInvariant()}",
            Source = GameSource.Manual,
            DisplayName = name,
            InstallPath = installPath,
            ExePath = Path.Combine(installPath, $"{name}.exe"),
            SupportStatus = policy switch
            {
                InstallPolicy.Supported => SupportStatus.Supported,
                InstallPolicy.Warn => SupportStatus.Warning,
                InstallPolicy.Blocked => SupportStatus.Blocked,
                _ => SupportStatus.Unsupported,
            },
            ManifestEntry = new SupportedGameEntry
            {
                DisplayName = name,
                ExeNames = [$"{name}.exe"],
                PreferredProxy = "dxgi.dll",
                InstallPolicy = policy,
            },
        };

    private static string CreatePayload(string rootPath)
    {
        var payloadPath = Path.Combine(rootPath, "payload-resilience");
        Directory.CreateDirectory(payloadPath);
        File.WriteAllText(Path.Combine(payloadPath, "OptiScaler.dll"), "opti-dll");
        File.WriteAllText(Path.Combine(payloadPath, "OptiScaler.ini"), "LoadAsiPlugins=auto");
        File.WriteAllText(Path.Combine(payloadPath, "libxess.dll"), "xess");
        File.WriteAllText(Path.Combine(payloadPath, "OptiPatcher.asi"), "opti-patcher");
        return payloadPath;
    }

    private sealed class AlwaysFailMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => throw new HttpRequestException("Simulated network failure");
    }

    private sealed class TestReleaseProvider : IReleaseAssetProvider
    {
        private readonly string payloadPath;

        public TestReleaseProvider(string payloadPath) => this.payloadPath = payloadPath;

        public Task<PreparedReleaseAsset> PrepareLatestStableReleaseAsync(
            IProgress<InstallerLogEntry>? progress,
            CancellationToken cancellationToken = default)
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

        public Task<string> GetOptiPatcherPluginAsync(
            IProgress<InstallerLogEntry>? progress,
            CancellationToken cancellationToken = default)
            => Task.FromResult(Path.Combine(payloadPath, "OptiPatcher.asi"));
    }
}
