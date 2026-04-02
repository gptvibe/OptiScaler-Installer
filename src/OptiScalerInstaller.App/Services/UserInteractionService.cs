using System.Windows;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Diagnostics;
using System.IO;

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

    public string? PickSaveFile(string title, string suggestedFileName, string filter)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = title,
            FileName = suggestedFileName,
            Filter = filter,
            AddExtension = true,
            OverwritePrompt = true,
        };

        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public bool Confirm(string title, string message)
        => System.Windows.MessageBox.Show(
            message,
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning) == MessageBoxResult.Yes;

    public void CopyText(string text) => System.Windows.Clipboard.SetText(text ?? string.Empty);

    public void OpenFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var target = Directory.Exists(path)
            ? path
            : Path.GetDirectoryName(path);

        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{target}\"",
            UseShellExecute = true,
        });
    }

    public void ShowMessage(string title, string message, MessageBoxImage icon = MessageBoxImage.Information)
        => System.Windows.MessageBox.Show(message, title, MessageBoxButton.OK, icon);
}
