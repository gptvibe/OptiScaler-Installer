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
        InstallStateFilePath = Path.Combine(StatePath, "installs.json");
    }

    public string RootPath { get; }

    public string CachePath { get; }

    public string StatePath { get; }

    public string BackupPath { get; }

    public string InstallStateFilePath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(CachePath);
        Directory.CreateDirectory(StatePath);
        Directory.CreateDirectory(BackupPath);
    }
}
