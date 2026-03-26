using System.Security.Cryptography;

namespace OptiScalerInstaller.Core;

public sealed class InstallationService
{
    private static readonly string[] DefaultProxyOrder =
    [
        "dxgi.dll",
        "winmm.dll",
        "version.dll",
        "dbghelp.dll",
        "d3d12.dll",
        "wininet.dll",
        "winhttp.dll",
    ];

    private static readonly string[] RootFiles =
    [
        "OptiScaler.ini",
        "libxess.dll",
        "libxess_dx11.dll",
        "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_framegeneration_dx12.dll",
        "amd_fidelityfx_upscaler_dx12.dll",
        "amd_fidelityfx_vk.dll",
    ];

    private static readonly string[] RootDirectories =
    [
        "D3D12_Optiscaler",
        "DlssOverrides",
        "Licenses",
    ];

    private readonly AppPaths appPaths;
    private readonly IReleaseAssetProvider releaseAssetProvider;
    private readonly InstallStateStore installStateStore;

    public InstallationService(
        AppPaths appPaths,
        IReleaseAssetProvider releaseAssetProvider,
        InstallStateStore installStateStore)
    {
        this.appPaths = appPaths;
        this.releaseAssetProvider = releaseAssetProvider;
        this.installStateStore = installStateStore;
    }

    public async Task<IReadOnlyList<InstallRecord>> LoadInstalledGamesAsync(CancellationToken cancellationToken = default)
        => await installStateStore.LoadAsync(cancellationToken);

    public async Task<IReadOnlyList<BackupSnapshotManifest>> LoadRecoverableSnapshotsAsync(CancellationToken cancellationToken = default)
        => await installStateStore.LoadRecoverableSnapshotsAsync(cancellationToken);

    public async Task<InstallOutcome> InstallAsync(
        DetectedGame game,
        InstallationRequest request,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default)
    {
        BackupSnapshotManifest? snapshot = null;
        try
        {
            if (game.SupportStatus == SupportStatus.Blocked)
            {
                return InstallOutcome.Failed(
                    "This game is blocked from auto-install because it is marked unsafe.",
                    FailureKind.PreflightFailed);
            }

            if (game.SupportStatus == SupportStatus.Unsupported && !request.ForceUnsupportedInstall)
            {
                return InstallOutcome.Failed(
                    "This game is not in the supported catalog.",
                    FailureKind.PreflightFailed);
            }

            appPaths.EnsureCreated();

            var preflightOutcome = RunPreflightChecks(game);
            if (preflightOutcome is not null)
            {
                progress?.Report(InstallerLogEntry.Create(LogSeverity.Error, preflightOutcome.Message));
                return preflightOutcome;
            }

            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Preparing installation for {game.DisplayName}..."));

            var existingRecord = await ResolveExistingRecordAsync(game, cancellationToken);
            if (existingRecord is not null)
            {
                progress?.Report(InstallerLogEntry.Create(LogSeverity.Warning, $"Existing managed install found for {game.DisplayName}; refreshing it first."));
                var undoOutcome = await UndoInternalAsync(existingRecord, progress, removeFromState: true, cancellationToken);
                if (!undoOutcome.Success)
                {
                    return undoOutcome;
                }
            }

            var preparedRelease = await releaseAssetProvider.PrepareLatestStableReleaseAsync(progress, cancellationToken);
            var releaseRoot = preparedRelease.ExtractedPath;
            var optiScalerSourcePath = Path.Combine(releaseRoot, "OptiScaler.dll");
            if (!File.Exists(optiScalerSourcePath))
            {
                return InstallOutcome.Failed(
                    "The downloaded OptiScaler package did not contain OptiScaler.dll.",
                    FailureKind.InstallFailed);
            }

            var proxyName = SelectProxyName(game, optiScalerSourcePath);
            if (proxyName is null)
            {
                return InstallOutcome.Failed(
                    "No safe proxy DLL slot was available in the target folder.",
                    FailureKind.InstallFailed);
            }

            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Selected proxy: {proxyName}."));

            var markerPath = Path.Combine(game.InstallPath, "OptiScalerInstaller.manifest.json");
            var transactionRoot = Path.Combine(appPaths.BackupPath, game.GameKey, DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(transactionRoot);

            snapshot = new BackupSnapshotManifest
            {
                SnapshotId = Guid.NewGuid().ToString("N"),
                GameKey = game.GameKey,
                DisplayName = game.DisplayName,
                InstallPath = game.InstallPath,
                MarkerPath = markerPath,
                ReleaseTag = preparedRelease.Release.TagName,
                ProxyName = proxyName,
                TransactionRootPath = transactionRoot,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastUpdatedAtUtc = DateTimeOffset.UtcNow,
                Status = SnapshotTransactionStatus.Pending,
            };
            await installStateStore.UpsertSnapshotAsync(snapshot, cancellationToken);

            var warnings = new List<string>();

            foreach (var rootFile in RootFiles)
            {
                var sourcePath = Path.Combine(releaseRoot, rootFile);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(game.InstallPath, rootFile);
                await ApplyManagedFileAsync(sourcePath, destinationPath, snapshot, cancellationToken);
            }

            foreach (var rootDirectory in RootDirectories)
            {
                var sourceDirectoryPath = Path.Combine(releaseRoot, rootDirectory);
                if (!Directory.Exists(sourceDirectoryPath))
                {
                    continue;
                }

                await CopyManagedDirectoryAsync(
                    sourceDirectoryPath,
                    Path.Combine(game.InstallPath, rootDirectory),
                    snapshot,
                    cancellationToken);
            }

            var proxyDestinationPath = Path.Combine(game.InstallPath, proxyName);
            await ApplyManagedFileAsync(optiScalerSourcePath, proxyDestinationPath, snapshot, cancellationToken);

            var usedOptiPatcher = false;
            if (ShouldInstallOptiPatcher(game, request))
            {
                var pluginSourcePath = await releaseAssetProvider.GetOptiPatcherPluginAsync(progress, cancellationToken);
                var pluginsDirectoryPath = Path.Combine(game.InstallPath, "plugins");
                Directory.CreateDirectory(pluginsDirectoryPath);
                snapshot.CreatedDirectories.Add(Path.GetRelativePath(game.InstallPath, pluginsDirectoryPath));

                var pluginDestinationPath = Path.Combine(pluginsDirectoryPath, "OptiPatcher.asi");
                await ApplyManagedFileAsync(pluginSourcePath, pluginDestinationPath, snapshot, cancellationToken);
                EnableAsiPlugins(Path.Combine(game.InstallPath, "OptiScaler.ini"));
                usedOptiPatcher = true;
            }

            if (game.SupportStatus == SupportStatus.Warning || request.ForceUnsupportedInstall)
            {
                warnings.Add("This installation is not officially supported.");
            }

            var createdFiles = snapshot.Files
                .Where(file => !file.ReplacedExistingFile)
                .Select(file => file.RelativePath)
                .ToList();
            createdFiles.Add(Path.GetFileName(markerPath));

            var record = new InstallRecord
            {
                GameKey = game.GameKey,
                DisplayName = game.DisplayName,
                InstallPath = game.InstallPath,
                MarkerPath = markerPath,
                ReleaseTag = preparedRelease.Release.TagName,
                ProxyName = proxyName,
                InstalledAtUtc = DateTimeOffset.UtcNow,
                ManualOverride = request.ForceUnsupportedInstall || game.IsManualOverride,
                UsedOptiPatcher = usedOptiPatcher,
                CreatedFiles = createdFiles.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                CreatedDirectories = snapshot.CreatedDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                BackupFiles = snapshot.Files
                    .Where(file => file.ReplacedExistingFile && !string.IsNullOrWhiteSpace(file.BackupPath))
                    .Select(file => new FileBackup
                    {
                        RelativePath = file.RelativePath,
                        BackupPath = file.BackupPath!,
                    })
                    .OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Warnings = warnings,
            };

            await InstallStateStore.SaveMarkerAsync(record, cancellationToken);
            await installStateStore.UpsertAsync(record, cancellationToken);

            snapshot.Status = SnapshotTransactionStatus.Applied;
            snapshot.LastError = null;
            snapshot.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            await installStateStore.UpsertSnapshotAsync(snapshot, cancellationToken);

            progress?.Report(InstallerLogEntry.Create(LogSeverity.Success, $"{game.DisplayName} installed successfully."));
            return InstallOutcome.Succeeded("Installation completed successfully.", record);
        }
        catch (UnauthorizedAccessException ex)
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Error, ex.Message));

            if (snapshot is not null && snapshot.Files.Any(file => file.ReplacedExistingFile))
            {
                await RollbackFailedInstallAsync(snapshot, progress, CancellationToken.None);
            }

            return InstallOutcome.Failed(
                "Access denied while writing to the game folder. Try running the app as administrator.",
                FailureKind.AccessDenied);
        }
        catch (Exception exception)
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Error, exception.Message));

            if (snapshot is not null && snapshot.Files.Any(file => file.ReplacedExistingFile))
            {
                await RollbackFailedInstallAsync(snapshot, progress, CancellationToken.None);
            }

            return InstallOutcome.Failed($"Installation failed: {exception.Message}", FailureKind.InstallFailed);
        }
    }

    public async Task<InstallOutcome> UndoAsync(
        InstallRecord record,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default)
        => await UndoInternalAsync(record, progress, removeFromState: true, cancellationToken);

    public async Task<InstallOutcome> RestoreBackupAsync(
        string gameKey,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await installStateStore.FindLatestSnapshotByGameKeyAsync(gameKey, cancellationToken);
        if (snapshot is null)
        {
            return InstallOutcome.Failed($"No backup snapshot was found for game key '{gameKey}'.", FailureKind.UndoFailed);
        }

        return await RestoreFromSnapshotAsync(snapshot, progress, removeFromState: true, cancellationToken);
    }

    private async Task<InstallOutcome> UndoInternalAsync(
        InstallRecord record,
        IProgress<InstallerLogEntry>? progress,
        bool removeFromState,
        CancellationToken cancellationToken)
    {
        var snapshot = await installStateStore.FindLatestSnapshotByGameKeyAsync(record.GameKey, cancellationToken)
            ?? CreateSyntheticSnapshot(record);

        return await RestoreFromSnapshotAsync(snapshot, progress, removeFromState, cancellationToken);
    }

    private static InstallOutcome? RunPreflightChecks(DetectedGame game)
    {
        // 1. Verify the game folder is writable.
        var probeFile = Path.Combine(game.InstallPath, $".opti-preflight-{Guid.NewGuid():N}");
        try
        {
            File.WriteAllText(probeFile, string.Empty);
            File.Delete(probeFile);
        }
        catch (UnauthorizedAccessException)
        {
            return InstallOutcome.Failed(
                $"The folder '{game.InstallPath}' is not writable. Try running the installer as administrator.",
                FailureKind.PreflightFailed);
        }
        catch (IOException ex)
        {
            return InstallOutcome.Failed(
                $"Cannot write to '{game.InstallPath}': {ex.Message}",
                FailureKind.PreflightFailed);
        }

        // 2. Check for locked files among the ones we will replace.
        var lockedFiles = new List<string>();
        foreach (var fileName in RootFiles)
        {
            var filePath = Path.Combine(game.InstallPath, fileName);
            if (!File.Exists(filePath))
            {
                continue;
            }

            try
            {
                using var probe = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                lockedFiles.Add(fileName);
            }
        }

        if (lockedFiles.Count > 0)
        {
            return InstallOutcome.Failed(
                $"The following files are locked (the game may be running): {string.Join(", ", lockedFiles)}. Close the game and try again.",
                FailureKind.PreflightFailed);
        }

        // 3. Check available disk space (200 MB minimum on the install drive).
        const long MinimumFreeBytes = 200L * 1024 * 1024;
        try
        {
            var root = Path.GetPathRoot(game.InstallPath);
            if (root is not null)
            {
                var drive = new DriveInfo(root);
                if (drive.AvailableFreeSpace < MinimumFreeBytes)
                {
                    return InstallOutcome.Failed(
                        $"Not enough free disk space on '{drive.Name}'. At least 200 MB is required.",
                        FailureKind.PreflightFailed);
                }
            }
        }
        catch (IOException)
        {
            // Non-fatal: skip disk space check if the drive info is unavailable.
        }

        return null;
    }

    private async Task<InstallRecord?> ResolveExistingRecordAsync(DetectedGame game, CancellationToken cancellationToken)
    {
        var fromState = await installStateStore.FindByInstallPathAsync(game.InstallPath, cancellationToken);
        if (fromState is not null)
        {
            return fromState;
        }

        var markerPath = Path.Combine(game.InstallPath, "OptiScalerInstaller.manifest.json");
        return await InstallStateStore.LoadMarkerAsync(markerPath, cancellationToken);
    }

    private async Task<InstallOutcome> RestoreFromSnapshotAsync(
        BackupSnapshotManifest snapshot,
        IProgress<InstallerLogEntry>? progress,
        bool removeFromState,
        CancellationToken cancellationToken)
    {
        var stagingRoot = Path.Combine(appPaths.RestoreStagingPath, snapshot.SnapshotId, Guid.NewGuid().ToString("N"));

        try
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Undoing install for {snapshot.DisplayName}..."));

            snapshot.Status = SnapshotTransactionStatus.Restoring;
            snapshot.LastError = null;
            snapshot.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            await installStateStore.UpsertSnapshotAsync(snapshot, cancellationToken);

            Directory.CreateDirectory(stagingRoot);
            foreach (var replacedFile in snapshot.Files
                .Where(file => file.ReplacedExistingFile)
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(replacedFile.BackupPath))
                {
                    continue;
                }

                if (!File.Exists(replacedFile.BackupPath))
                {
                    var destinationPath = Path.Combine(snapshot.InstallPath, replacedFile.RelativePath);
                    if (File.Exists(destinationPath))
                    {
                        continue;
                    }

                    throw new IOException($"Backup file is missing: {replacedFile.BackupPath}");
                }

                var stagedPath = Path.Combine(stagingRoot, replacedFile.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(stagedPath)!);
                File.Copy(replacedFile.BackupPath, stagedPath, overwrite: true);
            }

            foreach (var createdFile in snapshot.Files
                .Where(file => !file.ReplacedExistingFile)
                .Select(file => file.RelativePath)
                .OrderByDescending(path => path.Length)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.Combine(snapshot.InstallPath, createdFile);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }

            foreach (var replacedFile in snapshot.Files
                .Where(file => file.ReplacedExistingFile)
                .OrderBy(file => file.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var stagedPath = Path.Combine(stagingRoot, replacedFile.RelativePath);
                if (!File.Exists(stagedPath))
                {
                    continue;
                }

                var restorePath = Path.Combine(snapshot.InstallPath, replacedFile.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
                File.Copy(stagedPath, restorePath, overwrite: true);
            }

            foreach (var directory in snapshot.CreatedDirectories
                .OrderByDescending(path => path.Length)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.Combine(snapshot.InstallPath, directory);
                if (Directory.Exists(fullPath) &&
                    !Directory.EnumerateFileSystemEntries(fullPath).Any())
                {
                    Directory.Delete(fullPath, recursive: false);
                }
            }

            if (File.Exists(snapshot.MarkerPath))
            {
                File.Delete(snapshot.MarkerPath);
            }

            if (removeFromState)
            {
                await installStateStore.RemoveAsync(snapshot.GameKey, cancellationToken);
            }

            foreach (var replacedFile in snapshot.Files.Where(file => file.ReplacedExistingFile))
            {
                if (!string.IsNullOrWhiteSpace(replacedFile.BackupPath) && File.Exists(replacedFile.BackupPath))
                {
                    File.Delete(replacedFile.BackupPath);
                }
            }

            TryDeleteEmptyBackupDirectories(snapshot.Files
                .Where(file => file.ReplacedExistingFile && !string.IsNullOrWhiteSpace(file.BackupPath))
                .Select(file => new FileBackup
                {
                    RelativePath = file.RelativePath,
                    BackupPath = file.BackupPath!,
                }));

            snapshot.Status = SnapshotTransactionStatus.Restored;
            snapshot.LastError = null;
            snapshot.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            await installStateStore.UpsertSnapshotAsync(snapshot, cancellationToken);

            progress?.Report(InstallerLogEntry.Create(LogSeverity.Success, $"{snapshot.DisplayName} has been restored."));
            return InstallOutcome.Succeeded("Undo completed successfully.");
        }
        catch (Exception exception)
        {
            snapshot.Status = SnapshotTransactionStatus.RestoreFailed;
            snapshot.LastError = exception.Message;
            snapshot.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            await installStateStore.UpsertSnapshotAsync(snapshot, CancellationToken.None);

            progress?.Report(InstallerLogEntry.Create(LogSeverity.Error, exception.Message));
            return InstallOutcome.Failed($"Undo failed: {exception.Message}", FailureKind.UndoFailed);
        }
        finally
        {
            if (Directory.Exists(stagingRoot))
            {
                try
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
    }

    private async Task RollbackFailedInstallAsync(
        BackupSnapshotManifest snapshot,
        IProgress<InstallerLogEntry>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            snapshot.Status = SnapshotTransactionStatus.RollingBack;
            snapshot.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            await installStateStore.UpsertSnapshotAsync(snapshot, cancellationToken);

            var outcome = await RestoreFromSnapshotAsync(snapshot, progress, removeFromState: false, cancellationToken);
            if (!outcome.Success)
            {
                throw new InvalidOperationException(outcome.Message);
            }

            snapshot.Status = SnapshotTransactionStatus.RolledBack;
            snapshot.LastError = null;
            snapshot.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            await installStateStore.UpsertSnapshotAsync(snapshot, cancellationToken);
        }
        catch (Exception exception)
        {
            snapshot.Status = SnapshotTransactionStatus.RollbackFailed;
            snapshot.LastError = exception.Message;
            snapshot.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
            await installStateStore.UpsertSnapshotAsync(snapshot, cancellationToken);
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Error, $"Rollback failed: {exception.Message}"));
        }
    }

    private string? SelectProxyName(DetectedGame game, string sourceOptiScalerPath)
    {
        var orderedCandidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(game.ManifestEntry?.PreferredProxy))
        {
            orderedCandidates.Add(game.ManifestEntry.PreferredProxy);
        }

        orderedCandidates.AddRange(game.ManifestEntry?.FallbackProxies ?? []);
        orderedCandidates.AddRange(DefaultProxyOrder);

        foreach (var candidate in orderedCandidates
            .Where(candidate => !string.IsNullOrWhiteSpace(candidate))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var destinationPath = Path.Combine(game.InstallPath, candidate);
            if (!File.Exists(destinationPath))
            {
                return candidate;
            }

            if (FilesMatch(sourceOptiScalerPath, destinationPath))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task CopyManagedDirectoryAsync(
        string sourceDirectoryPath,
        string destinationDirectoryPath,
        BackupSnapshotManifest snapshot,
        CancellationToken cancellationToken)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectoryPath, sourceFile);
            var destinationPath = Path.Combine(destinationDirectoryPath, relativePath);
            var relativeDirectory = Path.GetRelativePath(snapshot.InstallPath, Path.GetDirectoryName(destinationPath)!);
            if (!snapshot.CreatedDirectories.Contains(relativeDirectory, StringComparer.OrdinalIgnoreCase))
            {
                snapshot.CreatedDirectories.Add(relativeDirectory);
            }

            await ApplyManagedFileAsync(sourceFile, destinationPath, snapshot, cancellationToken);
        }
    }

    private async Task ApplyManagedFileAsync(
        string sourcePath,
        string destinationPath,
        BackupSnapshotManifest snapshot,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var relativePath = Path.GetRelativePath(snapshot.InstallPath, destinationPath);
        var existingRecord = snapshot.Files.FirstOrDefault(file =>
            string.Equals(file.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));

        string? backupPath = existingRecord?.BackupPath;
        long? originalSize = existingRecord?.OriginalFileSizeBytes;
        string? originalHash = existingRecord?.OriginalFileSha256;

        if (File.Exists(destinationPath) && string.IsNullOrWhiteSpace(backupPath))
        {
            backupPath = Path.Combine(snapshot.TransactionRootPath, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(destinationPath, backupPath, overwrite: true);

            var backupInfo = new FileInfo(backupPath);
            originalSize = backupInfo.Length;
            originalHash = ComputeSha256(backupPath);
        }

        await using (var source = File.OpenRead(sourcePath))
        await using (var destination = File.Create(destinationPath))
        {
            await source.CopyToAsync(destination, cancellationToken);
        }

        var destinationInfo = new FileInfo(destinationPath);
        var entry = new SnapshotFileRecord
        {
            RelativePath = relativePath,
            TransactionPath = destinationPath,
            BackupPath = backupPath,
            ReplacedExistingFile = !string.IsNullOrWhiteSpace(backupPath),
            InstalledFileSizeBytes = destinationInfo.Length,
            InstalledFileSha256 = ComputeSha256(destinationPath),
            OriginalFileSizeBytes = originalSize,
            OriginalFileSha256 = originalHash,
            InstalledAtUtc = DateTimeOffset.UtcNow,
        };

        if (existingRecord is not null)
        {
            snapshot.Files.Remove(existingRecord);
        }

        snapshot.Files.Add(entry);
        snapshot.LastUpdatedAtUtc = DateTimeOffset.UtcNow;
        await installStateStore.UpsertSnapshotAsync(snapshot, cancellationToken);
    }

    private static void EnableAsiPlugins(string iniPath)
    {
        if (!File.Exists(iniPath))
        {
            return;
        }

        var content = File.ReadAllText(iniPath);
        if (content.Contains("LoadAsiPlugins=auto", StringComparison.OrdinalIgnoreCase))
        {
            content = content.Replace("LoadAsiPlugins=auto", "LoadAsiPlugins=true", StringComparison.OrdinalIgnoreCase);
        }
        else if (!content.Contains("LoadAsiPlugins=true", StringComparison.OrdinalIgnoreCase))
        {
            content = $"{content}{Environment.NewLine}LoadAsiPlugins=true{Environment.NewLine}";
        }

        File.WriteAllText(iniPath, content);
    }

    private static bool ShouldInstallOptiPatcher(DetectedGame game, InstallationRequest request)
        => request.GpuVendor is GpuVendor.Amd or GpuVendor.Intel &&
           game.ManifestEntry?.RequiresOptiPatcher == true &&
           !request.ForceUnsupportedInstall;

    private static BackupSnapshotManifest CreateSyntheticSnapshot(InstallRecord record)
    {
        var transactionRoot = record.BackupFiles
            .Select(backup => Path.GetDirectoryName(backup.BackupPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .OrderBy(path => path.Length)
            .FirstOrDefault() ?? Path.Combine(record.InstallPath, ".opti-synthetic-backup");

        var files = new List<SnapshotFileRecord>();
        foreach (var createdFile in record.CreatedFiles
            .Where(path => !string.Equals(path, Path.GetFileName(record.MarkerPath), StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            files.Add(new SnapshotFileRecord
            {
                RelativePath = createdFile,
                TransactionPath = Path.Combine(record.InstallPath, createdFile),
                ReplacedExistingFile = false,
                InstalledFileSizeBytes = 0,
                InstalledFileSha256 = string.Empty,
                InstalledAtUtc = record.InstalledAtUtc,
            });
        }

        foreach (var backup in record.BackupFiles.DistinctBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
        {
            files.Add(new SnapshotFileRecord
            {
                RelativePath = backup.RelativePath,
                TransactionPath = Path.Combine(record.InstallPath, backup.RelativePath),
                BackupPath = backup.BackupPath,
                ReplacedExistingFile = true,
                InstalledFileSizeBytes = 0,
                InstalledFileSha256 = string.Empty,
                InstalledAtUtc = record.InstalledAtUtc,
            });
        }

        return new BackupSnapshotManifest
        {
            SnapshotId = $"synthetic-{record.GameKey}-{record.InstalledAtUtc.UtcDateTime.Ticks}",
            GameKey = record.GameKey,
            DisplayName = record.DisplayName,
            InstallPath = record.InstallPath,
            MarkerPath = record.MarkerPath,
            ReleaseTag = record.ReleaseTag,
            ProxyName = record.ProxyName,
            TransactionRootPath = transactionRoot,
            CreatedAtUtc = record.InstalledAtUtc,
            LastUpdatedAtUtc = DateTimeOffset.UtcNow,
            Status = SnapshotTransactionStatus.Applied,
            CreatedDirectories = record.CreatedDirectories.ToList(),
            Files = files,
        };
    }

    private static string ComputeSha256(string filePath)
    {
        using var hash = SHA256.Create();
        using var stream = File.OpenRead(filePath);
        var bytes = hash.ComputeHash(stream);
        return Convert.ToHexString(bytes);
    }

    private static bool FilesMatch(string firstPath, string secondPath)
    {
        using var firstHash = SHA256.Create();
        using var secondHash = SHA256.Create();
        using var firstStream = File.OpenRead(firstPath);
        using var secondStream = File.OpenRead(secondPath);

        var left = firstHash.ComputeHash(firstStream);
        var right = secondHash.ComputeHash(secondStream);
        return left.AsSpan().SequenceEqual(right);
    }

    private static void TryDeleteEmptyBackupDirectories(IEnumerable<FileBackup> backups)
    {
        foreach (var directoryPath in backups
            .Select(backup => Path.GetDirectoryName(backup.BackupPath))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(path => path!.Length))
        {
            if (directoryPath is null || !Directory.Exists(directoryPath))
            {
                continue;
            }

            if (!Directory.EnumerateFileSystemEntries(directoryPath).Any())
            {
                Directory.Delete(directoryPath, recursive: false);
            }
        }
    }
}
