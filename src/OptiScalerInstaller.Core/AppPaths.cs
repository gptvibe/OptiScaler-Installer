namespace OptiScalerInstaller.Core;

public sealed class AppPaths
{
    public AppPaths(string? rootPath = null)
    {
        RootPath = rootPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OptiScalerInstaller");

        CachePath = Path.Combine(RootPath, "cache");
        StatePath = Path.Combine(RootPath, "state");
        BackupPath = Path.Combine(RootPath, "backups");
        LogsPath = Path.Combine(RootPath, "logs");
        InstallStateFilePath = Path.Combine(StatePath, "installs.json");
        SnapshotStateFilePath = Path.Combine(StatePath, "backup-snapshots.json");
        RestoreStagingPath = Path.Combine(StatePath, "restore-staging");
    }

    public string RootPath { get; }

    public string CachePath { get; }

    public string StatePath { get; }

    public string BackupPath { get; }

    public string LogsPath { get; }

    public string InstallStateFilePath { get; }

    public string SnapshotStateFilePath { get; }

    public string RestoreStagingPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(StatePath);
        Directory.CreateDirectory(BackupPath);
        Directory.CreateDirectory(LogsPath);
        Directory.CreateDirectory(RestoreStagingPath);
    }
}
