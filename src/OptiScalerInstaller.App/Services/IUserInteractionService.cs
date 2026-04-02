using System.Windows;

namespace OptiScalerInstaller.App.Services;

public interface IUserInteractionService
{
    string? PickFolder();

    string? PickSaveFile(string title, string suggestedFileName, string filter);

    bool Confirm(string title, string message);

    void CopyText(string text);

    void OpenFolder(string path);

    void ShowMessage(string title, string message, MessageBoxImage icon = MessageBoxImage.Information);
}
