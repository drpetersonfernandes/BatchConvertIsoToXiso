using System.Windows;
using BatchConvertIsoToXiso.Interfaces;

namespace BatchConvertIsoToXiso.Services;

public class MessageBoxService : IMessageBoxService
{
    public MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        if (Application.Current is { MainWindow: not null } app)
        {
            return MessageBox.Show(app.MainWindow, message, title, buttons, icon);
        }

        return MessageBox.Show(message, title, buttons, icon);
    }

    public void ShowError(string message)
    {
        Show(message, "Error", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public void ShowWarning(string message, string title)
    {
        Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}