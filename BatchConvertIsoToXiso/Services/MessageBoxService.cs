using System.Windows;

namespace BatchConvertIsoToXiso.Services;

public interface IMessageBoxService
{
    MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon);
    void ShowError(string message);
    void ShowWarning(string message, string title);
}

public class MessageBoxService : IMessageBoxService
{
    public MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage icon)
    {
        if (Application.Current.MainWindow != null)
        {
            return MessageBox.Show(Application.Current.MainWindow, message, title, buttons, icon);
        }

        return MessageBoxResult.None; // Return a default value if MainWindow is null
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