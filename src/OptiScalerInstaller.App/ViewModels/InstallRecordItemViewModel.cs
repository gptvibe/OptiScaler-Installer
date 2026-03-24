using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.App.ViewModels;

public sealed class InstallRecordItemViewModel
{
    public InstallRecordItemViewModel(InstallRecord record)
    {
        Record = record;
    }

    public InstallRecord Record { get; }

    public string DisplayName => Record.DisplayName;

    public string InstallPath => Record.InstallPath;

    public string Details
        => $"{Record.ReleaseTag} · {Record.ProxyName} · {Record.InstalledAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
}
