using System.Windows.Media;
using OptiScalerInstaller.App.Infrastructure;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.App.ViewModels;

public sealed class DetectedGameItemViewModel : ObservableObject
{
    private bool isSelected;
    private bool forceUnsupportedInstall;

    public DetectedGameItemViewModel(DetectedGame model)
    {
        Model = model;
        isSelected = model.IsSelectedByDefault;
    }

    public DetectedGame Model { get; }

    public string DisplayName => Model.DisplayName;

    public string InstallPath => Model.InstallPath;

    public string SourceLabel => Model.Source == GameSource.Steam ? "Steam" : "Manual";

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
}
