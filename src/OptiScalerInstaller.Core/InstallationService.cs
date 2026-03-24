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

    public async Task<InstallOutcome> InstallAsync(
        DetectedGame game,
        InstallationRequest request,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (game.SupportStatus == SupportStatus.Blocked)
            {
                return InstallOutcome.Failed("This game is blocked from auto-install because it is marked unsafe.");
            }

            if (game.SupportStatus == SupportStatus.Unsupported && !request.ForceUnsupportedInstall)
            {
                return InstallOutcome.Failed("This game is not in the supported catalog.");
            }

            appPaths.EnsureCreated();
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
                return InstallOutcome.Failed("The downloaded OptiScaler package did not contain OptiScaler.dll.");
            }

            var proxyName = SelectProxyName(game, optiScalerSourcePath);
            if (proxyName is null)
            {
                return InstallOutcome.Failed("No safe proxy DLL slot was available in the target folder.");
            }

            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Selected proxy: {proxyName}."));

            var markerPath = Path.Combine(game.InstallPath, "OptiScalerInstaller.manifest.json");
            var backupRoot = Path.Combine(appPaths.BackupPath, game.GameKey, DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss"));
            Directory.CreateDirectory(backupRoot);

            var createdFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var createdDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var backups = new Dictionary<string, FileBackup>(StringComparer.OrdinalIgnoreCase);
            var warnings = new List<string>();

            foreach (var rootFile in RootFiles)
            {
                var sourcePath = Path.Combine(releaseRoot, rootFile);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(game.InstallPath, rootFile);
                await CopyManagedFileAsync(sourcePath, destinationPath, game.InstallPath, backupRoot, backups, createdFiles, cancellationToken);
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
                    game.InstallPath,
                    backupRoot,
                    backups,
                    createdFiles,
                    createdDirectories,
                    cancellationToken);
            }

            var proxyDestinationPath = Path.Combine(game.InstallPath, proxyName);
            await CopyManagedFileAsync(optiScalerSourcePath, proxyDestinationPath, game.InstallPath, backupRoot, backups, createdFiles, cancellationToken);

            var usedOptiPatcher = false;
            if (ShouldInstallOptiPatcher(game, request))
            {
                var pluginSourcePath = await releaseAssetProvider.GetOptiPatcherPluginAsync(progress, cancellationToken);
                var pluginsDirectoryPath = Path.Combine(game.InstallPath, "plugins");
                Directory.CreateDirectory(pluginsDirectoryPath);
                createdDirectories.Add(Path.GetRelativePath(game.InstallPath, pluginsDirectoryPath));

                var pluginDestinationPath = Path.Combine(pluginsDirectoryPath, "OptiPatcher.asi");
                await CopyManagedFileAsync(pluginSourcePath, pluginDestinationPath, game.InstallPath, backupRoot, backups, createdFiles, cancellationToken);
                EnableAsiPlugins(Path.Combine(game.InstallPath, "OptiScaler.ini"));
                usedOptiPatcher = true;
            }

            if (game.SupportStatus == SupportStatus.Warning || request.ForceUnsupportedInstall)
            {
                warnings.Add("This installation is not officially supported.");
            }

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
                CreatedDirectories = createdDirectories.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToList(),
                BackupFiles = backups.Values.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase).ToList(),
                Warnings = warnings,
            };

            await InstallStateStore.SaveMarkerAsync(record, cancellationToken);
            await installStateStore.UpsertAsync(record, cancellationToken);

            progress?.Report(InstallerLogEntry.Create(LogSeverity.Success, $"{game.DisplayName} installed successfully."));
            return InstallOutcome.Succeeded("Installation completed successfully.", record);
        }
        catch (UnauthorizedAccessException)
        {
            return InstallOutcome.Failed("Access denied while writing to the game folder. Try running the app as administrator.");
        }
        catch (Exception exception)
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Error, exception.Message));
            return InstallOutcome.Failed($"Installation failed: {exception.Message}");
        }
    }

    public async Task<InstallOutcome> UndoAsync(
        InstallRecord record,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default)
        => await UndoInternalAsync(record, progress, removeFromState: true, cancellationToken);

    private async Task<InstallOutcome> UndoInternalAsync(
        InstallRecord record,
        IProgress<InstallerLogEntry>? progress,
        bool removeFromState,
        CancellationToken cancellationToken)
    {
        try
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Info, $"Undoing install for {record.DisplayName}..."));

            foreach (var createdFile in record.CreatedFiles
                .OrderByDescending(path => path.Length)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = Path.Combine(record.InstallPath, createdFile);
                if (File.Exists(fullPath))
                {
                    File.Delete(fullPath);
                }
            }

            foreach (var backup in record.BackupFiles.OrderBy(item => item.RelativePath, StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var restorePath = Path.Combine(record.InstallPath, backup.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(restorePath)!);
                File.Copy(backup.BackupPath, restorePath, overwrite: true);
                if (File.Exists(backup.BackupPath))
                {
                    File.Delete(backup.BackupPath);
                }
            }

            foreach (var directory in record.CreatedDirectories
                .OrderByDescending(path => path.Length)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var fullPath = Path.Combine(record.InstallPath, directory);
                if (Directory.Exists(fullPath) &&
                    !Directory.EnumerateFileSystemEntries(fullPath).Any())
                {
                    Directory.Delete(fullPath, recursive: false);
                }
            }

            if (File.Exists(record.MarkerPath))
            {
                File.Delete(record.MarkerPath);
            }

            if (removeFromState)
            {
                await installStateStore.RemoveAsync(record.GameKey, cancellationToken);
            }

            TryDeleteEmptyBackupDirectories(record.BackupFiles);

            progress?.Report(InstallerLogEntry.Create(LogSeverity.Success, $"{record.DisplayName} has been restored."));
            return InstallOutcome.Succeeded("Undo completed successfully.");
        }
        catch (Exception exception)
        {
            progress?.Report(InstallerLogEntry.Create(LogSeverity.Error, exception.Message));
            return InstallOutcome.Failed($"Undo failed: {exception.Message}");
        }
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

    private static async Task CopyManagedDirectoryAsync(
        string sourceDirectoryPath,
        string destinationDirectoryPath,
        string installRoot,
        string backupRoot,
        Dictionary<string, FileBackup> backups,
        HashSet<string> createdFiles,
        HashSet<string> createdDirectories,
        CancellationToken cancellationToken)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(sourceDirectoryPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectoryPath, sourceFile);
            var destinationPath = Path.Combine(destinationDirectoryPath, relativePath);
            createdDirectories.Add(Path.GetRelativePath(installRoot, Path.GetDirectoryName(destinationPath)!));
            await CopyManagedFileAsync(sourceFile, destinationPath, installRoot, backupRoot, backups, createdFiles, cancellationToken);
        }
    }

    private static async Task CopyManagedFileAsync(
        string sourcePath,
        string destinationPath,
        string installRoot,
        string backupRoot,
        Dictionary<string, FileBackup> backups,
        HashSet<string> createdFiles,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var relativePath = Path.GetRelativePath(installRoot, destinationPath);

        if (File.Exists(destinationPath) && !backups.ContainsKey(relativePath))
        {
            var backupPath = Path.Combine(backupRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
            File.Copy(destinationPath, backupPath, overwrite: true);
            backups.Add(relativePath, new FileBackup
            {
                RelativePath = relativePath,
                BackupPath = backupPath,
            });
        }

        await using var source = File.OpenRead(sourcePath);
        await using var destination = File.Create(destinationPath);
        await source.CopyToAsync(destination, cancellationToken);
        createdFiles.Add(relativePath);
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
