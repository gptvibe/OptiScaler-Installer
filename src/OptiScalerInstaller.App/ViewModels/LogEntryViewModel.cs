using System.Windows.Media;
using OptiScalerInstaller.Core;

namespace OptiScalerInstaller.App.ViewModels;

public sealed class LogEntryViewModel
{
    public required string Time { get; init; }

    public required string Message { get; init; }

    public required System.Windows.Media.Brush Foreground { get; init; }

    public static LogEntryViewModel FromCore(InstallerLogEntry entry)
        => new()
        {
            Time = entry.Timestamp.ToString("HH:mm:ss"),
            Message = entry.Message,
            Foreground = entry.Severity switch
            {
                LogSeverity.Success => System.Windows.Media.Brushes.MediumSpringGreen,
                LogSeverity.Warning => System.Windows.Media.Brushes.Gold,
                LogSeverity.Error => System.Windows.Media.Brushes.OrangeRed,
                _ => System.Windows.Media.Brushes.Gainsboro,
            },
        };
}
