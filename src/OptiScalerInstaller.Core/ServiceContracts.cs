namespace OptiScalerInstaller.Core;

public interface IGameScannerService
{
    Task<IReadOnlyList<DetectedGame>> ScanGamesAsync(CancellationToken cancellationToken = default);

    Task<DetectedGame> InspectManualFolderAsync(string installPath, CancellationToken cancellationToken = default);
}

public interface IGpuDetectorService
{
    GpuVendor DetectGpuVendor();
}

public interface IInstallationWorkflowService
{
    Task<IReadOnlyList<InstallRecord>> LoadInstalledGamesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupSnapshotManifest>> LoadSnapshotsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackupSnapshotManifest>> LoadRecoverableSnapshotsAsync(CancellationToken cancellationToken = default);

    Task<PreparedReleaseAsset> PrepareLatestStableReleaseAsync(
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default);

    Task<InstallOutcome> InstallAsync(
        DetectedGame game,
        InstallationRequest request,
        PreparedReleaseAsset? preparedRelease = null,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default);

    Task<InstallOutcome> UndoAsync(
        InstallRecord record,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default);

    Task<InstallOutcome> RestoreBackupAsync(
        string gameKey,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default);

    Task<InstallOutcome> RestoreSnapshotAsync(
        string snapshotId,
        IProgress<InstallerLogEntry>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteSnapshotAsync(string snapshotId, CancellationToken cancellationToken = default);
}
