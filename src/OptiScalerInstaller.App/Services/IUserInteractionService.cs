using System.Windows;

namespace OptiScalerInstaller.App.Services;

public interface IUserInteractionService
{
    string? PickFolder();

    bool Confirm(string title, string message);

    void ShowMessage(string title, string message, MessageBoxImage icon = MessageBoxImage.Information);
}
