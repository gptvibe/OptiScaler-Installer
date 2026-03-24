using System.Windows;
using System.Windows.Forms;

namespace OptiScalerInstaller.App.Services;

public sealed class UserInteractionService : IUserInteractionService
{
    public string? PickFolder()
    {
        using var dialog = new FolderBrowserDialog
        {
            Description = "Choose a game installation folder",
            UseDescriptionForTitle = true,
            AutoUpgradeEnabled = true,
            ShowNewFolderButton = false,
        };

        return dialog.ShowDialog() == DialogResult.OK ? dialog.SelectedPath : null;
    }

    public bool Confirm(string title, string message)
        => System.Windows.MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void ShowMessage(string title, string message, MessageBoxImage icon = MessageBoxImage.Information)
        => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, icon);
}
