using System.Windows.Media;
using OptiScalerInstaller.App.Infrastructure;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.App.ViewModels;

public sealed class DetectedGameItemViewModel : ObservableObject
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

    private bool isSelected;
    private bool forceUnsupportedInstall;
    private InstallRecord? installedRecord;
    private BackupSnapshotManifest? latestSnapshot;

    public DetectedGameItemViewModel(DetectedGame model)
    {
        Model = model;
        isSelected = model.IsSelectedByDefault;
    }

    public DetectedGame Model { get; }

    public string DisplayName => Model.DisplayName;

    public string InstallPath => Model.InstallPath;

    public string ExecutablePath => Model.ExePath ?? "Executable not detected";

    public string SourceLabel => Model.Source == GameSource.Steam ? "Steam" : "Manual";

    public bool IsSteamGame => Model.Source == GameSource.Steam;

    public bool HasInstalledRecord => installedRecord is not null;

    public string ManagedStateText => installedRecord is null
        ? "Not currently managed"
        : $"Managed install from {installedRecord.InstalledAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    public string ReleaseTagText => installedRecord?.ReleaseTag
        ?? latestSnapshot?.ReleaseTag
        ?? "Latest stable (resolved during install)";

    public string ProxyChoiceText => installedRecord?.ProxyName
        ?? latestSnapshot?.ProxyName
        ?? $"{GetProxyPreferenceOrder().FirstOrDefault() ?? "dxgi.dll"} preferred";

    public string SupportNote
    {
        get
        {
            var note = Model.SupportStatus switch
            {
                SupportStatus.Supported => "Catalog-supported for the standard one-click install flow.",
                SupportStatus.Warning => "Catalog-supported with cautions. Review the game folder and notes before installing.",
                SupportStatus.Blocked => "Blocked in the catalog because this title is marked unsafe for automated install.",
                _ when ForceUnsupportedInstall => "Manual override enabled. This install is outside the supported catalog path.",
                _ => "Not officially supported. You can keep it listed, review the details, and opt into a manual override if you want to try it.",
            };

            if (Model.ManifestEntry?.RequiresOptiPatcher == true)
            {
                note += " OptiPatcher is added automatically on AMD and Intel GPUs.";
            }

            if (!string.IsNullOrWhiteSpace(Model.ManifestEntry?.NotesUrl))
            {
                note += $" More info: {Model.ManifestEntry!.NotesUrl}";
            }

            return note;
        }
    }

    public bool CanToggleUnsupportedOverride => Model.SupportStatus == SupportStatus.Unsupported;

    public bool HasSnapshot => latestSnapshot is not null;

    public string SnapshotText => latestSnapshot is null
        ? "No backup snapshot yet."
        : $"{latestSnapshot.Status} · {latestSnapshot.CreatedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";

    public bool IsSelected
    {
        get => isSelected;
        set
        {
            if (SetProperty(ref isSelected, value))
            {
                OnPropertyChanged(nameof(CanInstall));
            }
        }
    }

    public bool ForceUnsupportedInstall
    {
        get => forceUnsupportedInstall;
        set
        {
            if (SetProperty(ref forceUnsupportedInstall, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(CanInstall));
                OnPropertyChanged(nameof(SupportNote));
            }
        }
    }

    public string StatusText
        => Model.SupportStatus switch
        {
            SupportStatus.Supported => "Supported",
            SupportStatus.Warning => "Supported with caution",
            SupportStatus.Blocked => "Blocked",
            _ when ForceUnsupportedInstall => "Manual override",
            _ => "Unsupported",
        };

    public System.Windows.Media.Brush StatusBrush
        => Model.SupportStatus switch
        {
            SupportStatus.Supported => System.Windows.Media.Brushes.MediumSpringGreen,
            SupportStatus.Warning => System.Windows.Media.Brushes.Gold,
            SupportStatus.Blocked => System.Windows.Media.Brushes.OrangeRed,
            _ when ForceUnsupportedInstall => System.Windows.Media.Brushes.DeepSkyBlue,
            _ => System.Windows.Media.Brushes.LightGray,
        };

    public bool CanInstall
        => Model.SupportStatus != SupportStatus.Blocked &&
           (Model.SupportStatus != SupportStatus.Unsupported || ForceUnsupportedInstall);

    public bool CanSelect => Model.SupportStatus != SupportStatus.Blocked;

    public void SyncInstallState(InstallRecord? record, BackupSnapshotManifest? snapshot)
    {
        installedRecord = record;
        latestSnapshot = snapshot;

        OnPropertyChanged(nameof(HasInstalledRecord));
        OnPropertyChanged(nameof(ManagedStateText));
        OnPropertyChanged(nameof(ReleaseTagText));
        OnPropertyChanged(nameof(ProxyChoiceText));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(SnapshotText));
        OnPropertyChanged(nameof(SupportNote));
    }

    private IEnumerable<string> GetProxyPreferenceOrder()
    {
        if (!string.IsNullOrWhiteSpace(Model.ManifestEntry?.PreferredProxy))
        {
            yield return Model.ManifestEntry.PreferredProxy;
        }

        foreach (var proxy in Model.ManifestEntry?.FallbackProxies ?? [])
        {
            if (!string.IsNullOrWhiteSpace(proxy))
            {
                yield return proxy;
            }
        }

        foreach (var proxy in DefaultProxyOrder)
        {
            yield return proxy;
        }
    }
}
