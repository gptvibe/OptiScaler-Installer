using System.IO;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.App.ViewModels;

public sealed class BackupSnapshotItemViewModel
{
    public BackupSnapshotItemViewModel(BackupSnapshotManifest manifest)
    {
        Manifest = manifest;
    }

    public BackupSnapshotManifest Manifest { get; }

    public string SnapshotId => Manifest.SnapshotId;

    public string DisplayName => Manifest.DisplayName;

    public string InstallPath => Manifest.InstallPath;

    public string ReleaseTag => Manifest.ReleaseTag;

    public string ProxyName => Manifest.ProxyName;

    public string TransactionRootPath => Manifest.TransactionRootPath;

    public string CreatedText => Manifest.CreatedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string StatusText => Manifest.Status switch
    {
        SnapshotTransactionStatus.Pending => "Pending backup",
        SnapshotTransactionStatus.Applied => "Active backup",
        SnapshotTransactionStatus.RollingBack => "Rolling back",
        SnapshotTransactionStatus.RolledBack => "Rolled back",
        SnapshotTransactionStatus.Restoring => "Restoring",
        SnapshotTransactionStatus.Restored => "Restored",
        SnapshotTransactionStatus.RestoreFailed => "Restore failed",
        SnapshotTransactionStatus.RollbackFailed => "Rollback failed",
        _ => Manifest.Status.ToString(),
    };

    public System.Windows.Media.Brush StatusBrush => Manifest.Status switch
    {
        SnapshotTransactionStatus.Applied => System.Windows.Media.Brushes.MediumSpringGreen,
        SnapshotTransactionStatus.Restored or SnapshotTransactionStatus.RolledBack => System.Windows.Media.Brushes.DeepSkyBlue,
        SnapshotTransactionStatus.Restoring or SnapshotTransactionStatus.RollingBack or SnapshotTransactionStatus.Pending => System.Windows.Media.Brushes.Gold,
        SnapshotTransactionStatus.RestoreFailed or SnapshotTransactionStatus.RollbackFailed => System.Windows.Media.Brushes.OrangeRed,
        _ => System.Windows.Media.Brushes.Gainsboro,
    };

    public string Summary => $"{ReleaseTag} · {ProxyName} · {CreatedText}";

    public bool HasError => !string.IsNullOrWhiteSpace(Manifest.LastError);

    public string ErrorText => Manifest.LastError ?? string.Empty;

    public bool CanRestore => Manifest.Status is not (SnapshotTransactionStatus.Restored or SnapshotTransactionStatus.RolledBack);

    public bool CanDelete => Manifest.Status != SnapshotTransactionStatus.Applied;

    public string OpenFolderPath => Directory.Exists(TransactionRootPath) ? TransactionRootPath : InstallPath;
}
